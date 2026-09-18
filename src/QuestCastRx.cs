// QuestCast receiver for Windows.
//
// The QuestCast sender (Quest APK) finds a receiver over Bonjour/mDNS and then pushes
// H.264 access units as fragmented UDP datagrams. This program advertises the service
// so the headset can find this PC, reassembles the fragments, and writes a plain
// Annex-B H.264 stream to stdout, ready to be piped into mpv/ffplay.
//
// Wire format (from Shared/protocol.md), 24-byte big-endian header:
//   0  4  magic "QCTV" (video) / "QCTA" (audio)
//   4  1  version (1)
//   5  1  flags: 0x01 codec config (SPS/PPS), 0x02 keyframe
//   6  2  header length (24)
//   8  4  frame / access-unit id
//  12  2  fragment index
//  14  2  fragment count
//  16  8  presentation timestamp (microseconds)
//  24  n  payload (<= 1176 bytes)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class QuestCastRx
{
    const int MdnsPort = 5353;
    const string MdnsGroup = "224.0.0.251";
    const string ServiceType = "_questcast._udp.local";

    static int DataPort = 49152;
    static string Instance = "QuestCastPC._questcast._udp.local";
    static string HostName = "questcast-pc.local";

    static IPAddress localIp = IPAddress.Loopback;
    static Stream stdout;
    static long framesOut = 0, framesDropped = 0, bytesOut = 0;
    static long mdnsRx = 0, mdnsTx = 0, dataPkts = 0, lateDrops = 0, audioPkts = 0, audioOut = 0, audioLate = 0;

    // Writing to the player's pipe blocks whenever it falls behind. Doing that on the
    // receive thread stops us reading the socket, so datagrams get lost and the picture
    // breaks up. Hand finished access units to a writer thread through a short queue
    // instead, and throw away the oldest ones when the player cannot keep up - that
    // caps the latency instead of letting it grow.
    const int MaxQueued = 6;
    static readonly object qLock = new object();
    static readonly Queue<byte[]> outQ = new Queue<byte[]>();

    // Audio (optional). The video path deliberately runs "show each frame as it lands",
    // which is what keeps latency low, so mixing sound into the same stream would force
    // the player into timestamp-following mode and cost a few hundred ms. Instead the
    // audio is played here, held back by audioDelayMs so it lines up with the picture
    // (the TV and the decoder put the video several hundred ms behind the sound).
    static bool audioEnabled = false;
    static int audioDelayMs = 0;
    static readonly object aLock = new object();
    static readonly Queue<AudioChunk> audioQ = new Queue<AudioChunk>();

    struct AudioChunk
    {
        public DateTime Arrived;
        public byte[] Pcm;
    }

    static void Main(string[] argv)
    {
        string wantIp = null, wantName = "QuestCastPC";
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            string next = (i + 1 < argv.Length) ? argv[i + 1] : null;
            if (a == "--help" || a == "-h" || a == "/?") { Usage(); return; }
            else if (a == "--ip" && next != null) { wantIp = next; i++; }
            else if (a == "--port" && next != null) { DataPort = int.Parse(next); i++; }
            else if (a == "--name" && next != null) { wantName = next; i++; }
            else if (a == "--audio") audioEnabled = true;
            else if (a == "--audio-delay" && next != null) { audioDelayMs = int.Parse(next); i++; }
            else if (!a.StartsWith("-")) wantIp = a;          // bare address, for convenience
            else { Console.Error.WriteLine("unknown option: " + a); Usage(); return; }
        }

        Instance = wantName + "._questcast._udp.local";
        HostName = wantName.ToLowerInvariant().Replace(' ', '-') + ".local";

        localIp = PickLocalIp(wantIp);
        Log("local IP: " + localIp);
        Log("advertising " + Instance + " on port " + DataPort);

        stdout = Console.OpenStandardOutput();

        var t = new Thread(MdnsLoop);
        t.IsBackground = true;
        t.Start();

        var writer = new Thread(WriterLoop);
        writer.IsBackground = true;
        writer.Start();

        if (audioEnabled)
        {
            Log("audio enabled, delayed by " + audioDelayMs + " ms to match the picture");
            var audio = new Thread(AudioLoop);
            audio.IsBackground = true;
            audio.Start();
        }

        var stats = new Thread(StatsLoop);
        stats.IsBackground = true;
        stats.Start();

        if (audioEnabled)
        {
            var tuner = new Thread(DelayTunerLoop);
            tuner.IsBackground = true;
            tuner.Start();
        }

        VideoLoop();
    }

    static void Usage()
    {
        Console.Error.WriteLine(
            "QuestCastRx - QuestCast receiver for Windows\n" +
            "\n" +
            "Advertises itself over mDNS so the QuestCast app on a Meta Quest can find this\n" +
            "PC, then reassembles the incoming H.264 stream and writes it to stdout.\n" +
            "\n" +
            "Usage:\n" +
            "  QuestCastRx.exe [--ip ADDRESS] [--port PORT] [--name NAME]\n" +
            "\n" +
            "  --ip ADDRESS   address to advertise (default: the interface with a gateway)\n" +
            "  --port PORT    UDP port to listen on (default: 49152)\n" +
            "  --name NAME    name shown in the headset (default: QuestCastPC)\n" +
            "  --audio        play the headset audio (enable it in the app too)\n" +
            "  --audio-delay MS  hold sound back to line up with the picture (default: 0)\n" +
            "                 retune it while playing by writing a number into\n" +
            "                 audio-delay.txt next to the executable\n" +
            "\n" +
            "Pipe the output into a player, for example:\n" +
            "  QuestCastRx.exe | mpv --demuxer=lavf --demuxer-lavf-format=h264 \\\n" +
            "      --no-correct-pts --container-fps-override=30 --untimed -\n" +
            "\n" +
            "Progress is written to stderr, video to stdout.");
    }

    static void Log(string s)
    {
        Console.Error.WriteLine("[rx] " + s);
        Console.Error.Flush();
    }

    static IPAddress PickLocalIp(string preferred)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            IPAddress p;
            if (IPAddress.TryParse(preferred, out p)) return p;
        }
        // Pick the interface that actually reaches the LAN: it must have a default
        // gateway. This keeps us off Tailscale/virtual adapters, which would make the
        // mDNS announcement advertise an address the headset cannot reach.
        IPAddress fallback = null;
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            var props = ni.GetIPProperties();
            bool hasGateway = false;
            foreach (var gw in props.GatewayAddresses)
                if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gw.Address.Equals(IPAddress.Any)) hasGateway = true;

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.") || s.StartsWith("127.")) continue;
                if (hasGateway) return ua.Address;
                if (fallback == null && (s.StartsWith("192.168.") || s.StartsWith("10."))) fallback = ua.Address;
            }
        }
        return fallback != null ? fallback : IPAddress.Loopback;
    }

    // ---------------- video ----------------

    class Pending
    {
        public byte[][] Frags;
        public int Count;
        public int Have;
        public int Bytes;
        public DateTime First;
    }

    static void VideoLoop()
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.ReceiveBufferSize = 8 * 1024 * 1024;
        sock.Bind(new IPEndPoint(IPAddress.Any, DataPort));
        Log("listening for video on udp/" + DataPort);

        var pending = new Dictionary<uint, Pending>();
        var audioPending = new Dictionary<uint, Pending>();
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        bool started = false;             // wait for SPS/PPS or a keyframe before emitting
        uint lastEmitted = 0;
        bool haveLast = false;
        var lastSweep = DateTime.UtcNow;
        var lastData = DateTime.UtcNow;
        byte[] codecConfig = null;        // last SPS/PPS, replayed after a restart

        while (true)
        {
            int n;
            try { n = sock.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            if (n < 24) continue;
            dataPkts++;

            if (buf[0] == 'Q' && buf[1] == 'C' && buf[2] == 'T' && buf[3] == 'A')
            {
                audioPkts++;
                if (audioEnabled) HandleAudio(buf, n, audioPending);
                continue;
            }
            if (!(buf[0] == 'Q' && buf[1] == 'C' && buf[2] == 'T' && buf[3] == 'V')) continue;
            if (buf[4] != 1) continue;

            byte flags = buf[5];
            int headerLen = (buf[6] << 8) | buf[7];
            if (headerLen < 24 || headerLen > n) continue;

            uint frameId = (uint)((buf[8] << 24) | (buf[9] << 16) | (buf[10] << 8) | buf[11]);
            int fragIdx = (buf[12] << 8) | buf[13];
            int fragCnt = (buf[14] << 8) | buf[15];
            if (fragCnt <= 0 || fragIdx >= fragCnt) continue;

            int payloadLen = n - headerLen;
            if (payloadLen < 0) continue;

            // The sender restarts frame numbering on every new session, and a gap in the
            // stream means the decoder lost its reference frames. Either way, resync:
            // wait for fresh SPS/PPS or a keyframe instead of feeding garbage to mpv.
            var now = DateTime.UtcNow;
            bool wentBackwards = haveLast && (int)(frameId - lastEmitted) < -100;
            bool wasIdle = (now - lastData).TotalSeconds > 2;
            if (wentBackwards || wasIdle)
            {
                Log("resync (" + (wentBackwards ? "new session" : "stream gap") + ")");
                started = false;
                haveLast = false;
                pending.Clear();
            }
            lastData = now;

            if (!started)
            {
                if ((flags & 0x01) != 0) started = true;          // SPS/PPS: safe to start
                else if ((flags & 0x02) != 0)
                {
                    // Keyframe without config: replay the cached SPS/PPS first, otherwise
                    // the decoder has nothing to initialise from.
                    if (codecConfig != null)
                    {
                        Enqueue(codecConfig);
                        started = true;
                    }
                }
                if (!started) continue;
            }

            var payload = new byte[payloadLen];
            Buffer.BlockCopy(buf, headerLen, payload, 0, payloadLen);

            if (fragCnt == 1)
            {
                if ((flags & 0x01) != 0) codecConfig = payload;
                Emit(payload, frameId, ref lastEmitted, ref haveLast);
            }
            else
            {
                Pending p;
                if (!pending.TryGetValue(frameId, out p))
                {
                    p = new Pending();
                    p.Frags = new byte[fragCnt][];
                    p.Count = fragCnt;
                    p.First = DateTime.UtcNow;
                    pending[frameId] = p;
                }
                if (p.Frags[fragIdx] == null)     // ignore duplicates
                {
                    p.Frags[fragIdx] = payload;
                    p.Have++;
                    p.Bytes += payloadLen;
                }
                if (p.Have == p.Count)
                {
                    var au = new byte[p.Bytes];
                    int off = 0;
                    for (int i = 0; i < p.Count; i++)
                    {
                        Buffer.BlockCopy(p.Frags[i], 0, au, off, p.Frags[i].Length);
                        off += p.Frags[i].Length;
                    }
                    pending.Remove(frameId);
                    if ((flags & 0x01) != 0) codecConfig = au;
                    Emit(au, frameId, ref lastEmitted, ref haveLast);
                }
            }

            // Drop access units that never completed; holding them adds latency.
            if ((DateTime.UtcNow - lastSweep).TotalMilliseconds > 100)
            {
                lastSweep = DateTime.UtcNow;
                var dead = new List<uint>();
                foreach (var kv in pending)
                    if ((lastSweep - kv.Value.First).TotalMilliseconds > 200) dead.Add(kv.Key);
                foreach (var k in dead) { pending.Remove(k); framesDropped++; }
            }
        }
    }

    static void Emit(byte[] au, uint frameId, ref uint lastEmitted, ref bool haveLast)
    {
        // Skip access units that arrive after a newer one was already written.
        if (haveLast && (int)(frameId - lastEmitted) <= 0) { framesDropped++; return; }
        lastEmitted = frameId;
        haveLast = true;
        Enqueue(au);
        framesOut++;
        bytesOut += au.Length;
    }

    // ---------------- audio ----------------

    // Audio access units are fragmented exactly like video ones, but numbered
    // independently. ~10 ms of 48 kHz stereo PCM per unit, so usually two datagrams.
    static void HandleAudio(byte[] buf, int n, Dictionary<uint, Pending> pending)
    {
        int headerLen = (buf[6] << 8) | buf[7];
        if (headerLen < 24 || headerLen > n) return;

        uint id = (uint)((buf[8] << 24) | (buf[9] << 16) | (buf[10] << 8) | buf[11]);
        int idx = (buf[12] << 8) | buf[13];
        int cnt = (buf[14] << 8) | buf[15];
        if (cnt <= 0 || idx >= cnt) return;

        int len = n - headerLen;
        if (len <= 0) return;

        var payload = new byte[len];
        Buffer.BlockCopy(buf, headerLen, payload, 0, len);

        if (cnt == 1) { QueueAudio(payload); return; }

        Pending p;
        if (!pending.TryGetValue(id, out p))
        {
            p = new Pending();
            p.Frags = new byte[cnt][];
            p.Count = cnt;
            p.First = DateTime.UtcNow;
            pending[id] = p;
        }
        if (p.Frags[idx] == null) { p.Frags[idx] = payload; p.Have++; p.Bytes += len; }

        if (p.Have == p.Count)
        {
            var pcm = new byte[p.Bytes];
            int off = 0;
            for (int i = 0; i < p.Count; i++)
            {
                Buffer.BlockCopy(p.Frags[i], 0, pcm, off, p.Frags[i].Length);
                off += p.Frags[i].Length;
            }
            pending.Remove(id);
            QueueAudio(pcm);
        }

        if (pending.Count > 64)
        {
            var dead = new List<uint>();
            foreach (var kv in pending)
                if ((DateTime.UtcNow - kv.Value.First).TotalMilliseconds > 200) dead.Add(kv.Key);
            foreach (var k in dead) pending.Remove(k);
        }
    }

    // Lining sound up with the picture is a matter of taste and of how much lag the
    // display adds, so allow it to be retuned while playing: drop a number of
    // milliseconds into audio-delay.txt next to the executable and it takes effect.
    static void DelayTunerLoop()
    {
        string path = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "audio-delay.txt");
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    int ms;
                    if (int.TryParse(File.ReadAllText(path).Trim(), out ms) && ms >= 0 && ms <= 3000 && ms != audioDelayMs)
                    {
                        audioDelayMs = ms;
                        Log("audio delay set to " + ms + " ms");
                        lock (aLock) Monitor.PulseAll(aLock);
                    }
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    static void QueueAudio(byte[] pcm)
    {
        lock (aLock)
        {
            // Cap the backlog. If playback cannot keep up, drop the oldest audio rather
            // than letting the delay creep upwards.
            while (audioQ.Count > 300) audioQ.Dequeue();
            audioQ.Enqueue(new AudioChunk { Arrived = DateTime.UtcNow, Pcm = pcm });
            Monitor.Pulse(aLock);
        }
    }

    static void Enqueue(byte[] au)
    {
        lock (qLock)
        {
            while (outQ.Count >= MaxQueued) { outQ.Dequeue(); lateDrops++; }
            outQ.Enqueue(au);
            Monitor.Pulse(qLock);
        }
    }

    // --- playback through winmm, so there is no external dependency ---

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    const uint WHDR_DONE = 0x00000001;

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

    class Slot
    {
        public IntPtr Hdr, Data;
        public int Capacity;
        public bool Queued;
    }

    static void AudioLoop()
    {
        var fmt = new WAVEFORMATEX();
        fmt.wFormatTag = 1;                     // PCM
        fmt.nChannels = 2;
        fmt.nSamplesPerSec = 48000;
        fmt.wBitsPerSample = 16;
        fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
        fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
        fmt.cbSize = 0;

        IntPtr dev;
        int rc = waveOutOpen(out dev, 0xFFFFFFFF /* WAVE_MAPPER */, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) { Log("audio device could not be opened (error " + rc + "), continuing without sound"); return; }

        int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        var slots = new Slot[24];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new Slot();
            slots[i].Capacity = 8192;
            slots[i].Data = Marshal.AllocHGlobal(slots[i].Capacity);
            slots[i].Hdr = Marshal.AllocHGlobal(hdrSize);
        }

        while (true)
        {
            AudioChunk chunk;
            lock (aLock)
            {
                while (audioQ.Count == 0) Monitor.Wait(aLock);
                chunk = audioQ.Peek();

                // Hold each chunk back until it is as old as the video pipeline's own
                // delay, so sound and picture line up.
                double waited = (DateTime.UtcNow - chunk.Arrived).TotalMilliseconds;
                if (waited < audioDelayMs)
                {
                    Monitor.Wait(aLock, (int)Math.Max(1, audioDelayMs - waited));
                    continue;
                }
                audioQ.Dequeue();
            }

            Slot slot = null;
            foreach (var s in slots)
            {
                if (!s.Queued) { slot = s; break; }
                var h = (WAVEHDR)Marshal.PtrToStructure(s.Hdr, typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) != 0)
                {
                    waveOutUnprepareHeader(dev, s.Hdr, hdrSize);
                    s.Queued = false;
                    slot = s;
                    break;
                }
            }
            if (slot == null) { audioLate++; continue; }   // device backed up; skip this chunk

            if (chunk.Pcm.Length > slot.Capacity)
            {
                Marshal.FreeHGlobal(slot.Data);
                slot.Capacity = chunk.Pcm.Length;
                slot.Data = Marshal.AllocHGlobal(slot.Capacity);
            }
            Marshal.Copy(chunk.Pcm, 0, slot.Data, chunk.Pcm.Length);

            var hdr = new WAVEHDR();
            hdr.lpData = slot.Data;
            hdr.dwBufferLength = (uint)chunk.Pcm.Length;
            Marshal.StructureToPtr(hdr, slot.Hdr, false);

            if (waveOutPrepareHeader(dev, slot.Hdr, hdrSize) == 0 &&
                waveOutWrite(dev, slot.Hdr, hdrSize) == 0)
            {
                slot.Queued = true;
                audioOut++;
            }
        }
    }

    static void WriterLoop()
    {
        while (true)
        {
            byte[] au;
            lock (qLock)
            {
                while (outQ.Count == 0) Monitor.Wait(qLock);
                au = outQ.Dequeue();
            }
            try
            {
                stdout.Write(au, 0, au.Length);
                stdout.Flush();
            }
            catch (IOException) { Environment.Exit(0); }   // player closed the pipe
        }
    }

    static void StatsLoop()
    {
        long prev = 0;
        while (true)
        {
            Thread.Sleep(5000);
            long f = framesOut;
            Log("frames=" + f + " (+" + (f - prev) + "/5s) dropped=" + framesDropped +
                " mbytes=" + (bytesOut / 1048576) + " datapkts=" + dataPkts + " audio=" + audioPkts + "/" + audioOut + " alate=" + audioLate + " late=" + lateDrops + " mdns_rx=" + mdnsRx);
            prev = f;
        }
    }

    // ---------------- mDNS ----------------

    static void MdnsLoop()
    {
        Socket sock = null;
        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(MdnsGroup), localIp));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            // This PC has several interfaces (Wi-Fi, Ethernet, Tailscale). Without pinning the
            // outgoing interface, Windows may send the announcement out the wrong one.
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                localIp.GetAddressBytes());
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }
        catch (Exception e)
        {
            Log("mDNS bind failed: " + e.Message + " (is Bonjour already running?)");
            return;
        }

        var group = new IPEndPoint(IPAddress.Parse(MdnsGroup), MdnsPort);

        // Announce unprompted as well - some clients rely on it.
        var announcer = new Thread(delegate ()
        {
            while (true)
            {
                try { var pkt = BuildAnswer(); sock.SendTo(pkt, group); }
                catch (Exception e) { Log("announce failed: " + e.Message); }
                Thread.Sleep(3000);
            }
        });
        announcer.IsBackground = true;
        announcer.Start();

        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int n;
            try { n = sock.ReceiveFrom(buf, ref any); }
            catch (SocketException) { continue; }
            if (n < 12) continue;
            mdnsRx++;

            try
            {
                if (!QueryWantsUs(buf, n)) continue;
                var pkt = BuildAnswer();
                sock.SendTo(pkt, group);
                // Also answer the asker directly - some stacks accept the unicast reply
                // even when the multicast one is lost or filtered.
                try { sock.SendTo(pkt, any); } catch { }
                mdnsTx++;
                Log("answered mDNS query from " + any);
            }
            catch { }
        }
    }

    static bool QueryWantsUs(byte[] b, int n)
    {
        int flags = (b[2] << 8) | b[3];
        if ((flags & 0x8000) != 0) return false;           // responses are not queries
        int qd = (b[4] << 8) | b[5];
        int off = 12;
        for (int i = 0; i < qd && off < n; i++)
        {
            string name = ParseName(b, n, ref off);
            if (off + 4 > n) return false;
            int qtype = (b[off] << 8) | b[off + 1];
            off += 4;
            string lower = name.ToLowerInvariant().TrimEnd('.');
            if (lower == ServiceType && (qtype == 12 || qtype == 255)) return true;
            if (lower == Instance.ToLowerInvariant() && (qtype == 33 || qtype == 16 || qtype == 255)) return true;
            if (lower == HostName && (qtype == 1 || qtype == 255)) return true;
        }
        return false;
    }

    static string ParseName(byte[] b, int n, ref int off)
    {
        var sb = new StringBuilder();
        int jumps = 0;
        int cur = off;
        bool jumped = false;
        while (cur < n)
        {
            int len = b[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (cur + 1 >= n) break;
                int ptr = ((len & 0x3F) << 8) | b[cur + 1];
                if (!jumped) { off = cur + 2; jumped = true; }
                cur = ptr;
                if (++jumps > 16) break;
                continue;
            }
            cur++;
            if (cur + len > n) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(b, cur, len));
            cur += len;
        }
        if (!jumped) off = cur;
        return sb.ToString();
    }

    static byte[] EncodeName(string name)
    {
        var ms = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0) continue;
            var lb = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)lb.Length);
            ms.Write(lb, 0, lb.Length);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    static void W16(MemoryStream ms, int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    static void W32(MemoryStream ms, long v)
    {
        ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
    }

    static void AddRecord(MemoryStream ms, string name, int type, int cls, int ttl, byte[] rdata)
    {
        var nb = EncodeName(name);
        ms.Write(nb, 0, nb.Length);
        W16(ms, type);
        W16(ms, cls);
        W32(ms, ttl);
        W16(ms, rdata.Length);
        ms.Write(rdata, 0, rdata.Length);
    }

    static byte[] BuildAnswer()
    {
        var ms = new MemoryStream();
        W16(ms, 0);            // transaction id
        W16(ms, 0x8400);       // response + authoritative
        W16(ms, 0);            // questions
        W16(ms, 1);            // answers
        W16(ms, 0);            // authority
        W16(ms, 3);            // additional

        AddRecord(ms, ServiceType, 12, 1, 120, EncodeName(Instance));            // PTR

        var srv = new MemoryStream();                                            // SRV
        W16(srv, 0); W16(srv, 0); W16(srv, DataPort);
        var target = EncodeName(HostName);
        srv.Write(target, 0, target.Length);
        AddRecord(ms, Instance, 33, 0x8001, 120, srv.ToArray());

        AddRecord(ms, Instance, 16, 0x8001, 120, new byte[] { 0 });              // TXT (empty)
        AddRecord(ms, HostName, 1, 0x8001, 120, localIp.GetAddressBytes());      // A

        return ms.ToArray();
    }
}
