# Opens the ports QuestCastRx needs and optionally fetches mpv.
#
# Windows drops inbound UDP for an unsigned executable without asking, which makes the
# receiver invisible to the headset while looking perfectly healthy - so these rules
# are not optional.

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Add-UdpRule($name, $port) {
    $existing = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "firewall rule already present: $name"
        return
    }
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
        -Protocol UDP -LocalPort $port -Profile Any | Out-Null
    Write-Host "added firewall rule: $name (UDP $port)"
}

Add-UdpRule 'QuestCastRx video (UDP 49152)' 49152
Add-UdpRule 'QuestCastRx mDNS (UDP 5353)'   5353

$exe = Join-Path $dir 'QuestCastRx.exe'
if (Test-Path $exe) {
    $name = 'QuestCastRx program'
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
            -Program $exe -Profile Any | Out-Null
        Write-Host "added firewall rule: $name"
    }
} else {
    Write-Host "QuestCastRx.exe not built yet - run build.cmd first (rules are in place regardless)"
}

# mpv is optional: any player that reads H.264 from stdin will do.
$mpv = Join-Path $dir 'mpv\mpv.exe'
if (Test-Path $mpv) {
    Write-Host "mpv already present"
} elseif ((Read-Host "Download mpv into $dir\mpv? [y/N]") -match '^(y|Y)') {
    $rel = Invoke-WebRequest -UseBasicParsing `
        -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' | ConvertFrom-Json
    $asset = $rel.assets | Where-Object { $_.name -like 'mpv-x86_64-2*' -and $_.name -like '*.7z' } | Select-Object -First 1
    if (-not $asset) { throw "could not find an mpv build to download" }

    $archive = Join-Path $env:TEMP $asset.name
    Write-Host "downloading $($asset.name)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing

    $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if (-not $sevenZip) {
        $sevenZip = Join-Path $env:TEMP '7zr.exe'
        Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7zr.exe' -OutFile $sevenZip -UseBasicParsing
    } else {
        $sevenZip = $sevenZip.Source
    }

    & $sevenZip x $archive ("-o" + (Join-Path $dir 'mpv')) -y | Out-Null
    Write-Host "mpv extracted to $dir\mpv"
}

Write-Host ""
Write-Host "Done. Start it with questcast-play.bat, then connect from the QuestCast app."
