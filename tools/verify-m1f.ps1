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

# 量一個「列」的**像素對比**：最亮的像素 vs 最暗的像素（WCAG 相對亮度）。
# 為什麼不是「不准接近白色」：2026-10-21 第一次寫守衛時這樣寫，然後在淺色配色（porcelain）
# 上失敗——那一套的列本來就是淺底深字（實測 240 的列平均亮度），是對的。
# 真正的不變式是「字跟底分得開」，與哪一邊亮無關；白底白字量出來是 1.0:1，深淺都會被抓到。
function RowPixelContrast($bmp, [int]$y0, [int]$y1) {
  $minL = 2.0; $maxL = -1.0
  for ($y = $y0; $y -le $y1; $y++) {
    for ($x = 0; $x -lt $bmp.Width; $x += 2) {
      $px = $bmp.GetPixel($x, $y)
      $f = { param($v) $s = $v / 255.0; if ($s -le 0.03928) { $s / 12.92 } else { [Math]::Pow(($s + 0.055) / 1.055, 2.4) } }
      $l = 0.2126 * (& $f $px.R) + 0.7152 * (& $f $px.G) + 0.0722 * (& $f $px.B)
      if ($l -lt $minL) { $minL = $l }
      if ($l -gt $maxL) { $maxL = $l }
    }
  }
  if ($maxL -lt 0) { return 0.0 }
  return ($maxL + 0.05) / ($minL + 0.05)
}

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

# ---- S14.32b-d: the OPEN dropdown list -------------------------------------------------------
# On 2026-10-21 the user reported "the dropdown is white on white". That bug survived a full
# 412-check sweep for one very specific reason: the list is a separate Popup window, and
# --settings-ui-dump only captures the main window, so nothing had ever looked at it.
#
# These checks look at PIXELS, not at the colours we declare, and that distinction is the whole
# point: before the fix the evidence line read
#     popup item 1: bg=#15171B fg=#FFFFFF contrast=17.9:1
# which is perfectly healthy. The default WPF ComboBoxItem style rewrites the background of the
# item's TEMPLATE on IsSelected/IsHighlighted, so it never touches the property and the declared
# colour stays right while the drawn one turns white. Only a pixel measurement catches that.
$popupPng = Join-Path $uiWork 'dropdown.png'
$popupLog = Join-Path $uiWork 'dropdown.log'
$popupProc = Start-Process -FilePath $Exe -ArgumentList @(
  '--settings-ui-dump-popup', $popupPng, '--settings-ui-popup-box', 'LanguageBox',
  '--quiet', '--log', $popupLog) -PassThru -WindowStyle Hidden
$popupFinished = $popupProc.WaitForExit(20000)
if (-not $popupFinished) { $popupProc.Kill(); $popupProc.WaitForExit(5000) | Out-Null }

$popupBytes = 0; $popupW = 0; $popupH = 0; $worstRow = 0
$itemLines = @()
if (Test-Path -LiteralPath $popupLog) {
  $itemLines = @(Get-Content -LiteralPath $popupLog | Where-Object { $_ -match 'popup item \d+:' })
}
if (Test-Path -LiteralPath $popupPng) {
  $popupBytes = (Get-Item -LiteralPath $popupPng).Length
  $popupBmp = [System.Drawing.Bitmap]::FromFile($popupPng)
  $popupW = $popupBmp.Width; $popupH = $popupBmp.Height
  # Mean luminance per row. A white-on-white row measures ~243; the healthy rows measure 51-60
  # and the selected row 123 (measured before/after the fix, not guessed).
  if ($itemLines.Count -gt 0 -and $popupH -gt 0) {
    $rowH = [int]($popupH / $itemLines.Count)
    if ($rowH -lt 1) { $rowH = 1 }
    for ($r = 0; $r -lt $itemLines.Count; $r++) {
      $y0 = $r * $rowH
      $y1 = [Math]::Min($y0 + $rowH - 1, $popupH - 1)
      $lsum = 0; $lcnt = 0
      for ($y = $y0; $y -le $y1; $y++) {
        for ($x = 0; $x -lt $popupW; $x++) {
          $px = $popupBmp.GetPixel($x, $y)
          $lsum += [int](($px.R + $px.G + $px.B) / 3); $lcnt++
        }
      }
      if ($lcnt -gt 0) {
        $mean = [int]($lsum / $lcnt)
        if ($mean -gt $worstRow) { $worstRow = $mean }
      }
    }
  }
  $popupBmp.Dispose()
}

