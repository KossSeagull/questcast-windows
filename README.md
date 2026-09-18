# QuestCast receiver for Windows

Watch your Meta Quest on a PC monitor or TV, over your own Wi-Fi, with about half a
second of delay.

[QuestCast](https://github.com/MartinChartrand/QuestCast) is a neat idea: a Quest app
that captures the headset's flat spectator view and pushes it straight to a receiver on
your LAN — no cloud, no account, no Meta services. The catch is that its only receiver
is an Apple TV app you have to build yourself in Xcode. This project is the missing
receiver for Windows: a single small executable that the headset can find and stream to.

Measured on an eight-year-old office PC (i5-6500T, Intel HD 530): 1920x1080 at 30 fps,
~0.5 s glass-to-glass, 3-5% CPU.

## What you need

- Meta Quest 2 / 3 / 3S / Pro with developer mode on, and the QuestCast APK sideloaded
  (grab it from [QuestCast releases](https://github.com/regulara/QuestCast/releases))
- A Windows PC on the same network as the headset
- [mpv](https://mpv.io/installation/) (or ffplay) to display the stream
- .NET Framework, which ships with Windows — nothing to install to build this

## Install

```cmd
git clone https://github.com/KossSeagull/questcast-windows
cd questcast-windows
build.cmd
powershell -ExecutionPolicy Bypass -File install.ps1
```

`build.cmd` compiles both executables with the C# compiler already present in Windows:
`QuestCastRx.exe` (the receiver) and `QuestCast.exe` (a small status window that starts
everything and shows whether the headset is connected).
`install.ps1` adds the firewall rules described below — it needs an elevated prompt, and
it is the step people most often skip.

## Run

Run `QuestCast.exe` for a window that starts everything and shows the state of the
stream, or `questcast-play.bat` to go straight to the picture with no window:

```cmd
questcast-play.bat
```

Then in the headset: open **QuestCast**, pick **QuestCastPC** from the list, press
connect. The picture appears on the PC.

To use your own player, pipe the output anywhere you like:

```cmd
QuestCastRx.exe | mpv --demuxer=lavf --demuxer-lavf-format=h264 ^
    --no-correct-pts --container-fps-override=30 --untimed --cache=no ^
    --hwdec=auto --fs -
```

Options:

| Option | Meaning | Default |
| --- | --- | --- |
| `--ip ADDRESS` | address to advertise over mDNS | interface that has a default gateway |
| `--port PORT` | UDP port to listen on | `49152` |
| `--name NAME` | name shown in the headset | `QuestCastPC` |
| `--audio` | play the headset audio as well | off |
| `--audio-delay MS` | hold the sound back to line it up with the picture | `0` |

Video goes to stdout, progress and errors to stderr.

## Sound

Turn on **Include headset audio** in the QuestCast app. That is the only switch: the
launcher always accepts sound, and when the app is not sending any there is simply
nothing to play.

The receiver plays the audio itself rather than feeding it to the player along with the
video, and that is deliberate. The video path runs in "show each frame the moment it
arrives" mode, which is what keeps latency down; handing a player two tracks would force
it into timestamp-following mode and cost a few hundred milliseconds. So the sound is
played separately and simply held back to meet the picture.

By default it is not held back at all, because Windows' own audio path already adds
roughly as much delay as the video path does — on the setup this was developed against,
zero lined up best. Your screen may differ, since a TV's image processing is usually the
largest single part of the delay, so tune it by ear while the stream is running: write a
number of milliseconds into `audio-delay.txt` next to the executable and it takes effect
within a second, no reconnecting. Sound lagging behind the picture means the number is
too high; there is nothing below zero, so if it still lags at zero the remaining delay is
in the audio device rather than here.

Note that a Quest's speakers are open, so with sound on both the headset and the TV the
room hears the same thing twice, a fraction of a second apart. Give the player
headphones — they hear the game with no delay either way, since capturing audio does not
delay what the headset itself plays.

## Passing the headset around

A Quest stops casting the moment it decides it has been taken off — which is exactly what
happens when you hand it to the next person, so the screen goes blank and someone has to
reconnect. `QuestCast.exe` prevents that: while streaming it tells the headset the
proximity sensor is covered, and puts it back to normal when you stop. The window shows
which state you are in.

This needs `adb` to reach the headset, so it is best effort — but in practice it is
easy: plug a cable in for about ten seconds. The window rechecks every ten seconds,
picks the headset up over USB, sends the command, and it stays in effect after you
unplug, until the headset reboots. ADB over Wi-Fi works too, but it does not survive a
headset reboot, so it is not worth setting up for this.

Without adb everything still works; the stream just stops when the headset comes off.
One caveat: the window restores the sensor when you stop, so if the headset is no longer
reachable by then it will keep not sleeping until its next reboot.

The low-tech alternative, which needs nothing and survives reboots, is a small sticker
over the proximity sensor between the lenses.

## If the headset does not find the PC

**Check the firewall first.** This is almost always the problem. Windows silently drops
incoming UDP for an unsigned executable, so the headset's discovery queries never
arrive and the receiver looks perfectly healthy while being invisible. You need inbound
UDP allowed on 5353 (mDNS) and 49152 (video); `install.ps1` does that.

**Several network adapters?** If the PC runs Tailscale, WSL, VirtualBox or a VPN,
Windows may send the mDNS announcement out the wrong adapter and advertise an address
the headset cannot reach. The receiver picks the interface that has a default gateway,
which is normally right — override it with `--ip` if it is not.

**Both devices on the same subnet?** mDNS is link-local: it does not cross subnets, and
many routers block multicast between the 2.4 GHz and 5 GHz bands or on a guest network.

**Port 5353 already taken?** Bonjour, adb and some printer utilities listen there too.
The receiver shares the port, but if it reports a bind failure, stop the other service.

## If the picture stutters or lags

**Delay grows the longer it runs.** Raw H.264 carries no timestamps, so a player will
invent them — mpv assumes 25 fps, and against a real 30 fps the lag piles up second by
second. Pass `--no-correct-pts --container-fps-override=30`, as the launcher does.

**Occasional blocky artefacts** are lost UDP packets, which are never retransmitted by
design — that is the trade for low latency. They get much rarer if the PC is on
Ethernet rather than Wi-Fi, because then the video only crosses the air once.

**Do not put anything on top of the video.** Another window over the fullscreen player
makes Windows throttle its presentation; the player falls behind and frames get dropped,
and since keyframes arrive only once a second, every dropped frame breaks the picture
until the next one. On modest graphics even the player's own on-screen display is enough
to cause it. The status window minimises itself for this reason, and an overlay with live
counters is deliberately not provided. Check the numbers before or after, not during.

## How it works

The sender announces nothing and listens for nothing: it looks for a Bonjour service
called `_questcast._udp`, then fires H.264 access units at whatever answers, split into
UDP datagrams of at most 1200 bytes with a 24-byte header. So the receiver has to do
three things:

1. **Answer mDNS.** It implements just enough of a responder — PTR, SRV, TXT and A
   records for `_questcast._udp.local` — because the sender has no manual IP entry.
2. **Reassemble access units** from fragments, tolerating reordering and duplicates, and
   dropping units that never complete rather than waiting on them.
3. **Stay out of the way of the socket.** Writing to the player's pipe blocks whenever
   the player falls behind. Doing that on the receive thread stops the socket being
   read, so datagrams are lost, which causes more stalling — a spiral that turned into
   several seconds of lag in testing. Finished units go to a writer thread through a
   short queue instead. The queue holds 24 frames: dropping one costs far more than it
   saves, because a dropped frame breaks the picture until the next keyframe a second
   later, and measured depth stays at 1-2 frames anyway, so the cap only absorbs
   hiccups rather than adding delay.

It also notices when the sender restarts numbering (a new session) or when the stream
has a gap, and waits for fresh SPS/PPS or a keyframe before feeding the decoder again,
replaying cached codec configuration if needed. Without that, reconnecting gives you a
black window.

The protocol is documented in
[Shared/protocol.md](https://github.com/MartinChartrand/QuestCast/blob/main/Shared/protocol.md)
upstream. This receiver was written against that document; no upstream code was copied.

## Credits

The QuestCast sender and protocol are by
[MartinChartrand](https://github.com/MartinChartrand/QuestCast), MIT licensed. This
receiver is an independent implementation of the same protocol, also MIT licensed.
