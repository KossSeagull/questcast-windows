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

`build.cmd` compiles `QuestCastRx.exe` with the C# compiler already present in Windows.
`install.ps1` adds the firewall rules described below — it needs an elevated prompt, and
it is the step people most often skip.

## Run

Start the receiver and the player together:

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

Video goes to stdout, progress and errors to stderr.

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

**The stream stops when you take the headset off.** The sender stops when it goes to the
background. To keep it running while the headset is off your head:

```cmd
adb shell am broadcast -a com.oculus.vrpowermanager.prox_close
```

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
   four-frame queue instead, and the oldest are discarded when the player cannot keep
   up, which caps latency instead of letting it grow.

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