Check 'S14.32b the open dropdown renders to its own PNG (the list lives in a separate Popup window)' (
  $popupFinished -and $popupProc.ExitCode -eq 0 -and $popupBytes -gt 800 -and
  $popupW -gt 100 -and $itemLines.Count -ge 2) `
  "size=${popupBytes}B ${popupW}x${popupH} items=$($itemLines.Count)"

$minContrast = 99.0
foreach ($ln in $itemLines) {
  if ($ln -match 'contrast=([0-9.]+):1') {
    $v = [double]$Matches[1]
    if ($v -lt $minContrast) { $minContrast = $v }
  }
}
Check 'S14.32c every dropdown row is legible (WCAG contrast >= 4.5:1)' (
  $itemLines.Count -ge 2 -and $minContrast -ge 4.5) "minContrast=${minContrast}:1 items=$($itemLines.Count)"

# 像素層的對比：白底白字（或淺底白字）在這裡是 1.0:1，宣告的顏色再怎麼正確也躲不掉。
$minRowPx = 99.0
if (Test-Path -LiteralPath $popupPng) {
  $rb = [System.Drawing.Bitmap]::FromFile($popupPng)
  $rn = $itemLines.Count
  if ($rn -gt 0 -and $rb.Height -gt 0) {
    $rh = [int]($rb.Height / $rn)
    if ($rh -lt 1) { $rh = 1 }
    for ($r = 0; $r -lt $rn; $r++) {
      $c0 = $r * $rh
      $c1 = [Math]::Min($c0 + $rh - 1, $rb.Height - 1)
      $v = RowPixelContrast $rb $c0 $c1
      if ($v -lt $minRowPx) { $minRowPx = $v }
    }
  }
  $rb.Dispose()
}
Check 'S14.32d every dropdown row really has legible pixels (guards the 2026-10-21 white-on-white bug)' (
  $minRowPx -lt 99.0 -and $minRowPx -ge 4.5) `
  "minRowPixelContrast=$([Math]::Round($minRowPx, 1)):1 (white-on-white measures 1.0:1; healthy rows 13-19:1 in every direction)"
Expect 3

