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
    readonly Button stopButton = new Button();
    readonly Timer timer = new Timer();

    long lastFrames = -1;
    int idleTicks = 0;

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
        hintLabel.SetBounds(24, 96, 412, 52);

        stopButton.Text = "Stop";
        stopButton.Font = new Font("Segoe UI", 10F);
        stopButton.SetBounds(24, 158, 110, 32);
        stopButton.FlatStyle = FlatStyle.Flat;
        stopButton.BackColor = Color.FromArgb(44, 48, 58);
        stopButton.ForeColor = Color.White;
        stopButton.Click += delegate { StopAll(); Close(); };

        Controls.Add(stateLabel);
        Controls.Add(detailLabel);
        Controls.Add(hintLabel);
        Controls.Add(stopButton);

        FormClosing += delegate { StopAll(); };

        StartStream();

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
        Kill("QuestCastRx");
        Kill("mpv");
    }

    // Reads the tail of the receiver's log and turns its counters into something
    // readable. Format: frames=N (+M/5s) dropped=D ... audio=A/B alate=X late=L
    void Refresh_()
    {
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
            return;
        }

        int fps = per5 / 5;
        string sound = audioIn > 0 ? "sound on" : "no sound";
        string losses = (dropped + lateDrop) == 0 ? "no losses" : (dropped + lateDrop) + " frames lost";

        Set("Streaming",
            fps + " fps  ·  " + sound + "  ·  " + losses,
            "Close the player window or press Stop to finish.");
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
