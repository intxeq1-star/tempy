using LanAgent.Core;
using LanAgent.Core.Configuration;
using LanAgent.Core.Logging;
using LanAgent.Service;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // When running as a Windows service the working directory is System32 — anchor to the binary.
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = false
});

// Windows Service lifetime (SCM start/stop); falls back to console lifetime when run interactively.
builder.Services.AddWindowsService(options => { options.ServiceName = "LanAgent"; });

builder.Services.AddSingleton(sp =>
{
    var options = new AgentOptions();
    var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    config.GetSection("Agent").Bind(options);
    options.Validate();
    return options;
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<AgentOptions>();
    var logDirectory = Path.Combine(options.DataDirectory, "logs");
    var interactive = Environment.UserInteractive && !OperatingSystem.IsWindows()
        ? true
        : Environment.UserInteractive && Environment.GetEnvironmentVariable("LANAGENT_CONSOLE") == "1";
    return new RollingFileLog(logDirectory, mirrorToConsole: interactive);
});

builder.Services.AddSingleton<IAgentLog>(sp => sp.GetRequiredService<RollingFileLog>());
builder.Services.AddSingleton<AgentEngine>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
return;