# ---- S14.32e-g: EVERY selectable palette, not just the one this machine happens to use -------
# 2026-10-21: the first fix for white-on-white selected SelectedBg for the highlighted row. It
# measured 5.8:1 on midnight (this box's palette) and was fine — but the user's real settings are
# graphite, where SelFill is a mid grey #787B7E and white text sits at 4.3:1, below WCAG AA.
# A guard that only ever looks at one palette cannot see that. So: dump the dropdown in all six
# and require every one of them to pass. porcelain (the light palette, exposed the same day) is the
# one that would have broken silently too - its "dim" tokens had to be re-derived toward black.
$allPalettes = @('midnight', 'graphite', 'citrus', 'celadon', 'plum', 'porcelain')
$perPalette = @()
$worstAll = 99.0;   $worstAllPal = ''
$brightest = 99.0;  $brightestPal = ''
$minItems = 99
foreach ($pal in $allPalettes) {
  $palFile = Join-Path $uiWork "palette-$pal.json"
  Set-Content -LiteralPath $palFile -Value "{ `"Language`": `"en`", `"Palette`": `"$pal`" }" -Encoding UTF8
  $palPng = Join-Path $uiWork "palette-$pal.png"
  $palLog = Join-Path $uiWork "palette-$pal.log"
  $palProc = Start-Process -FilePath $Exe -ArgumentList @(
    '--settings-ui-dump-popup', $palPng, '--settings-ui-popup-box', 'PaletteBox',
    '--settings-file', $palFile, '--quiet', '--log', $palLog) -PassThru -WindowStyle Hidden
  $palOk = $palProc.WaitForExit(20000)
  if (-not $palOk) { $palProc.Kill(); $palProc.WaitForExit(5000) | Out-Null }

  $palItems = @()
  if (Test-Path -LiteralPath $palLog) {
    $palItems = @(Get-Content -LiteralPath $palLog | Where-Object { $_ -match 'popup item \d+:' })
  }
  $palMin = 99.0
  foreach ($ln in $palItems) {
    if ($ln -match 'contrast=([0-9.]+):1') {
      $v = [double]$Matches[1]
      if ($v -lt $palMin) { $palMin = $v }
    }
  }
  if ($palItems.Count -lt $minItems) { $minItems = $palItems.Count }
  if ($palMin -lt $worstAll) { $worstAll = $palMin; $worstAllPal = $pal }

  $palBright = 0
  if (Test-Path -LiteralPath $palPng) {
    $pb = [System.Drawing.Bitmap]::FromFile($palPng)
    $n = $palItems.Count
    if ($n -gt 0 -and $pb.Height -gt 0) {
      $rowH = [int]($pb.Height / $n)
      if ($rowH -lt 1) { $rowH = 1 }
      for ($r = 0; $r -lt $n; $r++) {
        $y0 = $r * $rowH
        $y1 = [Math]::Min($y0 + $rowH - 1, $pb.Height - 1)
        $sum = 0; $cnt = 0
        for ($y = $y0; $y -le $y1; $y++) {
          for ($x = 0; $x -lt $pb.Width; $x++) {
            $px = $pb.GetPixel($x, $y)
            $sum += [int](($px.R + $px.G + $px.B) / 3); $cnt++
          }
        }
        if ($cnt -gt 0) {
          $mean = [int]($sum / $cnt)
          if ($mean -gt $palBright) { $palBright = $mean }
        }
      }
    }
    $pb.Dispose()
  }
  # 像素層的對比（與 S14.32d 同一個量法）：淺底深字與深底淺字都會過，白底白字不會。
  $palPx = 99.0
  if (Test-Path -LiteralPath $palPng) {
    $pq = [System.Drawing.Bitmap]::FromFile($palPng)
    $qn = $palItems.Count
    if ($qn -gt 0 -and $pq.Height -gt 0) {
      $qh = [int]($pq.Height / $qn)
      if ($qh -lt 1) { $qh = 1 }
      for ($r = 0; $r -lt $qn; $r++) {
        $q0 = $r * $qh
        $q1 = [Math]::Min($q0 + $qh - 1, $pq.Height - 1)
        $v = RowPixelContrast $pq $q0 $q1
        if ($v -lt $palPx) { $palPx = $v }
      }
    }
    $pq.Dispose()
  }
  if ($palPx -lt $brightest) { $brightest = $palPx; $brightestPal = $pal }
  $perPalette += "$pal(items=$($palItems.Count) declared=$palMin pixels=$([Math]::Round($palPx, 1)))"
}

Check 'S14.32e every palette lists all six palettes in the dropdown' ($minItems -eq 6) `
  "itemsPerPalette=$($perPalette -join ' ')"
Check 'S14.32f the dropdown is legible in EVERY palette (WCAG >= 4.5:1)' ($worstAll -ge 4.5) `
  "worst=$worstAllPal $worstAll`:1; all=[$($perPalette -join ' ')]"
Check 'S14.32g every palette''s dropdown rows have legible pixels (light palettes included)' `
  ($brightest -lt 99.0 -and $brightest -ge 4.5) `
  "worstPixels=$brightestPal $([Math]::Round($brightest,1)):1 (white-on-white = 1.0:1); porcelain is the light one"
Expect 3

# =============================================================================================
"=== S14.38 the dropdown boxes that are NOT open (the bug that survived two fixes) ==="
# 2026-10-22, the user's third report: "為何設定框裡的下拉選單還是一樣白底白字?"
#
# He was right, and both earlier fixes were aimed at the wrong half. S14.32b-g above measure the
# dropdown LIST -- the popup that opens. That list was already correct (18.9:1 rows). What was
# white-on-white was the box itself, the part that is on screen all the time:
#
#     box                before this fix    after
#     LanguageBox        bg=#E7E7E7 fg=#FFFFFF  1.2:1      bg=#111112 fg=#FFFFFF  18.9:1
#     TriggerPresetBox   bg=#7EB4EA fg=#FFFFFF  2.2:1      bg=#111112 fg=#FFFFFF  18.9:1
#
# Aero2's ComboBox ControlTemplate never paints our Background inside the box; it paints the theme
# surface (light grey, and pale blue on hover), and our text is near-white. So the whole row of six
# dropdowns was illegible while every popup measurement stayed green.
#
# The measurement below takes the box rects from the product (`settings ui combo:` lines) and then
# reads ONLY pixels from the page PNG. Sensitivity is not assumed: the same rects on the previous
# build's page dump read 1.1-2.2:1, i.e. this check would have gone red.
$comboImg = Join-Path $uiWork 'page-after.png'
$comboLog = Join-Path $uiWork 'page-after.log'
# 用真實設定的一份複本：要看到的就是使用者此刻看到的畫面（繁中＋石墨），但絕不寫回他的檔案。
$comboCfg = Join-Path $uiWork 'combo-real.json'
Copy-Item -LiteralPath $realSettings -Destination $comboCfg -Force -EA SilentlyContinue
$comboRun = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump', $comboImg, '--settings-file', $comboCfg, '--log', $comboLog) -PassThru -WindowStyle Hidden
$comboDone = $comboRun.WaitForExit(25000)
if (-not $comboDone) { $comboRun.Kill(); $comboRun.WaitForExit(5000) | Out-Null }
$comboText = if (Test-Path -LiteralPath $comboLog) { Get-Content -LiteralPath $comboLog -Raw -Encoding UTF8 } else { '' }
$combos = @()
foreach ($line in ($comboText -split "`r?`n")) {
  if ($line -match 'settings ui combo: (\S+) rect=(\d+),(\d+),(\d+)x(\d+) selected=''?(.+?)''?$') {
    $combos += [pscustomobject]@{ Name = $Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; H = [int]$Matches[5]; Sel = $Matches[6] }
  }
}
# 去重（同一份 dump 會因為多次 UpdateLayout 印好幾輪）
$combos = @($combos | Sort-Object Name, X, Y -Unique)
$comboBad = @(); $comboDetail = @()
# WCAG 相對亮度。這裡刻意不沿用 RowPixelContrast：那個量的是「整列最暗 vs 最亮」，
# 用在這一格時會把 1px 外框也算進去；下拉框要問的是「框底（最常見的顏色）vs 字（最亮的像素）」。
function LumOfArgb([int]$r, [int]$g, [int]$b) {
  $chan = {
    param([int]$v)
    $s = $v / 255.0
    if ($s -le 0.03928) { return $s / 12.92 }
    return [Math]::Pow(($s + 0.055) / 1.055, 2.4)
  }
  return 0.2126 * (& $chan $r) + 0.7152 * (& $chan $g) + 0.0722 * (& $chan $b)
}
if ($combos.Count -gt 0 -and (Test-Path -LiteralPath $comboImg)) {
  $cBmp = [System.Drawing.Bitmap]::FromFile($comboImg)
  foreach ($c in $combos) {
    $hist = @{}
    for ($y = $c.Y; $y -lt [Math]::Min($c.Y + $c.H, $cBmp.Height); $y++) {
      for ($x = $c.X; $x -lt [Math]::Min($c.X + $c.W, $cBmp.Width); $x++) {
        $px = $cBmp.GetPixel($x, $y)
        $k = ('{0:X2}{1:X2}{2:X2}' -f $px.R, $px.G, $px.B)
        if ($hist.ContainsKey($k)) { $hist[$k] = $hist[$k] + 1 } else { $hist[$k] = 1 }
      }
    }
    $bgKey = ($hist.GetEnumerator() | Sort-Object Value -Descending)[0].Key
    $bgL = LumOfArgb ([Convert]::ToInt32($bgKey.Substring(0,2),16)) ([Convert]::ToInt32($bgKey.Substring(2,2),16)) ([Convert]::ToInt32($bgKey.Substring(4,2),16))
    # 「字」＝與底色亮度差最多的那個顏色，但必須佔至少 0.5% 的像素：
    # 一是免得一格反鋸齒雜點把對比撐高；二是不能只取「最亮的像素」——
    # 那假設了深底淺字。冷瓷是淺底深字（#12161A 疊 #F3F5F6，實測 16.6:1），
    # 用「最亮」去量會得到 1.0:1 的假失敗（2026-10-22 我自己就踩了這個）。
    $minCount = [Math]::Max(8, [int](0.005 * $c.W * $c.H))
    $bestD = -1.0; $fgKey = $bgKey; $fgL = $bgL
    foreach ($e in $hist.GetEnumerator()) {
      if ($e.Value -lt $minCount -or $e.Key -eq $bgKey) { continue }
      $l = LumOfArgb ([Convert]::ToInt32($e.Key.Substring(0,2),16)) ([Convert]::ToInt32($e.Key.Substring(2,2),16)) ([Convert]::ToInt32($e.Key.Substring(4,2),16))
      if ([Math]::Abs($l - $bgL) -gt $bestD) { $bestD = [Math]::Abs($l - $bgL); $fgKey = $e.Key; $fgL = $l }
    }
    $hiL = [Math]::Max($bgL, $fgL); $loL = [Math]::Min($bgL, $fgL)
    $ratio = ($hiL + 0.05) / ($loL + 0.05)
    $comboDetail += ("{0}=bg#{1}/fg#{2}/{3:0.0}:1" -f $c.Name, $bgKey, $fgKey, $ratio)
    if ($ratio -lt 4.5) { $comboBad += ("{0}({1:0.0}:1 bg=#{2} fg=#{3})" -f $c.Name, $ratio, $bgKey, $fgKey) }
  }
  $cBmp.Dispose()
}
Check 'S14.38a every closed dropdown box is legible in the real pixels of the page' `
  ($comboDone -and $comboRun.ExitCode -eq 0 -and $combos.Count -ge 6 -and $comboBad.Count -eq 0) `
  ($(if ($comboBad.Count -gt 0) { 'BAD=' + ($comboBad -join ' ') } else { "boxes=$($combos.Count) all>=4.5:1 " }) + ($comboDetail -join ' '))

# 展開的清單也不能有任何「白底」的列：列底最常見的顏色若是近白，就是白底。
# 這比量「項目對比」更接近使用者看到的東西——上次就是項目一路 18.9:1 而面板其實是白的
# （Border#DropDownBorder bg=#FFFFFF），列與列之間的縫全是白線。
$popupWhite = @()
foreach ($boxName in @('TriggerPresetBox','LanguageBox','PaletteBox','OutputPresetBox','CompressModeBox','CompressUnitBox')) {
  $bp = Join-Path $uiWork ("whitecheck-" + $boxName + ".png")
  $bl = Join-Path $uiWork ("whitecheck-" + $boxName + ".log")
  Remove-Item -LiteralPath $bp -EA SilentlyContinue
  $bpRun = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump-popup', $bp, '--settings-ui-popup-box', $boxName, '--settings-file', $comboCfg, '--log', $bl) -PassThru -WindowStyle Hidden
  $bpDone = $bpRun.WaitForExit(25000)
  if (-not $bpDone) { $bpRun.Kill(); $bpRun.WaitForExit(5000) | Out-Null }
  if (-not (Test-Path -LiteralPath $bp)) { $popupWhite += "$boxName(no png)"; continue }
  $pBmp = [System.Drawing.Bitmap]::FromFile($bp)
  $whiteRows = 0; $firstWhite = @()
  for ($y = 0; $y -lt $pBmp.Height; $y++) {
    $hist2 = @{}
    for ($x = 0; $x -lt $pBmp.Width; $x++) {
      $px2 = $pBmp.GetPixel($x, $y)
      $k2 = ('{0:X2}{1:X2}{2:X2}' -f $px2.R, $px2.G, $px2.B)
      if ($hist2.ContainsKey($k2)) { $hist2[$k2] = $hist2[$k2] + 1 } else { $hist2[$k2] = 1 }
    }
    $top2 = ($hist2.GetEnumerator() | Sort-Object Value -Descending)[0].Key
    if ([Convert]::ToInt32($top2.Substring(0,2),16) -ge 200 -and
        [Convert]::ToInt32($top2.Substring(2,2),16) -ge 200 -and
        [Convert]::ToInt32($top2.Substring(4,2),16) -ge 200) {
      $whiteRows++
      if ($firstWhite.Count -lt 4) { $firstWhite += $y }
    }
  }
  $pBmp.Dispose()
  if ($whiteRows -gt 0) { $popupWhite += ("{0}({1} white rows at y={2})" -f $boxName, $whiteRows, ($firstWhite -join ',')) }
}
Check 'S14.38b no open dropdown has a white background row under the text' `
  ($popupWhite.Count -eq 0) `
  $(if ($popupWhite.Count -gt 0) { 'BAD ' + ($popupWhite -join '; ') } else { 'six dropdowns: 0 white rows each (background is the card surface, not the theme white)' })
Expect 2

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

# =============================================================================================
"=== S14.37 eight languages: the tables align, and no page overflows (wave 2, 2026-10-21) ==="
# The second wave added fr / de / es. Two failure modes here are SILENT, which is exactly why they
# are measured instead of assumed:
#   (1) a table missing a key compiles fine and throws nothing -- the screen simply shows "!key!"
#       in whatever cell nobody looked at;
#   (2) a longer word does not resize the window and raises no error -- the fixed 28px rows just
#       push the control out of the row or let the text run over its neighbour.
# Neither was covered by any earlier check: S17.19 looked only at a scrollbar, and it only ever ran
# in one language.
$selfLog = Join-Path $uiWork 'strings-selftest.log'
$ss = Start-Process -FilePath $Exe -ArgumentList @('--strings-selftest','--quiet','--log',$selfLog) -PassThru -WindowStyle Hidden
$ssDone = $ss.WaitForExit(20000)
if (-not $ssDone) { $ss.Kill(); $ss.WaitForExit(5000) | Out-Null }
$ssText = if (Test-Path -LiteralPath $selfLog) { Get-Content -LiteralPath $selfLog -Raw } else { '' }
$ssLine = (($ssText -split "`r?`n") | Where-Object { $_ -match 'strings selftest:' } | Select-Object -First 1)
Check 'S14.37a all eight language tables hold the same keys and no impossible placeholder' `
  ($ssDone -and $ss.ExitCode -eq 0 -and $ssLine -match 'aligned=True' -and $ssLine -match 'missing=0' `
   -and $ssLine -match 'extra=0' -and $ssLine -match 'badPlaceholders=0') "$ssLine"
