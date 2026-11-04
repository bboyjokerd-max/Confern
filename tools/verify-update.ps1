# verify-update.ps1 -- acceptance for the update check (2026-11-03).
#
# WHAT THIS PROVES
#   The product can ask ONE question -- "is there a newer build than mine?" -- and it answers it in
#   five ways, only one of which is allowed to talk to the user:
#     state=newer    the published build is newer            -> announce it
#     state=current  same version                            -> silence
#     state=older    the published build is older than mine  -> silence
#     state=failed   cannot reach / cannot parse / HTTP error -> silence (this is not an error)
#     (no url)       nothing configured                      -> exit 4, nothing checked
#
# WHY A REAL SOCKET
#   The interesting part of this feature is the network path: a timeout, a non-200 answer, a body
#   that is not JSON. Stubbing the HTTP client would test the stub. So this suite runs a raw
#   TcpListener on 127.0.0.1 and answers with hand-written HTTP/1.1 -- no ACL, no admin, no
#   HttpListener, and the product's real HttpClient goes over a real loopback socket.
#
# WHAT IT DOES NOT PROVE
#   That there is a server hosting a manifest. There is not one yet (UpdateChecker.DefaultManifestUrl
#   is deliberately empty), so every check here points the product at 127.0.0.1 by hand.
#
# ISOLATION
#   CONFERN_UPDATE_CHECK points inside this run's temp folder, so the user's own cached answer (and
#   their "already announced" memory) are never read or written. The last check fingerprints the real
#   file to prove it.
#
# ENCODING: ASCII only, CRLF, no BOM.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Work = ''
)

$ErrorActionPreference = 'Stop'
$script:ExpectedChecks = 10
$checks = 0
$pass = 0
$fail = 0
$inconclusive = 0
$failures = New-Object System.Collections.ArrayList

function Expect([int]$n) {
    if ($checks -ne $n) {
        Write-Host "FATAL: registered $checks checks but this suite must register exactly $n" -ForegroundColor Red
        exit 2
    }
}

function Pass([string]$name) {
    $script:checks++
    $script:pass++
    Write-Host "PASS  $name"
}

function Fail([string]$name, [string]$why) {
    $script:checks++
    $script:fail++
    [void]$script:failures.Add("$name -- $why")
    Write-Host "FAIL  $name" -ForegroundColor Red
    Write-Host "      $why" -ForegroundColor DarkGray
}

function Check([string]$name, [bool]$ok, [string]$why) {
    if ($ok) { Pass $name } else { Fail $name $why }
}

# -------------------------------------------------------------------------------------------- setup
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "FATAL: no exe at '$Exe'" -ForegroundColor Red; exit 2 }

