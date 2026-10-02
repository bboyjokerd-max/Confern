param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1f-verify"
)
# =============================================================================
#  M1f regression (S14): the settings file (spec 8) -- can it be saved, does a
#  change round-trip, and does it actually TAKE EFFECT in the running app.
#
#  S13 (clamping out-of-range values, falling back to defaults on a broken file)
#  lives in verify-m1d.ps1; this suite starts where that one stops: the file was
#  readable but nothing could WRITE it, and three of the nine spec settings were
#  never consulted at all (the trigger key and the capture timeout were wired to
#  command-line flags only, so editing settings.json looked like it worked).
#
#  ASCII-only on purpose: PS 5.1 reads BOM-less scripts as ANSI and would mangle
#  CJK literals. No CJK expectation is needed here, so none is built.
#
#  exit 0 = all pass, 1 = at least one FAIL, 2 = zero checks ran,
#  3 = inconclusive (a precondition could not be established)
# =============================================================================
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue   # S14.32 counts colours in the dump

$script:Pass = 0
$script:Fail = 0
$script:Inconclusive = 0
$script:Checks = 0

function Check($name, $ok, $detail) {
  $script:Checks++
  if ($ok) { $script:Pass++; "  PASS  $name$(if ($detail) { "  [$detail]" })" }
  else { $script:Fail++; "  FAIL  $name$(if ($detail) { "  [$detail]" })" }
}
function Inconclusive($name, $why) {
  $script:Checks++
  $script:Inconclusive++
  "  INCONCLUSIVE  $name  [$why]"
}

# P1-2 (2026-10-02 falsifiability audit): how many checks this run MUST register.
# Every Check call in this file is unconditional, so this is a single number: a block
# that silently stops registering turns the run red instead of printing N/N.
$script:ExpectedChecks = 34
function Expect([int]$n) { $script:ExpectedChecks += $n }

# P1-3 (2026-10-02): the runner's own window instrument. S14.24 used to assert
# `SettingsUi.IsOpen`, which is true after the FIRST open succeeds whatever the second
# open did -- delete the singleton guard entirely and that check stayed green. Asking the
# OS how many visible top-level windows the process owns can only move if a second window
# really exists.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiProbe {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  /// Visible top-level windows owned by pid. A page that was never shown cannot be counted.
  public static int VisibleTopLevel(uint pid) {
    int n = 0;
    EnumProc cb = delegate(IntPtr h, IntPtr p) {
      uint wpid; GetWindowThreadProcessId(h, out wpid);
      if (wpid == pid && IsWindowVisible(h)) n++;
      return true;
    };
    EnumWindows(cb, IntPtr.Zero); GC.KeepAlive(cb);
    return n;
  }
}
'@

"=== setup ==="
if (-not (Test-Path -LiteralPath $Exe)) { "executable not found: $Exe"; exit 2 }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$logDir = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$script:counter = 0

# The user's own settings file must survive this suite untouched: if a test can
# change the real configuration, every later run of every suite becomes a
# different experiment (and "the defaults" stop meaning anything).
$realSettings = Join-Path $env:LOCALAPPDATA 'Confern\settings.json'
$realBefore = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings -Algorithm SHA256).Hash } else { 'absent' }

function Invoke-Sidecar([string[]]$a, [string]$tag) {
  $script:counter++
  $log = Join-Path $logDir "$tag-$($script:counter).log"
  $full = $a + @('--quiet', '--log', $log)
  $p = Start-Process -FilePath $Exe -ArgumentList $full -PassThru -Wait -WindowStyle Hidden
  return @{ Exit = $p.ExitCode; Log = $log; Text = (Get-Content -LiteralPath $log -Encoding UTF8) -join "`n" }
}
function Get-Field([string]$text, [string]$name) {
  $m = [regex]::Match($text, [regex]::Escape($name) + '=(\S+)')
  if ($m.Success) { return $m.Groups[1].Value }
  return ''
}
function Get-SettingResult([string]$text) {
  $m = [regex]::Match($text, 'settings (?:probe result|set result): (.*)')
  if ($m.Success) { return $m.Groups[1].Value.Trim() }
  return ''
}