# Every key the settings page promises to render must exist in EVERY table. Table-to-table alignment
# alone is not that: vk.Alt was missing from all eight tables at once (2026-10-23), so the alignment
# report stayed perfectly green while the dropdown showed the literal string !vk.Alt!.
Check 'S14.37e every key the settings page renders exists in all eight tables' `
  ($ssLine -match 'requiredKeys=[1-9]\d*' -and $ssLine -match 'missingRequired=0') "$ssLine"

# (b)(c) every language lays out inside its rows, needs no scrollbar, and renders a frame of its own.
$langs = @('zh-Hant','zh-Hans','ja','ko','fr','de','es','en')
$langBad = @(); $langDetail = @(); $langHashes = @{}
foreach ($lang in $langs) {
  $lj = Join-Path $uiWork ("lang-$lang.json")
  Set-Content -LiteralPath $lj -Value "{ `"Language`": `"$lang`", `"Palette`": `"graphite`" }" -Encoding UTF8
  $lp = Join-Path $uiWork ("lang-$lang.png")
  $llog = Join-Path $uiWork ("lang-$lang.log")
  $pr = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump',$lp,'--settings-file',$lj,'--quiet','--log',$llog) -PassThru -WindowStyle Hidden
  $lDone = $pr.WaitForExit(25000)
  if (-not $lDone) { $pr.Kill(); $pr.WaitForExit(5000) | Out-Null }
  $lt = if (Test-Path -LiteralPath $llog) { Get-Content -LiteralPath $llog -Raw } else { '' }
  $lay = (($lt -split "`r?`n") | Where-Object { $_ -match 'settings ui layout:' } | Select-Object -First 1)
  if (-not ($lDone -and $pr.ExitCode -eq 0 -and $lay -match 'scrollbar=False' -and $lay -match 'rowOverflow=0' -and $lay -match 'overlaps=0')) { $langBad += $lang }
  $langDetail += ("{0}:{1}" -f $lang, ($lay -replace '.*settings ui layout: ',''))
  if (Test-Path -LiteralPath $lp) { $langHashes[$lang] = (Get-FileHash -LiteralPath $lp -Algorithm SHA256).Hash }
}
Check 'S14.37b no language pushes a control out of its row, overlaps a neighbour, or needs a scrollbar' `
  ($langBad.Count -eq 0 -and $langHashes.Count -eq $langs.Count) `
  ($(if ($langBad.Count -gt 0) { 'BAD=' + ($langBad -join ',') + ' ' } else { '' }) + ($langDetail -join ' | '))
