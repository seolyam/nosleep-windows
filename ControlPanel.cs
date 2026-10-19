using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace nosleep_windows;

// Small window shown when the exe is launched without arguments (e.g. double-clicked).
// It drives the same 'on' / 'off' commands as the CLI by running this exe, so behavior stays identical.
internal class ControlPanel : Form
{
    private readonly Label statusLabel = new();
    private readonly Label detailLabel = new();
    private readonly ComboBox durationBox = new();
    private readonly CheckBox batteryCheck = new();
    private readonly NumericUpDown batteryPercent = new();
    private readonly CheckBox requireAcCheck = new();
    private readonly TextBox targetsBox = new();
    private readonly Button toggleButton = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };

    // True while a start/stop is in flight, so the refresh timer doesn't treat the half-started
    // state file as a stale session.
    private bool busy;
    private bool running;

    public static void Run()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ControlPanel());
    }

    private ControlPanel()
    {
        Text = "NoSleep";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(380, 330);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? Icon;

        statusLabel.SetBounds(16, 14, 348, 34);
        statusLabel.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        detailLabel.SetBounds(16, 50, 348, 40);
        detailLabel.ForeColor = SystemColors.GrayText;

        var durationLabel = new Label { Text = "Stay awake for", Left = 16, Top = 104, Width = 110 };
        durationBox.SetBounds(130, 100, 234, 28);
        durationBox.DropDownStyle = ComboBoxStyle.DropDown; // editable: type e.g. 1h30m
        durationBox.Items.AddRange(new object[] { "Until I turn it off", "30m", "1h", "2h", "4h", "8h" });
        durationBox.SelectedIndex = 0;

        batteryCheck.SetBounds(16, 140, 240, 24);
        batteryCheck.Text = "Turn off if battery drops to";
        batteryCheck.Checked = true;
        batteryPercent.SetBounds(262, 138, 56, 26);
        batteryPercent.Minimum = 1;
        batteryPercent.Maximum = 99;
        batteryPercent.Value = 20;
        var pct = new Label { Text = "%", Left = 322, Top = 142, Width = 20 };
        batteryCheck.CheckedChanged += (_, _) => batteryPercent.Enabled = batteryCheck.Checked;

        requireAcCheck.SetBounds(16, 172, 348, 24);
        requireAcCheck.Text = "Only while plugged in (turn off if charger is removed)";

        var targetsLabel = new Label { Text = "Extra apps to protect (optional, comma separated)", Left = 16, Top = 206, Width = 348 };
        targetsBox.SetBounds(16, 230, 348, 26);

        toggleButton.SetBounds(16, 272, 348, 42);
        toggleButton.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        toggleButton.Click += async (_, _) => await ToggleAsync();

        Controls.AddRange(new Control[]
        {
            statusLabel, detailLabel, durationLabel, durationBox, batteryCheck, batteryPercent, pct,
            requireAcCheck, targetsLabel, targetsBox, toggleButton
        });

        refreshTimer.Tick += (_, _) => Refresh_();
        refreshTimer.Start();
        Refresh_();
    }

    private void Refresh_()
    {
        if (busy) return;

        var state = StateManager.LoadState();
        running = state != null && CommandHandlers.IsDaemonRunning(state);

        // A state file with a recorded PID but no live daemon is a session that died without cleaning up;
        // put the user's settings back (same as 'nosleep status' does).
        if (state != null && !running && state.DaemonPid > 0)
        {
            try { Daemon.RestoreState(state); } catch { }
            state = null;
        }

        if (running && state != null)
        {
            statusLabel.Text = "● ON";
            statusLabel.ForeColor = Color.SeaGreen;
            detailLabel.Text = DescribeRunning(state);
            toggleButton.Text = "Turn OFF";
        }
        else
        {
            statusLabel.Text = "○ OFF";
            statusLabel.ForeColor = SystemColors.ControlText;
            detailLabel.Text = "Windows will sleep normally.";
            toggleButton.Text = "Turn ON";
        }

        bool editable = !running;
        durationBox.Enabled = batteryCheck.Enabled = requireAcCheck.Enabled = targetsBox.Enabled = editable;
        batteryPercent.Enabled = editable && batteryCheck.Checked;
        toggleButton.Enabled = true;
    }

    private static string DescribeRunning(NoSleepState state)
    {
        var sb = new StringBuilder("Staying awake, lid close does nothing. ");
        if (state.ExpirationTime.HasValue)
        {
            var rem = state.ExpirationTime.Value - DateTime.Now;
            sb.Append(rem.TotalSeconds > 0
                ? $"Turns off in {(int)rem.TotalHours}h {rem.Minutes:00}m (at {state.ExpirationTime.Value:hh:mm tt})."
                : "Turning off now.");
        }
        else
        {
            sb.Append("Runs until you turn it off.");
        }
        return sb.ToString();
    }

    private async Task ToggleAsync()
    {
        busy = true;
        toggleButton.Enabled = false;
        try
        {
            string args = running ? "off" : BuildOnArgs();
            if (args.Length == 0) return;

            toggleButton.Text = running ? "Turning off..." : "Starting...";
            var (code, output) = await Task.Run(() => RunSelf(args));

            if (code != 0)
            {
                MessageBox.Show(this, string.IsNullOrWhiteSpace(output) ? "Something went wrong." : output.Trim(),
                    "NoSleep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            busy = false;
            Refresh_();
        }
    }

    private string BuildOnArgs()
    {
        var args = new StringBuilder("on");

        string duration = durationBox.Text.Trim();
        if (duration.Length > 0 && durationBox.SelectedIndex != 0)
            args.Append(' ').Append(duration);

        args.Append(batteryCheck.Checked ? $" --battery={(int)batteryPercent.Value}" : " --no-battery");
        if (requireAcCheck.Checked) args.Append(" --require-ac");

        string targets = targetsBox.Text.Trim();
        if (targets.Length > 0)
        {
            if (targets.Contains('"'))
            {
                MessageBox.Show(this, "App names can't contain quotes.", "NoSleep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return "";
            }
            args.Append(" \"--target=").Append(targets).Append('"');
        }
        return args.ToString();
    }

    private static (int code, string output) RunSelf(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "nosleep-windows.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        // Errors (bad duration, low battery) are written to stderr, so prefer it when present.
        string err = proc.StandardError.ReadToEnd();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, err.Length > 0 ? err : output);
    }
}