if ($Work -eq '') { $Work = Join-Path (Join-Path $env:TEMP 'confern-update') ([guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$cachePath = Join-Path $Work 'update-check.json'
$env:CONFERN_UPDATE_CHECK = $cachePath
$realCachePath = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'update-check.json'

function Get-FileFingerprint([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 'absent' }
    $i = Get-Item -LiteralPath $path
    return ("{0}|{1}|{2}" -f $i.Length, $i.LastWriteTimeUtc.Ticks,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
}

$realCacheBefore = Get-FileFingerprint $realCachePath

$version = (& $Exe --version 2>$null | Select-Object -First 1)
if (-not $version) { $version = '0.1.0' }
$version = $version.Trim()

Write-Host "exe     : $Exe"
Write-Host "          $((Get-Item -LiteralPath $Exe).LastWriteTime)"
Write-Host "version : $version"
Write-Host "cache   : $cachePath (this run; the real one is not touched)"
Write-Host ""

# "the next version after this one" -- bump the patch, never append a fourth segment (a four-part
# version is not a version, and the product would (correctly) refuse to parse that manifest).
function Newer([string]$v) {
    $parts = @($v.TrimStart('v').Split('.'))
    while ($parts.Length -lt 3) { $parts += '0' }
    $patch = [int]$parts[2] + 1
    return ('{0}.{1}.{2}' -f $parts[0], $parts[1], $patch)
}

# The product is a WinExe: `& $exe` returns immediately, so every launch goes through
# Start-Process -Wait. Evidence lines land in the log as well as on stderr.
function Invoke-Build {
    param([string[]]$Arguments, [string]$Tag = 'run')
    $log = Join-Path $Work ("$Tag.log")
    $out = Join-Path $Work ("$Tag.out")
    $err = Join-Path $Work ("$Tag.err")
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $text = ''
    foreach ($f in @($out, $err, $log)) {
        if (Test-Path -LiteralPath $f) { $text += (Get-Content -LiteralPath $f -Raw -Encoding UTF8) + "`n" }
    }
    return [pscustomobject]@{ Exit = $p.ExitCode; Text = $text }
}

# ------------------------------------------------------------------- a real HTTP server on loopback
function Get-FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $port = $l.LocalEndpoint.Port
    $l.Stop()
    return $port
}

function Start-ManifestServer {
    param([string]$Body, [int]$Status = 200, [string]$Reason = 'OK')
    $port = Get-FreePort
    $job = Start-Job -ArgumentList $port, $Body, $Status, $Reason -ScriptBlock {
        param($port, $body, $status, $reason)
        $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
        $listener.Start()
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            if (-not $listener.Pending()) { Start-Sleep -Milliseconds 25; continue }
            $client = $listener.AcceptTcpClient()
            $stream = $client.GetStream()
            $reader = New-Object System.IO.StreamReader($stream)
            # Drain the request headers (the product sends one GET; we do not care what it says).
            while ($true) {
                $line = $reader.ReadLine()
                if ($null -eq $line -or $line -eq '') { break }
            }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
            $head = "HTTP/1.1 $status $reason`r`nContent-Type: application/json; charset=utf-8`r`n" +
                    "Content-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"
            $hb = [System.Text.Encoding]::ASCII.GetBytes($head)
            $stream.Write($hb, 0, $hb.Length)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
            $client.Close()
        }
        $listener.Stop()
    }
    # Wait until the socket actually accepts, so the first request cannot beat the listener.
    $ready = $false
    for ($i = 0; $i -lt 100; $i++) {
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', $port)
            $c.Close()
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 50 }
    }
    return [pscustomobject]@{ Job = $job; Port = $port; Url = "http://127.0.0.1:$port/manifest.json"; Ready = $ready }
}

function Stop-ManifestServer($server) {
    if ($null -eq $server) { return }
    Stop-Job -Job $server.Job -ErrorAction SilentlyContinue
    Remove-Job -Job $server.Job -Force -ErrorAction SilentlyContinue
}

function Manifest([string]$v, [string]$extra = '') {
    $json = '{"version":"' + $v + '","url":"https://example.invalid/confern/Confern-Setup-' + $v + '.exe",' +
            '"sha256":"0000000000000000000000000000000000000000000000000000000000000000",' +
            '"notes":"a newer build exists","mandatory":false,"published":"2026-11-03"'
    if ($extra -ne '') { $json += ',' + $extra }
    return $json + '}'
}

# ------------------------------------------------------------------------------------------ U1.1
$noUrl = Invoke-Build @('--check-updates') 'U1.1-no-url'
Check 'U1.1 with no url configured the check refuses to run (exit 4) and says why' `
    ($noUrl.Exit -eq 4 -and $noUrl.Text -match 'no update url') `
    ("exit=$($noUrl.Exit); expected 4 and 'no update url'`n" + $noUrl.Text)

# ------------------------------------------------------------------------------------------ U1.2
$newerVersion = Newer $version
$srv = Start-ManifestServer -Body (Manifest $newerVersion)
try {
    $r = Invoke-Build @('--check-updates', '--update-url', $srv.Url) 'U1.2-newer'
    Check 'U1.2 a newer manifest is reported as state=newer with its version, url and sha256' `
        ($r.Exit -eq 0 -and $r.Text -match 'state=newer' -and
         $r.Text -match ([regex]::Escape("latest=$newerVersion")) -and
         $r.Text -match 'Confern-Setup-' -and $r.Text -match 'sha256=0000000') `
        ("exit=$($r.Exit); expected state=newer latest=$newerVersion`n" + $r.Text)
} finally { Stop-ManifestServer $srv }

# ------------------------------------------------------------------------------------------ U1.3
$srv = Start-ManifestServer -Body (Manifest $version)
try {
    $r = Invoke-Build @('--check-updates', '--update-url', $srv.Url) 'U1.3-current'
    Check 'U1.3 the same version is reported as state=current (no prompt)' `
        ($r.Exit -eq 0 -and $r.Text -match 'state=current' -and $r.Text -notmatch 'state=newer') `
        ("exit=$($r.Exit); expected state=current`n" + $r.Text)
} finally { Stop-ManifestServer $srv }

# ------------------------------------------------------------------------------------------ U1.4
$srv = Start-ManifestServer -Body (Manifest '0.0.1')
try {
    $r = Invoke-Build @('--check-updates', '--update-url', $srv.Url) 'U1.4-older'
    Check 'U1.4 an older published build is state=older (downgrades are never suggested)' `
        ($r.Exit -eq 0 -and $r.Text -match 'state=older' -and $r.Text -notmatch 'state=newer') `
        ("exit=$($r.Exit); expected state=older`n" + $r.Text)
} finally { Stop-ManifestServer $srv }

# ------------------------------------------------------------------------------------------ U1.5
# What the cache holds right now: the last successful answer (0.0.1, from U1.4). The three failures
# below must leave it byte-for-byte alone -- a bad answer is not an answer.
$cacheBeforeFailures = ''
if (Test-Path -LiteralPath $cachePath) { $cacheBeforeFailures = Get-Content -LiteralPath $cachePath -Raw -Encoding UTF8 }
$srv = Start-ManifestServer -Body '{"version": "0.9.9", "url": ' # deliberately truncated JSON
try {
    $r = Invoke-Build @('--check-updates', '--update-url', $srv.Url) 'U1.5-bad-json'
    Check 'U1.5 a body that is not JSON is state=failed and names the reason (not valid JSON)' `
        ($r.Exit -eq 0 -and $r.Text -match 'state=failed' -and $r.Text -match 'not valid JSON') `
        ("exit=$($r.Exit); expected state=failed + 'not valid JSON'`n" + $r.Text)
} finally { Stop-ManifestServer $srv }

# ------------------------------------------------------------------------------------------ U1.6
$srv = Start-ManifestServer -Body '{"error":"boom"}' -Status 500 -Reason 'Internal Server Error'
try {
    $r = Invoke-Build @('--check-updates', '--update-url', $srv.Url) 'U1.6-http-500'
    Check 'U1.6 an HTTP 500 answer is state=failed and quotes the status code' `
        ($r.Exit -eq 0 -and $r.Text -match 'state=failed' -and $r.Text -match 'HTTP 500') `
        ("exit=$($r.Exit); expected state=failed + 'HTTP 500'`n" + $r.Text)
} finally { Stop-ManifestServer $srv }

# ------------------------------------------------------------------------------------------ U1.7
$deadPort = Get-FreePort      # bound, then released: nothing is listening there any more
$r = Invoke-Build @('--check-updates', '--update-url', "http://127.0.0.1:$deadPort/manifest.json") 'U1.7-dead'
Check 'U1.7 nothing listening is state=failed, exit 0 (a network failure is not a product failure)' `
    ($r.Exit -eq 0 -and $r.Text -match 'state=failed' -and $r.Text -match 'could not reach|refused') `
    ("exit=$($r.Exit); expected state=failed + exit 0`n" + $r.Text)

# ------------------------------------------------------------------------------------------ U1.8
# Two halves of one rule: a good answer is kept, and a bad answer never overwrites it.
$cached = ''
if (Test-Path -LiteralPath $cachePath) { $cached = Get-Content -LiteralPath $cachePath -Raw -Encoding UTF8 }
$keptAnswer = ($cached -match '"latest"\s*:\s*"0\.0\.1"') -and ($cached -match 'checkedUtc')
$notOverwritten = ($cached -eq $cacheBeforeFailures)
Check 'U1.8 a good answer is cached, and none of the three failures overwrote it' `
    ($keptAnswer -and $notOverwritten) `
    ("keptAnswer=$keptAnswer notOverwritten=$notOverwritten`n-- now --`n$cached`n-- before the failures --`n$cacheBeforeFailures")

# ------------------------------------------------------------------------------------------ U1.9
# The resident path must NOT ask again inside 24 hours: a fresh cache short-circuits the network.
# The url here points at a dead port on purpose -- if the product ignored the cache it would fail.
$cacheObj = @{
    latest = $newerVersion
    url = 'https://example.invalid/confern/Confern-Setup.exe'
    sha256 = ''
    notes = 'cached'
    mandatory = $false
    checkedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notifiedVersion = ''
} | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($cachePath, $cacheObj, (New-Object System.Text.UTF8Encoding($false)))

$settingsPath = Join-Path $Work 'settings.json'
$deadPort2 = Get-FreePort
$settings = @{ UpdateUrl = "http://127.0.0.1:$deadPort2/manifest.json"; CheckUpdates = $true } | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($settingsPath, $settings, (New-Object System.Text.UTF8Encoding($false)))

$logPath = Join-Path $Work 'U1.9-resident.log'
$resident = Invoke-Build @('--no-autostart', '--tray-exit-after', '2500',
                           '--settings-file', $settingsPath, '--log', $logPath) 'U1.9-resident'
Check 'U1.9 the resident uses a fresh cached answer instead of asking again (no network)' `
    ($resident.Text -match 'using a cached answer' -and $resident.Text -notmatch 'update: checking') `
    ("expected 'using a cached answer' and no 'update: checking'`n" + $resident.Text)

# ----------------------------------------------------------------------------------------- U1.10
Check 'U1.10 the resident announces the cached newer version exactly once, via the tray balloon' `
    ($resident.Text -match 'update: state=newer' -and $resident.Text -match 'update: balloon shown=True') `
    ("expected state=newer and 'balloon shown=True'`n" + $resident.Text)

# ----------------------------------------------------------------------------------------- U1.11
# 2026-11-04: `--check-updates` runs BEFORE the settings load (it has to: it must work on a machine
# with no tray, no monitors, nothing), so the url it uses came from `_settings`, which was still
# null -- meaning the ONLY thing that ever reads the url the installer seeds into settings.json was
# the resident, and the diagnostic command said "no url configured" while pointing at a file that
# clearly had one. That is worse than broken: it is a diagnostic that lies in the direction of
# "nothing is set up", which is exactly what a user runs it to find out.
$seedSrv = Start-ManifestServer -Body (Manifest $newerVersion)
$seedSettings = Join-Path $Work 'U1.11-settings.json'
$seedJson = '{"UpdateUrl":"' + $seedSrv.Url + '"}'
[System.IO.File]::WriteAllText($seedSettings, $seedJson, (New-Object System.Text.UTF8Encoding($false)))
$fromSettings = Invoke-Build @('--check-updates', '--settings-file', $seedSettings) 'U1.11-from-settings'
$missingSettings = Invoke-Build @('--check-updates', '--settings-file', (Join-Path $Work 'U1.11-does-not-exist.json')) 'U1.11-missing-settings'
Check 'U1.11 the url is read from the settings file (and an absent one is not invented)' `
    ($fromSettings.Exit -eq 0 -and $fromSettings.Text.Contains("update: url='$($seedSrv.Url)'") -and
     $missingSettings.Exit -eq 4) `
    ("settings-file run: exit=$($fromSettings.Exit) (want 0 and url='$($seedSrv.Url)'), " +
     "missing-file run: exit=$($missingSettings.Exit) (want 4)`n" + $fromSettings.Text)
Stop-ManifestServer $seedSrv

# ----------------------------------------------------------------------------------- what was not touched
Expect 11
$realCacheAfter = Get-FileFingerprint $realCachePath
if ($realCacheAfter -ne $realCacheBefore) {
    Write-Host "FATAL: the user's own update cache changed during this suite" -ForegroundColor Red
    Write-Host "  before: $realCacheBefore"
    Write-Host "  after : $realCacheAfter"
    exit 3
}
Write-Host ""
Write-Host "the user's own update cache is untouched: $realCacheAfter"
Write-Host ""
Write-Host "summary: checks=$checks pass=$pass fail=$fail inconclusive=$inconclusive"
if ($fail -gt 0) {
    Write-Host "failures:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
exit 0