$distinctLangs = @($langHashes.Values | Select-Object -Unique).Count
Check 'S14.37c the eight languages render eight different pages (each one really used its own table)' `
  ($distinctLangs -eq 8) "distinct=$distinctLangs/8"

# (d) the dropdown must list all eight plus "follow the system" -- a language you cannot select is
# not a language you shipped.
$langPopupPng = Join-Path $uiWork 'lang-dropdown.png'
$langPopupLog = Join-Path $uiWork 'lang-dropdown.log'
$ljFr = Join-Path $uiWork 'lang-fr.json'
$pp = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump-popup',$langPopupPng,'--settings-ui-popup-box','LanguageBox','--settings-file',$ljFr,'--quiet','--log',$langPopupLog) -PassThru -WindowStyle Hidden
$ppDone = $pp.WaitForExit(25000)
if (-not $ppDone) { $pp.Kill(); $pp.WaitForExit(5000) | Out-Null }
$ppText = if (Test-Path -LiteralPath $langPopupLog) { Get-Content -LiteralPath $langPopupLog -Raw } else { '' }
$langItemCount = @(($ppText -split "`r?`n") | Where-Object { $_ -match 'popup item \d+:' }).Count
Check 'S14.37d the language dropdown offers all eight languages plus "follow the system"' `
  ($ppDone -and $pp.ExitCode -eq 0 -and $langItemCount -eq 9) "items=$langItemCount (expected 9)"