"=== S14.1-S14.7 save, round-trip, and the right to overwrite ==="
$missing = Join-Path $Work 'does-not-exist.json'
$r = Invoke-Sidecar @('--settings-probe', $missing) 'probe-missing'
$result = Get-SettingResult $r.Text
Check 'S14.1 no settings file -> still runs on spec defaults' ($r.Exit -eq 0 -and $result -ne '') "exit=$($r.Exit)"
Check 'S14.2 the defaults ARE the spec 8 values' (
  (Get-Field $result 'jpgQuality') -eq '85' -and (Get-Field $result 'webpQuality') -eq '85' -and
  (Get-Field $result 'capture') -eq '30' -and (Get-Field $result 'trigger') -eq 'Space' -and
  (Get-Field $result 'startWithWindows') -eq 'True' -and (Get-Field $result 'language') -eq 'system') $result

$s = Join-Path $Work 'settings.json'
$r = Invoke-Sidecar @('--settings-set', 'jpgQuality=91;triggerKey=VK_TAB;capture=45', '--settings-file', $s) 'set-1'
Check 'S14.3 a change is accepted and the file appears' ((Test-Path -LiteralPath $s) -and $r.Exit -eq 0) "exit=$($r.Exit)"
Check 'S14.4 the log says what was applied' ($r.Text -match 'settings set done: applied=3 rejected=0') ''

$r = Invoke-Sidecar @('--settings-probe', $s) 'probe-roundtrip'
$result = Get-SettingResult $r.Text
Check 'S14.5 what was written is what loads back' (
  (Get-Field $result 'jpgQuality') -eq '91' -and (Get-Field $result 'trigger') -eq 'Tab' -and
  (Get-Field $result 'capture') -eq '45') $result

# Overwrite is the whole point of this path: a settings file always already
# exists on the second save. AtomicWriter is "never overwrite" by default (that
# is the output-file red line), so this check is what proves the settings path
# asked for overwrite explicitly instead of throwing on the second save.
$r = Invoke-Sidecar @('--settings-set', 'webpQuality=77', '--settings-file', $s) 'set-2-overwrite'
$r2 = Invoke-Sidecar @('--settings-probe', $s) 'probe-after-overwrite'
$result = Get-SettingResult $r2.Text
Check 'S14.6 saving over an existing file works and replaces it' (
  $r.Exit -eq 0 -and (Get-Field $result 'webpQuality') -eq '77') "set exit=$($r.Exit)"
Check 'S14.7 the first values survived the overwrite' ((Get-Field $result 'jpgQuality') -eq '91') $result

"=== S14.8-S14.11 the file itself ==="
$r = Invoke-Sidecar @('--settings-set', 'jpgQuality=200;capture=9999', '--settings-file', $s) 'set-clamp'
$raw = Get-Content -LiteralPath $s -Raw -Encoding UTF8
Check 'S14.8 out-of-range values are clamped BEFORE they are written' (
  ($raw -match '"JpgQuality":\s*100') -and ($raw -match '"CaptureTimeoutSeconds":\s*120')) ''
Check 'S14.9 clamping is reported, not silent' ($r.Text -match 'clamped JpgQuality 200 -> 100') ''

$obj = $null
try { $obj = $raw | ConvertFrom-Json } catch { }
$keys = @('JpgQuality','WebpQuality','TriggerKey','StartWithWindows','Language','NamingCollision','CaptureTimeoutSeconds','EmergencyHotkey','Diagnostics')
$present = 0
if ($obj) { foreach ($k in $keys) { if ($obj.PSObject.Properties.Name -contains $k) { $present++ } } }
Check 'S14.10 the saved file is valid JSON holding all nine spec keys' ($present -eq 9) "keys=$present/9"
Check 'S14.11 the file stays human-readable (no \uXXXX escaping)' ($raw -match 'Ctrl\+Alt\+Shift\+Q') ''
Check 'S14.12 no .tmp-* residue next to the settings file' ((@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File -ErrorAction SilentlyContinue).Count) -eq 0) ''

"=== S14.13-S14.16 refusals are loud, and a typo is not silent ==="
$r = Invoke-Sidecar @('--settings-set', 'jpgQualitee=90', '--settings-file', $s) 'reject-unknown-key'
Check 'S14.13 an unknown key is refused with exit 4' ($r.Exit -eq 4 -and $r.Text -match "reject unknown key 'jpgQualitee'") "exit=$($r.Exit)"

$r = Invoke-Sidecar @('--settings-set', 'triggerKey=Ctrl+Shift', '--settings-file', $s) 'reject-chord'
Check 'S14.14 a trigger chord is refused and EXPLAINED (spec 8 gap, not a silent no-op)' (
  $r.Exit -eq 4 -and $r.Text -match 'modifier combinations are not implemented') "exit=$($r.Exit)"

