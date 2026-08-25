using System.Net;
using LanManagement.Server.Data;
using LanManagement.Server.Options;
using LanManagement.Server.Security;
using LanManagement.Server.Services;
using LanManagement.Server.Transport;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var serverConfiguration = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var securityConfiguration = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
if (serverConfiguration.UseHttps && string.IsNullOrWhiteSpace(serverConfiguration.CertificatePath))
{
    throw new InvalidOperationException("Server:UseHttps is enabled but Server:CertificatePath is not configured.");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    var address = serverConfiguration.ListenAddress.Trim();
    var ip = address switch
    {
        "0.0.0.0" or "*" => IPAddress.Any,
        "::" => IPAddress.IPv6Any,
        _ when IPAddress.TryParse(address, out var parsed) => parsed,
        _ => throw new InvalidOperationException("Server:ListenAddress must be an IP address, 0.0.0.0, or ::.")
    };
    kestrel.Listen(ip, serverConfiguration.Port, listen =>
    {
        if (serverConfiguration.UseHttps)
        {
            listen.UseHttps(serverConfiguration.CertificatePath, serverConfiguration.CertificatePassword);
        }
    });
});

builder.Services.AddOptions<ServerOptions>().Bind(builder.Configuration.GetSection("Server")).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().Bind(builder.Configuration.GetSection("Security")).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<BootstrapAdminOptions>().Bind(builder.Configuration.GetSection("BootstrapAdmin"));

var connectionString = EnsureDatabaseDirectory(builder.Configuration.GetConnectionString("ManagementDatabase") ?? "Data Source=data/tempy-management.db;Cache=Shared;Foreign Keys=True;Default Timeout=30", builder.Environment.ContentRootPath);
builder.Services.AddDbContextFactory<ManagementDbContext>(options => options.UseSqlite(connectionString));

var keyDirectory = Path.Combine(builder.Environment.ContentRootPath, "data", "keys");
Directory.CreateDirectory(keyDirectory);
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)).SetApplicationName("Tempy.Management.Server");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.Cookie.Name = "TempyAdmin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = serverConfiguration.UseHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(builder.Configuration.GetValue<int?>("Security:CookieMinutes") ?? 480);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect("/");
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-TEMPY-CSRF";
    options.Cookie.Name = "TempyCsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = serverConfiguration.UseHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 8;
        limiter.Window = TimeSpan.FromMinutes(5);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.WriteIndented = false;
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPasswordService, Pbkdf2PasswordService>();
builder.Services.AddSingleton<IAgentTokenHasher, AgentTokenHasher>();
builder.Services.AddSingleton<IAgentAuthenticationService, AgentAuthenticationService>();
builder.Services.AddSingleton<IAdminUserService, AdminUserService>();
builder.Services.AddSingleton<IDashboardEventBus, DashboardEventBus>();
builder.Services.AddSingleton<IAgentConnectionManager, AgentConnectionManager>();
builder.Services.AddSingleton<IAgentSessionRunner, AgentSessionRunner>();
builder.Services.AddSingleton<CommandWorkSignal>();
builder.Services.AddSingleton<SyncWorkSignal>();
builder.Services.AddSingleton<ICommandWorkSignal>(provider => provider.GetRequiredService<CommandWorkSignal>());
builder.Services.AddSingleton<ISyncWorkSignal>(provider => provider.GetRequiredService<SyncWorkSignal>());
builder.Services.AddSingleton<ICommandService, CommandService>();
builder.Services.AddSingleton<IPolicyService, PolicyService>();
builder.Services.AddSingleton<IDeviceService, DeviceService>();

builder.Services.AddHostedService<DatabaseBootstrapHostedService>();
builder.Services.AddHostedService<RecoveryService>();
builder.Services.AddHostedService<PresenceMonitorService>();
builder.Services.AddHostedService<CommandDispatcherService>();
builder.Services.AddHostedService<SyncDispatcherService>();
builder.Services.AddHostedService<CommandTimeoutMonitorService>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
if (!serverConfiguration.UseHttps)
{
    logger.LogWarning("Server is running without TLS. Use a LAN firewall and enable Server:UseHttps with a trusted certificate before production deployment.");
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    loggerFactory.CreateLogger("Unhandled").LogError(feature?.Error, "Unhandled request error.");
    if (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"The server could not process the request.\"}");
    }
}));
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Same-origin cookie APIs require an antiforgery header for every state-changing browser request.
app.Use(async (context, next) =>
{
    if (securityConfiguration.RequireHttpsForAdmin && context.Request.Path.StartsWithSegments("/api") && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "HTTPS is required for the administrator API." });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        try
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing CSRF token." });
            return;
        }
    }
    await next();
});

app.Map("/agent/ws", AgentWebSocketEndpoint.HandleAsync);
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol_version = LanManagement.Server.Protocol.ProtocolConstants.Version })).AllowAnonymous();
app.MapControllers();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers["Cache-Control"] = "no-store";
        context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
});
app.MapFallbackToFile("index.html");

app.Run();

static string EnsureDatabaseDirectory(string configuredConnectionString, string contentRoot)
{
    var builder = new SqliteConnectionStringBuilder(configuredConnectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
    {
        return builder.ToString();
    }
    if (!Path.IsPathRooted(builder.DataSource))
    {
        builder.DataSource = Path.GetFullPath(builder.DataSource, contentRoot);
    }
    var directory = Path.GetDirectoryName(builder.DataSource);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }
    return builder.ToString();
}

public partial class Program
{
}