# The trigger-key dropdown, dumped through the product's own offscreen renderer: six entries, every
# one of them a real label. `!vk.xxx!` is what UiStrings.Get returns for a key it cannot find, and
# that is exactly what a user would have seen in place of "Alt".
$tkPng = Join-Path $uiWork 'trigger-popup.png'
$tkLog = Join-Path $uiWork 'trigger-popup.log'
$tkJson = Join-Path $uiWork 'trigger-popup.json'
Set-Content -LiteralPath $tkJson -Value '{ "Language": "zh-Hant", "TriggerKey": "Alt" }' -Encoding UTF8
$tkp = Start-Process -FilePath $Exe -ArgumentList @('--settings-ui-dump-popup',$tkPng,'--settings-ui-popup-box','TriggerPresetBox','--settings-file',$tkJson,'--quiet','--log',$tkLog) -PassThru -WindowStyle Hidden
$tkDone = $tkp.WaitForExit(30000)
if (-not $tkDone) { $tkp.Kill(); $tkp.WaitForExit(5000) | Out-Null }
$tkText = if (Test-Path -LiteralPath $tkLog) { Get-Content -LiteralPath $tkLog -Raw } else { '' }
$tkItems = @(($tkText -split "`r?`n") | Where-Object { $_ -match 'popup item \d+: text=' })
$tkMissingMarker = @($tkItems | Where-Object { $_ -match "text='!" })
Check 'S14.37f the trigger dropdown renders six labelled entries, none of them a missing-key marker' `
  ($tkDone -and $tkp.ExitCode -eq 0 -and $tkItems.Count -eq 6 -and $tkMissingMarker.Count -eq 0 `
   -and (Test-Path -LiteralPath $tkPng)) `
  "exit=$($tkp.ExitCode) items=$($tkItems.Count) markers=$($tkMissingMarker.Count)"
$tkAltLabel = (($tkText -split "`r?`n") | Where-Object { $_ -match "text='Alt " } | Select-Object -First 1)
Check 'S14.37g Alt has a real localized label in the dropdown (zh-Hant)' `
  ($null -ne $tkAltLabel) "altLine=$tkAltLabel"
