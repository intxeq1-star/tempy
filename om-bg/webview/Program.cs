using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OMAgent
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : null;

            bool isService = cmd == "--service" || cmd == "--run" || (!Environment.UserInteractive && args.Length == 0);
            if (isService)
                return RunService();

            if (!string.IsNullOrEmpty(cmd))
                return RunCli(args);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OmWebViewForm());
            return 0;
        }

        private static int RunService()
        {
            var b = Host.CreateApplicationBuilder();
            b.Services.AddWindowsService(o => o.ServiceName = OmConstants.ServiceName);
            b.Services.AddSingleton(new AppConfig());
            b.Services.AddSingleton<ScriptRunner>();
            b.Services.AddHostedService<WorkerService>();
            var host = b.Build();
            try { host.Run(); return 0; }
            catch (Exception ex) { Logger.Error("Worker host failed", ex); return 1; }
        }

        private static int RunCli(string[] args)
        {
            var cmd = args[0].ToLowerInvariant();
            switch (cmd)
            {
                case "install": case "i": return WorkerInstaller.Install();
                case "uninstall": case "remove": case "u": return WorkerInstaller.Uninstall();
                case "start": return WorkerInstaller.Start();
                case "stop": return WorkerInstaller.Stop();
                case "stop-now": case "stopworker": WorkerInstaller.StopWorker(); return 0;
                case "restart": WorkerInstaller.Stop(); Thread.Sleep(1500); return WorkerInstaller.Start();
                case "status": case "st": return ShowStatus();
                case "block":
                    if (args.Length > 1) {
                        AppControlService.ApplyBlock(args[1]);
                        Console.WriteLine($"[BLOCKED] {args[1]} is now blocked.");
                        return 0;
                    }
                    Console.WriteLine("Usage: OMClient.exe block <app.exe>");
                    return 1;
                case "unblock":
                    if (args.Length > 1) {
                        AppControlService.ApplyUnblock(args[1]);
                        Console.WriteLine($"[UNBLOCKED] {args[1]} is now unblocked.");
                        return 0;
                    }
                    Console.WriteLine("Usage: OMClient.exe unblock <app.exe>");
                    return 1;
                case "test-block":
                    var target = args.Length > 1 ? args[1] : "notepad.exe";
                    AppControlService.StartTestBlock(target);
                    Console.WriteLine($"[TEST BLOCK ACTIVE] {target} is now blocked. Try opening it!");
                    return 0;
                case "test-launch":
                    var targetL = args.Length > 1 ? args[1] : "notepad.exe";
                    var res = AppControlService.RunLaunchTest(targetL);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                case "test-end":
                    var targetE = args.Length > 1 ? args[1] : "notepad.exe";
                    AppControlService.EndTestBlock(targetE);
                    Console.WriteLine($"[TEST ENDED] {targetE} has been restored to normal.");
                    return 0;
                case "run": return RunCommand(args, false);
                case "run-user": case "runuser": case "user": return RunCommand(args, true);
                case "log": Console.WriteLine(Logger.Tail(40)); return 0;
                case "script": Console.WriteLine(OmConstants.ScriptPath); return 0;
                default:
                    Console.WriteLine("OM Client — commands: install | start | stop | stop-now | restart | status | block <app> | unblock <app> | test-block <app> | test-launch <app> | test-end <app> | log | script | run \"cmd\" | run-user \"cmd\"");
                    return 0;
            }
        }

        private static int RunCommand(string[] args, bool asUser)
        {
            var shell = CommandShell.Cmd;
            var cmd = new List<string>();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--shell" && i + 1 < args.Length)
                    shell = args[++i].Equals("powershell", StringComparison.OrdinalIgnoreCase) ? CommandShell.PowerShell : CommandShell.Cmd;
                else if (args[i] is "--powershell" or "--ps")
                    shell = CommandShell.PowerShell;
                else if (args[i] is "--cmd")
                    shell = CommandShell.Cmd;
                else cmd.Add(args[i]);
            }
            var line = string.Join(" ", cmd);
            if (string.IsNullOrWhiteSpace(line)) { Console.WriteLine("No command."); return 1; }
            var r = asUser
                ? CommandRunner.RunUserAsync(line, shell, TimeSpan.FromSeconds(180), CancellationToken.None).GetAwaiter().GetResult()
                : CommandRunner.RunAdminAsync(line, shell, TimeSpan.FromSeconds(180), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(r.Output);
            return r.ExitCode;
        }

        private static int ShowStatus()
        {
            var s = StatusStore.Read();
            Console.WriteLine("Service      : " + OmConstants.ServiceName + "  (installed=" + WorkerInstaller.IsInstalled() + ")");
            Console.WriteLine("State        : " + s.State);
            Console.WriteLine("PID          : " + s.ProcessId + (s.LastScriptPid > 0 ? " (script " + s.LastScriptPid + ")" : ""));
            Console.WriteLine("Heartbeat    : " + (string.IsNullOrWhiteSpace(s.LastHeartbeatUtc) ? "-" : s.LastHeartbeatUtc));
            Console.WriteLine("Last run     : " + (string.IsNullOrWhiteSpace(s.LastRunAtUtc) ? "-" : s.LastRunAtUtc) + "  " + s.LastRunStatus);
            Console.WriteLine("Blocked apps : " + s.BlockedAppsCount);
            Console.WriteLine("Runs         : " + s.ScriptUseCount);
            Console.WriteLine("Output       :\n" + (string.IsNullOrWhiteSpace(s.LastOutput) ? "(none)" : s.LastOutput));
            return 0;
        }
    }
}