$r = Invoke-Sidecar @('--settings-set', 'namingCollision=overwrite', '--settings-file', $s) 'reject-fixed'
Check 'S14.15 the MVP-fixed setting is refused with its spec reason' (
  $r.Exit -eq 4 -and $r.Text -match 'fixed to auto-suffix') "exit=$($r.Exit)"

$typo = Join-Path $Work 'settings-typo.json'
Set-Content -LiteralPath $typo -Encoding UTF8 -Value '{ "JpgQuality": 90, "TriggerrKey": "Tab" }'
$r = Invoke-Sidecar @('--settings-probe', $typo) 'probe-typo'
$result = Get-SettingResult $r.Text
Check 'S14.16 a mistyped key is called out instead of silently defaulting' (
  $r.Text -match "unknown key 'TriggerrKey'" -and (Get-Field $result 'jpgQuality') -eq '90') ''

"=== S14.17-S14.20 the settings actually reach the running app ==="
$s2 = Join-Path $Work 'settings-live.json'
Invoke-Sidecar @('--settings-set', 'triggerKey=VK_TAB;capture=45', '--settings-file', $s2) 'set-live' | Out-Null
$r = Invoke-Sidecar @('--settings-file', $s2, '--cycle-test', '1') 'live-settings'
Check 'S14.17 the trigger key from the file reaches the detector' ($r.Text -match 'settings in effect: trigger=VK 0x09') ''
Check 'S14.18 the capture timeout from the file reaches the guard' ($r.Text -match 'watchdog=45000 ms') ''
Check 'S14.19 the evidence says WHERE the values came from' ($r.Text -match 'trigger from settings file, watchdog from settings file') ''

$r = Invoke-Sidecar @('--settings-file', $s2, '--trigger-key', 'VK_SPACE', '--cycle-test', '1') 'live-precedence'
Check 'S14.20 a command-line flag still wins over the file' (
  $r.Text -match 'settings in effect: trigger=VK 0x20' -and $r.Text -match 'trigger from command line') ''

"=== S14.21 the Shift warning required by spec 8 ==="
$s3 = Join-Path $Work 'settings-shift.json'
$r = Invoke-Sidecar @('--settings-set', 'triggerKey=Shift', '--settings-file', $s3) 'set-shift'
$raw3 = Get-Content -LiteralPath $s3 -Raw -Encoding UTF8
Check 'S14.21 choosing Shift warns about Explorer MOVE (spec 8) and still saves' (
  $r.Exit -eq 0 -and $r.Text -match 'WARNING' -and $r.Text -match 'MOVE' -and $raw3 -match '"TriggerKey":\s*"Shift"') "exit=$($r.Exit)"

"=== S14.23-S14.27 the page itself: lifecycle, cross-thread, in-app open ==="
# The page runs on its own WPF thread inside this very process. Its two realistic failure modes are
# invisible to the eye: "opens but cannot be closed" and "closing leaves the process unable to exit".
# The second one shows up as a hung command line, so every launch here is bounded and killed on timeout.
$uiWork = Join-Path $Work 'ui'
New-Item -ItemType Directory -Force -Path $uiWork | Out-Null
$uiSettings = Join-Path $uiWork 'settings.json'
$uiLog = Join-Path $uiWork 'selftest.log'
Get-Process -Name Confern -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-selftest', '--settings-file', $uiSettings, '--quiet', '--log', $uiLog) -PassThru -WindowStyle Hidden
# Sample the window count WHILE the self-test runs. It opens the page, asks for a second
# page, presses Save, then closes -- and only a sample taken during that window can see a
# second window if the singleton guard is gone. Bounded by the same 20 s as the launch.
$maxVisibleWindows = 0
$sawVisibleWindows = $false
$uiSamples = 0
while (-not $p.HasExited -and $sw.ElapsedMilliseconds -lt 20000) {
  $n = [UiProbe]::VisibleTopLevel([uint32]$p.Id)
  $uiSamples++
  if ($n -gt $maxVisibleWindows) { $maxVisibleWindows = $n }
  if ($n -ge 1) { $sawVisibleWindows = $true }
  Start-Sleep -Milliseconds 20
}
$finished = $p.WaitForExit(5000)
$elapsed = [int]$sw.ElapsedMilliseconds
if (-not $finished) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
$uiText = if (Test-Path -LiteralPath $uiLog) { Get-Content -LiteralPath $uiLog -Raw -Encoding UTF8 } else { '' }