Expect 7

"=== S14.36 this suite did not touch the real configuration ==="
$realAfter = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings -Algorithm SHA256).Hash } else { 'absent' }
Check 'S14.36 the real settings file is byte-identical afterwards' ($realBefore -eq $realAfter) "$realBefore -> $realAfter"

# ------------------------------------------------------------------------------------------------
"=== S14.38 a modifier can be the trigger key (Alt was the request, 2026-10-23) ==="
# WHY THIS EXISTS: the low-level keyboard hook reports the LEFT/RIGHT modifier VK (Alt -> 0xA4/0xA5,
# Ctrl -> 0xA2/0xA3, Shift -> 0xA0/0xA1) while the settings file and the command line carry the
# generic 0x12/0x11/0x10. Until Native.NormalizeModifierVk existed the two never matched: the log
# said 'settings in effect: trigger=VK 0x12' and pressing Alt did nothing at all. Shift, which this
# page has offered since M1, was broken the same way -- nobody had held it down to check.
# The probe below is the re-runnable proof: it resolves a key name, installs the REAL hook, injects
# the key from another thread (the generic VK, exactly what a real keyboard puts on the wire), and
# demands that the detector saw the press AND the release.
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$probeWork = Join-Path $Work 'tkprobe'
New-Item -ItemType Directory -Force -Path $probeWork | Out-Null

