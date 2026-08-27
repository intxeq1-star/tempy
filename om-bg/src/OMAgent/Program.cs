using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OMAgent
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int AttachParentProcess = -1;

        [STAThread]
        public static async Task<int> Main(string[] args)
        {
            string? cmd = args.Length > 0 ? args[0].ToLowerInvariant() : null;

            bool isService = cmd == "--service" || cmd == "--run" || (!Environment.UserInteractive && args.Length == 0);
            if (isService)
                return await RunWorkerHostAsync().ConfigureAwait(false);

            if (!string.IsNullOrEmpty(cmd))
            {
                AttachConsole(AttachParentProcess);
                return await RunCliAsync(args).ConfigureAwait(false);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static async Task<int> RunWorkerHostAsync()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(options => options.ServiceName = OmConstants.ServiceName);
            builder.Services.AddSingleton(new AppConfig());
            builder.Services.AddSingleton<ScriptRunner>();
            builder.Services.AddHostedService<WorkerService>();
            var host = builder.Build();
            try { await host.RunAsync().ConfigureAwait(false); return 0; }
            catch (Exception ex)
            {
                Logger.Error("Worker host failed", ex);
                return 1;
            }
        }

        private static async Task<int> RunCliAsync(string[] args)
        {
            string? cmd = args[0].ToLowerInvariant();
            switch (cmd)
            {
                case "install": case "i": return WorkerInstaller.Install();
                case "uninstall": case "remove": case "u": return WorkerInstaller.Uninstall();
                case "start": return WorkerInstaller.Start();
                case "stop": return WorkerInstaller.Stop();
                case "stop-now": case "stopworker": WorkerInstaller.StopWorker(); return 0;
                case "restart": WorkerInstaller.Stop(); Thread.Sleep(1500); return WorkerInstaller.Start();
                case "status": case "st": return await ShowStatusAsync().ConfigureAwait(false);
                case "run": return await RunCommandAsync(args, isUser: false).ConfigureAwait(false);
                case "run-user": case "runuser": case "user": return await RunCommandAsync(args, isUser: true).ConfigureAwait(false);
                case "log": Console.WriteLine(Logger.Tail(40)); return 0;
                case "script": Console.WriteLine(OmConstants.ScriptPath); return 0;
                default:
                    Console.WriteLine("OM Background Worker — commands: install | start | stop | stop-now | restart | status | log | script | run | run-user");
                    return 0;
            }
        }

        private static Task<int> ShowStatusAsync()
        {
            var status = StatusStore.Read();
            var installed = WorkerInstaller.IsInstalled();
            Console.WriteLine("===================================================================");
            Console.WriteLine("  OM Background Worker — status");
            Console.WriteLine("===================================================================");
            Console.WriteLine($"  Service name   : {OmConstants.ServiceName}");
            Console.WriteLine($"  Installed      : {(installed ? "yes" : "NO")}");
            Console.WriteLine($"  Worker state   : {status.State}");
            Console.WriteLine($"  Machine        : {Environment.MachineName}  ({status.MachineName})");
            Console.WriteLine($"  Version        : {status.Version}");
            if (status.ProcessId > 0) Console.WriteLine($"  Worker PID     : {status.ProcessId}");
            if (status.LastScriptPid > 0) Console.WriteLine($"  Script PID     : {status.LastScriptPid}");
            Console.WriteLine($"  Last heartbeat : {status.LastHeartbeatUtc}");
            Console.WriteLine($"  Last run (UTC) : {status.LastRunAtUtc}   status={status.LastRunStatus}");
            Console.WriteLine($"  Script runs    : {status.ScriptUseCount}");
            Console.WriteLine("===================================================================");
            return Task.FromResult(0);
        }

        private static async Task<int> RunCommandAsync(string[] args, bool isUser)
        {
            var shell = CommandShell.Cmd;
            var command = new List<string>();
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--shell" && i + 1 < args.Length)
                {
                    shell = args[++i].Equals("powershell", StringComparison.OrdinalIgnoreCase) || args[i].Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                        ? CommandShell.PowerShell : CommandShell.Cmd;
                }
                else if (a is "--powershell" or "--ps" or "-ps") shell = CommandShell.PowerShell;
                else if (a is "--cmd") shell = CommandShell.Cmd;
                else command.Add(a);
            }
            var cmdLine = string.Join(" ", command);
            if (string.IsNullOrWhiteSpace(cmdLine)) { Console.WriteLine("No command given."); return 1; }
            RunResult result = isUser
                ? await CommandRunner.RunUserAsync(cmdLine, shell, TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(false)
                : await CommandRunner.RunAdminAsync(cmdLine, shell, TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(result.Output);
            return result.ExitCode;
        }
    }
}