Check 'S14.23 the settings page opens' ($finished -and $p.ExitCode -eq 0 -and $uiText -match 'opened=True') "exit=$($p.ExitCode)"
Check 'S14.24 the OS never saw more than ONE settings window (two would silently overwrite each other)' `
  ($sawVisibleWindows -and ($maxVisibleWindows -eq 1) -and
   $uiText -match 'second open refused \(still exactly one window\)=True') `
  "maxVisibleWindows=$maxVisibleWindows samples=$uiSamples sawAWindow=$sawVisibleWindows"
Check 'S14.25 it closes cleanly' ($uiText -match 'closed=True') ''
Check 'S14.26 closing it does not leave the process hanging (bounded launch)' ($finished -and $elapsed -lt 20000) "finished=$finished elapsed=${elapsed}ms"
Check 'S14.27 the page thread logs through the main thread, not around it' (
  $uiText -match 'settings ui: STA thread started' -and $uiText -match 'settings ui selftest done: ok=True' -and
  $uiText -match 'settings ui: open requested') ''

$appLog = Join-Path $uiWork 'in-app.log'
Get-Process -Name Confern -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
# --no-autostart: --settings-ui-live is the RESIDENT path (tray included), and the resident path
# applies the startWithWindows setting, whose spec default is ON. Without this flag this run would
# write the user's Run key with a path into bin\Release -- checked, not assumed: it did.
$app = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-live', '--no-autostart', '--quiet', '--log', $appLog) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 5
$appAlive = -not $app.HasExited
if ($appAlive) { $app.Kill(); $app.WaitForExit(5000) | Out-Null }
$appText = if (Test-Path -LiteralPath $appLog) { Get-Content -LiteralPath $appLog -Raw -Encoding UTF8 } else { '' }
Check 'S14.28 --settings-ui-live opens the page inside the running app' (
  $appText -match 'settings ui: opened in the running app') "app stayed alive=$appAlive"

# The standalone entry is the ONLY way a user can reach the settings page until the tray exists
# (step 4), so it has to behave like a normal settings window: no overlay, no background process left.
$pageLog = Join-Path $uiWork 'standalone.log'
$page = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui', '--settings-file', $uiSettings, '--quiet', '--log', $pageLog) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 4
# Ask the OS again instead of reading the cached Process object's HasExited: a stale property here
# would report "exited" for a process that is running, which is the wrong reason to fail a check.
$pageWaiting = @(Get-Process -Id $page.Id -ErrorAction SilentlyContinue).Count -gt 0
$pageText = if (Test-Path -LiteralPath $pageLog) { Get-Content -LiteralPath $pageLog -Raw -Encoding UTF8 } else { '' }
$pageKilled = $false
if ($pageWaiting) { $page.Kill(); $page.WaitForExit(5000) | Out-Null; $pageKilled = $true }
Check 'S14.29 --settings-ui shows the page alone and keeps no overlay running' (
  $pageWaiting -and $pageText -match 'settings ui standalone: open' -and
  $pageText -notmatch 'hit layer|visual layer SHOWN') "pid=$($page.Id) alive=$pageWaiting killed=$pageKilled"

"=== S14.30-S14.32 the page's Save button, end to end ==="
# The page's own Save path is what a user actually exercises, and until now nothing in the repo could
# reproduce it (the original harness was a throwaway). SelfTestSave presses the REAL Save button; the
# file is then read back with the product's own reader, because a self-report is not evidence.
$r = Invoke-Sidecar @('--settings-probe', $uiSettings) 'probe-after-page-save'
$result = Get-SettingResult $r.Text
Check 'S14.30 the page Save button wrote the file' (
  $uiText -match 'save via the real Save button ok=True' -and
  (Get-Field $result 'jpgQuality') -eq '71' -and (Get-Field $result 'trigger') -eq 'Tab' -and
  (Get-Field $result 'capture') -eq '45' -and (Get-Field $result 'language') -eq 'en') $result
Check 'S14.31 an invalid value is refused instead of written' (
  $uiText -match 'an invalid value is refused \(save ok=False\)' -and
  (Get-Field $result 'jpgQuality') -eq '71' -and (Get-Field $result 'trigger') -eq 'Tab') "jpgQuality=$((Get-Field $result 'jpgQuality'))"