function Invoke-KeyProbe([string]$name) {
  $safe = $name -replace '[^A-Za-z0-9]', '_'
  $log = Join-Path $probeWork ("probe-$safe.log")
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $p = Start-Process -FilePath $Exe -ArgumentList @('--trigger-key-probe', $name, '--log', $log) -PassThru -WindowStyle Hidden
  $done = $p.WaitForExit(60000)
  if (-not $done) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
  $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
  [pscustomobject]@{ Exit = $p.ExitCode; Text = $text; Done = $done }
}

$alt = Invoke-KeyProbe 'Alt'
$altPress = (($alt.Text -split "`r?`n") | Where-Object { $_ -match 'PASS  trigger-key-probe: pressing it reaches' } | Select-Object -First 1)
Check 'S14.38 Alt as the trigger key really reaches the detector (probe exit 0)' `
  ($alt.Done -and $alt.Exit -eq 0) "exit=$($alt.Exit)"
Check 'S14.38a the Alt probe reports no FAIL line at all' `
  (-not ($alt.Text -match 'FAIL  trigger-key-probe')) ''
# The load-bearing one: the hook must have sent the SPLIT VK and the detector must still have matched.
# Without the normalization this line does not exist, which is exactly what went unnoticed until now.
Check 'S14.38b the hook really sent the left-Alt VK (0xA4) and it still matched Alt' `
  ($altPress -match '0xA4-down') "$altPress"
Check 'S14.38c releasing Alt reached the detector too' `
  ($alt.Text -match 'PASS  trigger-key-probe: releasing it reaches') ''
Check 'S14.38d the settings page offers Alt (the dropdown and the wiring agree)' `
  ($alt.Text -match 'settings page offers \[[^\]]*\bAlt\b') ''

$ctrl = Invoke-KeyProbe 'Ctrl'
Check 'S14.38e Ctrl works the same way (the hook sends 0xA2)' `
  (($ctrl.Exit -eq 0) -and ($ctrl.Text -match '0xA2-down')) "exit=$($ctrl.Exit)"
$shift = Invoke-KeyProbe 'Shift'
Check 'S14.38f Shift works too -- it was offered since M1 and never worked' `
  (($shift.Exit -eq 0) -and ($shift.Text -match '0xA0-down')) "exit=$($shift.Exit)"
$f8 = Invoke-KeyProbe 'F8'
Check 'S14.38g a non-modifier key is unaffected by the normalization' `
  ($f8.Exit -eq 0) "exit=$($f8.Exit)"
$bad = Invoke-KeyProbe 'NoSuchKey'
Check 'S14.38h an unknown key name is refused with its reason instead of silently accepted' `
  (($bad.Exit -eq 1) -and ($bad.Text -match 'is not a key name this build knows')) "exit=$($bad.Exit)"

"=== S14.39 the command line accepts the same names as the settings file ==="
$r = Invoke-Sidecar @('--trigger-key', 'ALT', '--cycle-test', '1') 'cli-alt'
Check 'S14.39 --trigger-key ALT resolves to VK 0x12 (it used to become Space without a word)' `
  ($r.Text -match 'settings in effect: trigger=VK 0x12' -and $r.Text -match 'trigger from command line') ''
$r = Invoke-Sidecar @('--trigger-key', 'NotAThing', '--cycle-test', '1') 'cli-badkey'
Check 'S14.39a an unusable --trigger-key is called out and falls back to space' `
  ($r.Text -match "WARNING --trigger-key 'NotAThing' unusable") ''
Expect 11

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
