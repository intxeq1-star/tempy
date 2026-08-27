using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OMClientAgent.Core;
using OMClientAgent.Diagnostics;
using OMClientAgent.Infrastructure;
using OMClientAgent.Services;

namespace OMClientAgent;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var diagnostic = args.Any(a => a.Equals("--diagnostic", StringComparison.OrdinalIgnoreCase));
        var configuration = BuildConfiguration(args);

        if (diagnostic)
            return await RunDiagnosticsAsync(configuration, args).ConfigureAwait(false);

        return await RunServiceAsync(configuration, args).ConfigureAwait(false);
    }

    private static IConfiguration BuildConfiguration(string[] args)
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("OM_")
            .AddCommandLine(args)
            .Build();
    }

    private static async Task<int> RunDiagnosticsAsync(IConfiguration configuration, string[] args)
    {
        var services = BuildServices(configuration);
        await using var provider = services.BuildServiceProvider();
        EnsureDataDirectory(provider);

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("OMClientAgent.Diag");
        logger.LogInformation("Running OM Client Agent connectivity diagnostics...");

        var diagnostics = provider.GetRequiredService<ConnectivityDiagnostics>();
        var stages = await diagnostics.RunAsync(CancellationToken.None).ConfigureAwait(false);

        var text = ConnectivityDiagnostics.ToText(stages);
        Console.WriteLine(text);

        var allOk = stages.All(s => s.Ok);
        logger.LogInformation("Diagnostics complete. All stages OK = {AllOk}.", allOk);
        return allOk ? 0 : 2;
    }

    private static async Task<int> RunServiceAsync(IConfiguration configuration, string[] args)
    {
        var hostBuilder = Host.CreateApplicationBuilder(args);
        hostBuilder.Configuration.AddConfiguration(configuration);
        ConfigureLogging(hostBuilder.Logging);

        if (OperatingSystem.IsWindows())
        {
            hostBuilder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "OMClientAgent";
            });
        }

        ConfigureServices(hostBuilder.Services, hostBuilder.Configuration);
        var host = hostBuilder.Build();
        EnsureDataDirectory(host.Services);

        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("OMClientAgent.Host");
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "OM Client Agent failed to run.");
            return 1;
        }
    }

    private static IServiceCollection BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
        });
        ConfigureServices(services, configuration);
        return services;
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var configService = new AgentConfigService(configuration, NullLogger<AgentConfigService>.Instance);
        services.AddSingleton(configService);
        services.AddSingleton<OmEvents>();
        services.AddSingleton(sp => new LocalDatabase(
            Path.Combine(ResolveDataDirectory(configService), "data.db"),
            sp.GetRequiredService<ILogger<LocalDatabase>>()));

        services.AddSingleton(sp => new MachineIdentityProvider(
            sp.GetRequiredService<ILogger<MachineIdentityProvider>>(),
            sp.GetRequiredService<LocalDatabase>(),
            OmProtocol.Version));

        services.AddSingleton<TlsTrust>();
        services.AddSingleton<AuthenticationManager>();
        services.AddSingleton<UserSessionManager>();
        services.AddSingleton<CommandExecutor>();
        services.AddSingleton<ApplicationControlManager>();
        services.AddSingleton<PolicyManager>();
        services.AddSingleton<SoftwareManager>();
        services.AddSingleton<DnsManager>();
        services.AddSingleton<CommunicationManager>();
        services.AddSingleton<IResultReporter>(sp => sp.GetRequiredService<CommunicationManager>());
        services.AddSingleton<HealthManager>();
        services.AddSingleton<SynchronizationManager>();
        services.AddSingleton<JobManager>();
        services.AddSingleton<ConnectivityDiagnostics>();

        services.AddHostedService<AgentWorker>();
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddProvider(new RotatingFileLoggerProvider());
        if (!OperatingSystem.IsWindows())
            logging.AddConsole();
    }

    private static void EnsureDataDirectory(IServiceProvider provider)
    {
        var configService = provider.GetRequiredService<AgentConfigService>();
        var resolved = ResolveDataDirectory(configService);
        Directory.CreateDirectory(resolved);
        Directory.CreateDirectory(Path.Combine(resolved, "Logs"));
        Directory.CreateDirectory(Path.Combine(resolved, "Packages"));
    }

    private static string ResolveDataDirectory(AgentConfigService configService)
    {
        if (!OperatingSystem.IsWindows())
        {
            configService.Current.DataDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "data"));
        }
        return configService.Current.DataDirectory;
    }
}
