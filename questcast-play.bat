@echo off
rem Receives the QuestCast stream and plays it fullscreen.
rem
rem Sound is always accepted. If "Include headset audio" is switched off in the app
rem then none arrives and the flag does nothing, so the headset alone decides whether
rem there is sound - no second launcher needed.
rem
rem Raw H.264 carries no timestamps, so the frame rate is pinned explicitly and pts
rem correction is switched off. Without that mpv assumes 25 fps, and against a real
rem 30 fps stream the delay grows by a second every few seconds.

setlocal
set "HERE=%~dp0"
set "RX=%HERE%QuestCastRx.exe"

rem Prefer a local mpv (see install.ps1), fall back to one on PATH.
set "MPV=%HERE%mpv\mpv.exe"
if not exist "%MPV%" set "MPV=%HERE%..\mpv\mpv.exe"
if not exist "%MPV%" set "MPV=mpv"

if not exist "%RX%" (
  echo QuestCastRx.exe not found - run build.cmd first.
  exit /b 1
)

"%RX%" --audio %* 2> "%HERE%questcast.log" | "%MPV%" ^
  --fs ^
  --demuxer=lavf --demuxer-lavf-format=h264 ^
  --demuxer-lavf-analyzeduration=0 --demuxer-lavf-probe-info=nostreams ^
  --demuxer-readahead-secs=0 --demuxer-max-bytes=2MiB ^
  --cache=no --untimed --video-latency-hacks=yes ^
  --no-correct-pts --container-fps-override=30 ^
  --framedrop=vo ^
  --hwdec=auto --vo=gpu ^
  --no-terminal --keep-open=no --title=QuestCast -
