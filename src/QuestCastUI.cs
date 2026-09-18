// Small status window for the QuestCast receiver.
//
// It starts the normal launcher and reads the log the receiver writes, rather than
// sitting in the video path: the stream still goes straight from QuestCastRx into mpv
// through a native pipe, so latency is unaffected. The window exists because until you
// connect from the headset nothing happens on screen at all, and it is not obvious
// whether the PC is waiting or broken.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

class QuestCastUI : Form
{
    readonly string dir;
    readonly string logPath;
    readonly Label stateLabel = new Label();
    readonly Label detailLabel = new Label();
    readonly Label hintLabel = new Label();
    readonly Label sleepLabel = new Label();
    readonly Button stopButton = new Button();
    readonly Timer timer = new Timer();

    long lastFrames = -1;
    int idleTicks = 0;
    string adbPath;
    bool sleepSuppressed = false;
    int retryTicks = 0;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new QuestCastUI());
    }

    QuestCastUI()
    {
        dir = Path.GetDirectoryName(Application.ExecutablePath);
        logPath = Path.Combine(dir, "questcast.log");

        Text = "QuestCast";
        ClientSize = new Size(460, 210);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.White;

        stateLabel.Text = "Starting...";
        stateLabel.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
        stateLabel.ForeColor = Color.White;
        stateLabel.SetBounds(24, 24, 412, 36);
        stateLabel.AutoSize = false;

        detailLabel.Font = new Font("Segoe UI", 10F);
        detailLabel.ForeColor = Color.FromArgb(170, 176, 190);
        detailLabel.SetBounds(24, 66, 412, 26);

        hintLabel.Font = new Font("Segoe UI", 10F);
        hintLabel.ForeColor = Color.FromArgb(130, 136, 150);
        hintLabel.SetBounds(24, 94, 412, 34);

        sleepLabel.Font = new Font("Segoe UI", 9F);
        sleepLabel.SetBounds(24, 130, 412, 24);

        stopButton.Text = "Stop";
        stopButton.Font = new Font("Segoe UI", 10F);
        stopButton.SetBounds(24, 162, 110, 32);
        stopButton.FlatStyle = FlatStyle.Flat;
        stopButton.BackColor = Color.FromArgb(44, 48, 58);
        stopButton.ForeColor = Color.White;
        stopButton.Click += delegate { StopAll(); Close(); };

        Controls.Add(stateLabel);
        Controls.Add(detailLabel);
        Controls.Add(hintLabel);
        Controls.Add(sleepLabel);
        Controls.Add(stopButton);

        FormClosing += delegate { StopAll(); };

        adbPath = FindAdb();
        StartStream();
        if (adbPath != null) SetSleepSuppressed(true);   // may be a no-op if adb sees no device

        timer.Interval = 1000;
        timer.Tick += delegate { Refresh_(); };
        timer.Start();
    }

    void StartStream()
    {
        try { File.Delete(logPath); } catch { }
        var bat = Path.Combine(dir, "questcast-play.bat");
        if (!File.Exists(bat))
        {
            stateLabel.Text = "questcast-play.bat not found";
            return;
        }
        var psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
        psi.WorkingDirectory = dir;
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        try { Process.Start(psi); }
        catch (Exception e) { stateLabel.Text = "Could not start: " + e.Message; }
    }

    // A Quest stops streaming the moment it thinks it has been taken off, which is
    // exactly what happens when the headset is passed from one person to the next.
    // Telling it the proximity sensor is covered keeps the stream alive - and it is
    // put back to normal on exit, so the headset is not left in that state.
    //
    // Best effort only: this needs adb to reach the headset, and ADB over Wi-Fi does
    // not survive a headset reboot. If it is unavailable, streaming still works, the
    // stream just stops when the headset comes off.
    static string FindAdb()
    {
        var candidates = new[] {
            Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "adb.exe"),
            @"C:\Program Files\Meta Quest Developer Hub\resources\bin\adb.exe",
            @"C:\Program Files (x86)\Meta Quest Developer Hub\resources\bin\adb.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
            foreach (var part in path.Split(';'))
            {
                try
                {
                    var p = Path.Combine(part.Trim(), "adb.exe");
                    if (part.Trim().Length > 0 && File.Exists(p)) return p;
                }
                catch { }
            }
        return null;
    }

    static string RunAdb(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(6000);
                return outp;
            }
        }
        catch { return null; }
    }

    bool AdbSeesHeadset()
    {
        string devices = RunAdb(adbPath, "devices");
        return devices != null && Regex.IsMatch(devices, @"\S+\s+device\s*$", RegexOptions.Multiline);
    }

    void SetSleepSuppressed(bool on)
    {
        if (adbPath == null) return;
        if (on && !AdbSeesHeadset()) return;
        string action = on ? "prox_close" : "automation_disable";
        RunAdb(adbPath, "shell am broadcast -a com.oculus.vrpowermanager." + action);
        sleepSuppressed = on;
    }

    static void Kill(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(); } catch { }
        }
    }

    void StopAll()
    {
        timer.Stop();
        if (sleepSuppressed) SetSleepSuppressed(false);   // leave the headset as we found it
        Kill("QuestCastRx");
        Kill("mpv");
    }

    // Keep trying: the headset may only become reachable later, when a cable is plugged
    // in or ADB over Wi-Fi is switched on.
    void UpdateSleepState()
    {
        if (!sleepSuppressed && adbPath != null && (retryTicks++ % 10 == 0))
            SetSleepSuppressed(true);

        if (sleepSuppressed)
        {
            sleepLabel.Text = "Headset sleep: off — it can be taken off without stopping";
            sleepLabel.ForeColor = Color.FromArgb(120, 220, 150);
        }
        else
        {
            sleepLabel.Text = adbPath == null
                ? "Headset sleep: on — adb not found, stream stops when taken off"
                : "Headset sleep: on — connect a cable to keep streaming when taken off";
            sleepLabel.ForeColor = Color.FromArgb(220, 180, 120);
        }
    }

    // Reads the tail of the receiver's log and turns its counters into something
    // readable. Format: frames=N (+M/5s) dropped=D ... audio=A/B late=L q=avg/max
    void Refresh_()
    {
        UpdateSleepState();
        string tail = ReadTail();
        if (tail == null)
        {
            stateLabel.Text = "Starting...";
            return;
        }

        var m = Regex.Matches(tail, @"frames=(\d+) \(\+(\d+)/5s\) dropped=(\d+).*?audio=(\d+)/(\d+).*?late=(\d+)");
        if (m.Count == 0)
        {
            Set("Waiting for the headset", "", "Open QuestCast in the headset and connect to this PC.");
            return;
        }

        var last = m[m.Count - 1];
        long frames = long.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
        int per5 = int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture);
        long dropped = long.Parse(last.Groups[3].Value, CultureInfo.InvariantCulture);
        long audioIn = long.Parse(last.Groups[4].Value, CultureInfo.InvariantCulture);
        long lateDrop = long.Parse(last.Groups[6].Value, CultureInfo.InvariantCulture);

        bool live = frames != lastFrames;
        idleTicks = live ? 0 : idleTicks + 1;
        lastFrames = frames;

        if (idleTicks > 8 || per5 == 0)
        {
            Set("Waiting for the headset", "",
                "Open QuestCast in the headset and connect to this PC.");
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            return;
        }

        int fps = per5 / 5;
        string sound = audioIn > 0 ? "sound on" : "no sound";
        string losses = (dropped + lateDrop) == 0 ? "no losses" : (dropped + lateDrop) + " frames lost";

        Set("Streaming", fps + " fps  ·  " + sound + "  ·  " + losses,
            "Close the player window or press Stop to finish.");

        // Get out of the player's way. Sitting on top of a fullscreen video window makes
        // Windows throttle its presentation, the player falls behind, and frames start
        // being dropped - which looks like heavy artefacting.
        if (WindowState != FormWindowState.Minimized) WindowState = FormWindowState.Minimized;
    }

    void Set(string state, string detail, string hint)
    {
        stateLabel.Text = state;
        stateLabel.ForeColor = state == "Streaming" ? Color.FromArgb(120, 220, 150) : Color.White;
        detailLabel.Text = detail;
        hintLabel.Text = hint;
    }

    string ReadTail()
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int want = 4096;
                if (fs.Length > want) fs.Seek(-want, SeekOrigin.End);
                using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
            }
        }
        catch { return null; }
    }
}