$png = Join-Path $uiWork 'settings-page.png'
$dumpLog = Join-Path $uiWork 'dump.log'
$d = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump', $png, '--quiet', '--log', $dumpLog) -PassThru -WindowStyle Hidden
$dumpFinished = $d.WaitForExit(20000)
if (-not $dumpFinished) { $d.Kill(); $d.WaitForExit(5000) | Out-Null }
$pngBytes = 0; $pngW = 0; $pngH = 0; $colours = -1
if (Test-Path -LiteralPath $png) {
  $pngBytes = (Get-Item -LiteralPath $png).Length
  $bmp = [System.Drawing.Bitmap]::FromFile($png)
  $pngW = $bmp.Width; $pngH = $bmp.Height
  $seen = New-Object 'System.Collections.Generic.HashSet[int]'
  for ($y = 0; $y -lt $pngH; $y += 6) {
    for ($x = 0; $x -lt $pngW; $x += 6) { [void]$seen.Add($bmp.GetPixel($x, $y).ToArgb()) }
  }
  $colours = $seen.Count
  $bmp.Dispose()
}
Check 'S14.32 the page renders to a PNG (a real frame, not a blank one)' (
  $dumpFinished -and $d.ExitCode -eq 0 -and $pngBytes -gt 5000 -and $pngW -gt 400 -and $colours -gt 50) `
  "size=${pngBytes}B ${pngW}x${pngH} colours=$colours"

"=== S14.34-S14.35 diagnostics: OFF means essential events only, not silence ==="
# The semantics were settled by the user on 2026-10-02: OFF = essential events only, ON = plus the
# high-frequency detail lines. Both directions are asserted here, because a switch that is only ever
# exercised in one direction is a switch nobody tested. The run must be one that really shows the
# progress line without a mouse, which is why it uses --wheel-payload: that commits a real conversion
# through the worker process, so the presenter actually ticks.
$diagSrc = Join-Path $uiWork 'diag-sample.png'
$bmp = New-Object System.Drawing.Bitmap 48,48
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.Clear([System.Drawing.Color]::Teal)
$gfx.Dispose()
$bmp.Save($diagSrc, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$cfgOff = Join-Path $uiWork 'diag-off.json'
$cfgOn = Join-Path $uiWork 'diag-on.json'
Invoke-Sidecar @('--settings-set', 'diagnostics=false', '--settings-file', $cfgOff) 'set-diag-off' | Out-Null
Invoke-Sidecar @('--settings-set', 'diagnostics=true', '--settings-file', $cfgOn) 'set-diag-on' | Out-Null

$runOff = Invoke-Sidecar @('--settings-file', $cfgOff, '--no-autostart', '--wheel-payload', $diagSrc, '--progress-trace') 'diag-off-run'
$runOn = Invoke-Sidecar @('--settings-file', $cfgOn, '--no-autostart', '--wheel-payload', $diagSrc, '--progress-trace') 'diag-on-run'
$ticksOff = ([regex]::Matches($runOff.Text, 'progress tick')).Count
$ticksOn = ([regex]::Matches($runOn.Text, 'progress tick')).Count
Check 'S14.34 diagnostics OFF keeps essential events and drops the very detail lines that ON shows' (
  $runOff.Text -match 'diagnostics=off \(essential events only\)' -and
  $runOff.Text -match 'conversion finished:' -and
  $runOff.Text -match 'note: --progress-trace is ON but diagnostics is OFF' -and
  $ticksOff -eq 0 -and
  # P1-7 (2026-10-02): `ticksOff -eq 0` alone is "counted zero and called it success" -- a
  # presenter that never ticked at all, in BOTH runs, satisfied it. The same assertion must
  # hold in both directions, so the ON run is required here as well.
  $ticksOn -ge 1) "essential=kept detail_ticks_off=$ticksOff detail_ticks_on=$ticksOn"
Check 'S14.35 diagnostics ON brings the detail lines back' (
  $runOn.Text -match 'diagnostics: off -> on \(detail events included\)' -and
  $ticksOn -ge 1) "detail_ticks=$ticksOn"

"=== S14.36 this suite did not touch the real configuration ==="
$realAfter = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings -Algorithm SHA256).Hash } else { 'absent' }
Check 'S14.36 the real settings file is byte-identical afterwards' ($realBefore -eq $realAfter) "$realBefore -> $realAfter"

""
"M1f summary: $($script:Pass)/$($script:Checks) checks passed"
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive) expected=$($script:ExpectedChecks)"
if ($script:Checks -eq 0) { exit 2 }
# P1-2: fewer checks than promised = a block silently did not register. Red, not N/N.
if ($script:Checks -ne $script:ExpectedChecks) {
  "FATAL: registered $($script:Checks) checks but this suite must register exactly $($script:ExpectedChecks)."
  "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
if ($script:Fail -gt 0) { exit 1 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
