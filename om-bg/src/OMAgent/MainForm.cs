using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows.Forms;

namespace OMAgent;

public sealed class MainForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly Label _svcState = new();
    private readonly Label _workerState = new();
    private readonly Label _pid = new();
    private readonly Label _heartbeat = new();
    private readonly Label _lastRun = new();
    private readonly TextBox _cmd = new();
    private readonly ComboBox _shell = new();
    private readonly ComboBox _context = new();
    private readonly TextBox _output = new();
    private readonly TextBox _filter = new();
    private readonly Label _elevation = new();

    public MainForm()
    {
        Text = "OM Background Worker — control";
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(620, 640);
        ClientSize = new Size(640, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var elevation = IsElevated();
        BuildStatusGroup(elevation);
        BuildControlGroup();
        BuildCommandGroup();
        BuildLogGroup();

        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
        FormClosing += (_, _) => _timer.Stop();
    }

    private void BuildStatusGroup(bool elevated)
    {
        var g = new GroupBox { Text = "Worker status", Location = new Point(12, 10), Size = new Size(612, 176), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        g.Controls.Add(new Label { Text = "Service:", Location = new Point(16, 26), AutoSize = true });
        _svcState.Location = new Point(110, 26); _svcState.AutoSize = true; g.Controls.Add(_svcState);

        g.Controls.Add(new Label { Text = "Worker:", Location = new Point(16, 50), AutoSize = true });
        _workerState.Location = new Point(110, 50); _workerState.AutoSize = true; g.Controls.Add(_workerState);

        g.Controls.Add(new Label { Text = "PID:", Location = new Point(16, 74), AutoSize = true });
        _pid.Location = new Point(110, 74); _pid.AutoSize = true; g.Controls.Add(_pid);

        g.Controls.Add(new Label { Text = "Heartbeat:", Location = new Point(16, 98), AutoSize = true });
        _heartbeat.Location = new Point(110, 98); _heartbeat.AutoSize = true; g.Controls.Add(_heartbeat);

        g.Controls.Add(new Label { Text = "Last run:", Location = new Point(16, 122), AutoSize = true });
        _lastRun.Location = new Point(110, 122); _lastRun.AutoSize = true; g.Controls.Add(_lastRun);

        _elevation.Text = elevated ? "Running as Administrator" : "Running as a standard user — Install / Stop / Start need Administrator.";
        _elevation.ForeColor = elevated ? Color.Green : Color.DarkOrange;
        _elevation.Location = new Point(16, 150); _elevation.AutoSize = true; g.Controls.Add(_elevation);
        Controls.Add(g);
    }

    private void BuildControlGroup()
    {
        var g = new GroupBox { Text = "Control", Location = new Point(12, 168), Size = new Size(612, 86), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var install = new Button { Text = "Install worker", Location = new Point(16, 24), Size = new Size(180, 34) };
        install.Click += (_, _) => RunWorkerAction(() => WorkerInstaller.Install(), "installed");

        var start = new Button { Text = "Start worker", Location = new Point(206, 24), Size = new Size(180, 34) };
        start.Click += (_, _) => RunWorkerAction(() => WorkerInstaller.Start(), "started");

        var stop = new Button { Text = "Stop worker", Location = new Point(396, 24), Size = new Size(200, 34), BackColor = Color.Firebrick, ForeColor = Color.White };
        stop.Click += (_, _) => RunWorkerAction(() => WorkerInstaller.StopWorker(), "stopped");

        var openFolder = new Button { Text = "Open script folder", Location = new Point(16, 62), Size = new Size(180, 22), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        openFolder.Click += (_, _) => OpenFolder();

        g.Controls.Add(install); g.Controls.Add(start); g.Controls.Add(stop); g.Controls.Add(openFolder);
        Controls.Add(g);
    }

    private void BuildCommandGroup()
    {
        var g = new GroupBox { Text = "Run a command (ADMIN or USER · cmd or PowerShell)", Location = new Point(12, 260), Size = new Size(612, 210), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        g.Controls.Add(new Label { Text = "Command:", Location = new Point(16, 26), AutoSize = true });
        _cmd.Location = new Point(16, 48); _cmd.Size = new Size(440, 20); _cmd.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _cmd.Text = "whoami"; g.Controls.Add(_cmd);

        var runBtn = new Button { Text = "Run", Location = new Point(466, 46), Size = new Size(120, 24), BackColor = Color.SteelBlue, ForeColor = Color.White, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        runBtn.Click += (_, _) => RunCommandAsync();
        g.Controls.Add(runBtn);

        g.Controls.Add(new Label { Text = "Shell:", Location = new Point(16, 78), AutoSize = true });
        _shell.Location = new Point(70, 76); _shell.Size = new Size(120, 22); _shell.DropDownStyle = ComboBoxStyle.DropDownList;
        _shell.Items.AddRange(new object[] { "cmd", "PowerShell" }); _shell.SelectedIndex = 0;
        g.Controls.Add(_shell);

        g.Controls.Add(new Label { Text = "Context:", Location = new Point(220, 78), AutoSize = true });
        _context.Location = new Point(280, 76); _context.Size = new Size(160, 22); _context.DropDownStyle = ComboBoxStyle.DropDownList;
        _context.Items.AddRange(new object[] { "ADMIN (SYSTEM)", "USER (logged-in)" }); _context.SelectedIndex = 0;
        g.Controls.Add(_context);

        g.Controls.Add(new Label { Text = "Output:", Location = new Point(16, 108), AutoSize = true });
        _output.Location = new Point(16, 128); _output.Size = new Size(576, 70); _output.Multiline = true; _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Both; _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        g.Controls.Add(_output);
        Controls.Add(g);
    }

    private void BuildLogGroup()
    {
        var g = new GroupBox { Text = "Log (last lines)", Location = new Point(12, 476), Size = new Size(612, 196), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        _filter.Location = new Point(16, 26); _filter.Size = new Size(400, 20); _filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        g.Controls.Add(_filter);

        var openLog = new Button { Text = "Open log file", Location = new Point(466, 24), Size = new Size(120, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        openLog.Click += (_, _) => OpenLog();
        g.Controls.Add(openLog);

        var tail = new TextBox { Location = new Point(16, 54), Size = new Size(576, 128), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        tail.Text = SafeLogTail("");
        g.Controls.Add(tail);

        var logTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        logTimer.Tick += (_, _) => tail.Text = SafeLogTail(_filter.Text);
        logTimer.Start();
        Controls.Add(g);
    }

    private string SafeLogTail(string filter)
    {
        try
        {
            var lines = Logger.Tail(400).Split('\n').ToList();
            if (!string.IsNullOrWhiteSpace(filter))
                lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            return string.Join('\n', lines.Take(200));
        }
        catch { return "(no log yet)"; }
    }

    private void OpenFolder()
    {
        try { if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", OmConstants.DataRoot) { UseShellExecute = true }); } catch { }
    }

    private void OpenLog()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(OmConstants.LogsDir, "omagent-" + DateTime.Now.ToString("yyyyMMdd") + ".log")) { UseShellExecute = true });
        }
        catch { }
    }

    private void RunWorkerAction(Action action, string doneLabel)
    {
        try
        {
            action();
            MessageBox.Show($"Worker {doneLabel}.", "OM Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Action failed: " + ex.Message, "OM Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshStatus();
    }

    private async void RunCommandAsync()
    {
        var command = _cmd.Text.Trim();
        if (string.IsNullOrWhiteSpace(command)) { MessageBox.Show("Enter a command first."); return; }

        var shell = _shell.SelectedIndex == 1 ? CommandShell.PowerShell : CommandShell.Cmd;
        var asUser = _context.SelectedIndex == 1;
        _output.Text = "Running… (" + (asUser ? "USER" : "ADMIN") + " / " + shell + ")\r\n\r\n";
        _output.Refresh();

        try
        {
            RunResult result = asUser
                ? await CommandRunner.RunUserAsync(command, shell, TimeSpan.FromSeconds(180), CancellationToken.None)
                : await CommandRunner.RunAdminAsync(command, shell, TimeSpan.FromSeconds(180), CancellationToken.None);
            _output.Text = result.Output + "\r\n\r\nStarted=" + result.Started + " ExitCode=" + result.ExitCode;
        }
        catch (Exception ex)
        {
            _output.Text = "Error: " + ex.Message;
        }
    }

    private void RefreshStatus()
    {
        var s = StatusStore.Read();
        _svcState.Text = ServiceState();
        _workerState.Text = s.State;
        _pid.Text = (s.ProcessId > 0 ? s.ProcessId.ToString() : "-") + (s.LastScriptPid > 0 ? " (script " + s.LastScriptPid + ")" : "");
        _heartbeat.Text = string.IsNullOrWhiteSpace(s.LastHeartbeatUtc) ? "-" : s.LastHeartbeatUtc;
        _lastRun.Text = string.IsNullOrWhiteSpace(s.LastRunAtUtc) ? "-" : s.LastRunAtUtc + " -> " + s.LastRunStatus;
    }

    private string ServiceState()
    {
        if (!OperatingSystem.IsWindows()) return "n/a (non-Windows)";
        try
        {
            using var sc = new ServiceController(OmConstants.ServiceName);
            return sc.Status.ToString();
        }
        catch { return "not installed"; }
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
