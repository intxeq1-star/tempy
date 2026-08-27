using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OMAgent;

public sealed class OmWebViewForm : Form
{
    private readonly WebView2 _web = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public OmWebViewForm()
    {
        Text = "OM Client — Control Center";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(960, 680);
        Icon = SystemIcons.Application;
        var panel = new Panel { Dock = DockStyle.Fill };
        _web.Dock = DockStyle.Fill;
        panel.Controls.Add(_web);
        Controls.Add(panel);
        Load += OnLoad;
    }

    private static string? ExtractUiToDisk()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OMClient", "ui");
            Directory.CreateDirectory(dir);
            var files = new[]
            {
                ("OMAgent.ui.index.html", "index.html"),
                ("OMAgent.ui.styles.css", "styles.css"),
                ("OMAgent.ui.renderer.js", "renderer.js")
            };
            foreach (var (res, name) in files)
            {
                var asm = typeof(OmWebViewForm).Assembly;
                using var s = asm.GetManifestResourceStream(res);
                if (s is null) continue;
                var dest = Path.Combine(dir, name);
                using var f = File.Create(dest);
                s.CopyTo(f);
            }
            return Path.Combine(dir, "index.html");
        }
        catch { return null; }
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, GetUserDataFolder(), null);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;

            var index = ExtractUiToDisk();
            if (index is not null && File.Exists(index))
                _web.CoreWebView2.Navigate(index);
            else
                _web.CoreWebView2.Navigate("about:blank");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start embedded web view: " + ex.Message + "\n\nWebView2 runtime may be missing.", "OM Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetUserDataFolder()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OMClient", "WebView2");
            Directory.CreateDirectory(p);
            return p;
        }
        catch { return null!; }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<WebRequest>(e.WebMessageAsJson, _json);
            if (msg is null) return;
            var result = await DispatchAsync(msg.Type, msg.Payload, CancellationToken.None);
            var reply = JsonSerializer.Serialize(new WebReply { Id = msg.Id, Data = result }, _json);
            _web.CoreWebView2.PostWebMessageAsJson(reply);
        }
        catch (Exception ex)
        {
            var reply = JsonSerializer.Serialize(new WebReply { Id = "0", Data = new { error = ex.Message } }, _json);
            try { _web.CoreWebView2.PostWebMessageAsJson(reply); } catch { }
        }
    }

    private async Task<object> DispatchAsync(string type, JsonNode? payload, CancellationToken ct)
    {
        switch (type)
        {
            case "status":
            {
                var s = StatusStore.Read();
                var service = GetServiceState();
                return new
                {
                    status = new
                    {
                        s.State, s.ProcessId, s.LastScriptPid, s.LastHeartbeatUtc, s.LastRunAtUtc,
                        s.LastRunStatus, s.ScriptUseCount, s.LastOutput, s.BlockedAppsCount
                    },
                    service
                };
            }
            case "install": return await Task.Run(() => WorkerInstaller.Install());
            case "start": return await Task.Run(() => { WorkerInstaller.Start(); return ""; });
            case "stop": return await Task.Run(() => { WorkerInstaller.StopWorker(); return ""; });
            case "uninstall": return await Task.Run(() => WorkerInstaller.Uninstall());
            case "run-command": return await RunCommandAsync(payload, ct);
            case "log": return Logger.Tail(120);
            case "script-path": return OmConstants.ScriptPath;
            case "path-info": return new { dataDir = OmConstants.DataRoot, script = OmConstants.ScriptPath, log = OmConstants.LogsDir, appControl = OmConstants.AppControlPath };
            case "open-folder": OpenFolder(); return "";
            case "open-log": OpenLog(); return "";

            // --- App Control API ---
            case "appcontrol-get":
            {
                var cfg = AppControlService.LoadConfig();
                return new
                {
                    config = cfg,
                    isWindows = OperatingSystem.IsWindows(),
                    protectedApps = new[] { "omclient.exe", "omagent.exe", "explorer.exe", "cmd.exe", "powershell.exe", "taskmgr.exe" }
                };
            }

            case "appcontrol-check-pass":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                bool ok = AppControlService.CheckPassword(pass);
                return new { valid = ok, error = ok ? null : "Invalid password. Use 'om' to unlock." };
            }

            case "appcontrol-add-rule":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                if (!AppControlService.CheckPassword(pass))
                    return new { success = false, error = "Access Denied: Incorrect password. Use 'om' to edit rules." };

                string app = payload?["application"]?.GetValue<string>() ?? "";
                string friendly = payload?["friendlyName"]?.GetValue<string>() ?? "";
                string act = payload?["action"]?.GetValue<string>() ?? "BLOCK";
                string norm = AppControlService.NormalizeExe(app);

                if (string.IsNullOrWhiteSpace(norm))
                    return new { success = false, error = "Please provide a valid application name (e.g. notepad.exe)." };

                if (AppControlService.IsProtected(norm))
                    return new { success = false, error = $"'{norm}' is a critical system app and cannot be blocked." };

                var cfg = AppControlService.LoadConfig();
                var existing = cfg.Rules.FirstOrDefault(r => r.Application.Equals(norm, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Action = act.ToUpperInvariant();
                    existing.FriendlyName = string.IsNullOrWhiteSpace(friendly) ? existing.FriendlyName : friendly;
                    existing.Enabled = true;
                    existing.UpdatedAt = DateTime.UtcNow.ToString("o");
                }
                else
                {
                    cfg.Rules.Add(new AppControlRule
                    {
                        Application = norm,
                        FriendlyName = string.IsNullOrWhiteSpace(friendly) ? norm : friendly,
                        Action = act.ToUpperInvariant(),
                        Enabled = true
                    });
                }

                AppControlService.EnforceAll(cfg);
                AppControlService.SaveConfig(cfg);
                return new { success = true, message = $"Rule for '{norm}' saved and enforced!" };
            }

            case "appcontrol-toggle-rule":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                if (!AppControlService.CheckPassword(pass))
                    return new { success = false, error = "Access Denied: Password 'om' required to toggle rules." };

                string id = payload?["id"]?.GetValue<string>() ?? "";
                bool enabled = payload?["enabled"]?.GetValue<bool>() ?? true;

                var cfg = AppControlService.LoadConfig();
                var rule = cfg.Rules.FirstOrDefault(r => r.Id == id);
                if (rule != null)
                {
                    rule.Enabled = enabled;
                    rule.UpdatedAt = DateTime.UtcNow.ToString("o");
                    AppControlService.EnforceAll(cfg);
                    AppControlService.SaveConfig(cfg);
                    return new { success = true, rule };
                }
                return new { success = false, error = "Rule not found." };
            }

            case "appcontrol-delete-rule":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                if (!AppControlService.CheckPassword(pass))
                    return new { success = false, error = "Access Denied: Password 'om' required to delete rules." };

                string id = payload?["id"]?.GetValue<string>() ?? "";
                var cfg = AppControlService.LoadConfig();
                var rule = cfg.Rules.FirstOrDefault(r => r.Id == id);
                if (rule != null)
                {
                    cfg.Rules.Remove(rule);
                    AppControlService.ApplyUnblock(rule.Application);
                    AppControlService.SaveConfig(cfg);
                    return new { success = true, message = $"Rule for '{rule.Application}' deleted and unblocked." };
                }
                return new { success = false, error = "Rule not found." };
            }

            case "appcontrol-test-start":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                if (!AppControlService.CheckPassword(pass))
                    return new { success = false, error = "Access Denied: Password 'om' required to start test phase." };

                string target = payload?["targetApp"]?.GetValue<string>() ?? "notepad.exe";
                return AppControlService.StartTestBlock(target);
            }

            case "appcontrol-test-launch":
            {
                string target = payload?["targetApp"]?.GetValue<string>() ?? "notepad.exe";
                return AppControlService.RunLaunchTest(target);
            }

            case "appcontrol-test-end":
            {
                string pass = payload?["password"]?.GetValue<string>() ?? "";
                if (!AppControlService.CheckPassword(pass))
                    return new { success = false, error = "Access Denied: Password 'om' required to end test phase." };

                string target = payload?["targetApp"]?.GetValue<string>() ?? "notepad.exe";
                return AppControlService.EndTestBlock(target);
            }

            default: return new { error = "unknown type: " + type };
        }
    }

    private async Task<object> RunCommandAsync(JsonNode? payload, CancellationToken ct)
    {
        string cmd = payload?["cmd"]?.GetValue<string>() ?? "";
        string shellName = payload?["shell"]?.GetValue<string>() ?? "cmd";
        bool asUser = payload?["asUser"]?.GetValue<bool>() ?? false;
        var shell = shellName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? CommandShell.PowerShell : CommandShell.Cmd;
        var result = asUser
            ? await CommandRunner.RunUserAsync(cmd, shell, TimeSpan.FromSeconds(300), ct)
            : await CommandRunner.RunAdminAsync(cmd, shell, TimeSpan.FromSeconds(300), ct);
        return new { result.Started, result.ExitCode, result.TimedOut, Output = result.Output };
    }

    private string GetServiceState()
    {
        if (!OperatingSystem.IsWindows()) return "not installed";
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(OmConstants.ServiceName);
            return sc.Status.ToString();
        }
        catch { return "not installed"; }
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", OmConstants.DataRoot) { UseShellExecute = true }); } catch { }
    }

    private void OpenLog()
    {
        try
        {
            var f = Directory.GetFiles(OmConstants.LogsDir, "omagent-*.log").OrderByDescending(x => x).FirstOrDefault();
            if (f is not null) Process.Start(new ProcessStartInfo("notepad.exe", f) { UseShellExecute = true });
        }
        catch { }
    }

    public sealed class WebRequest
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public JsonNode? Payload { get; set; }
    }

    public sealed class WebReply
    {
        public string Id { get; set; } = "";
        public object? Data { get; set; }
    }
}
