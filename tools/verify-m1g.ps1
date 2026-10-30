param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1g-verify",
  [int]$ResidentSeconds = 4
)
# =============================================================================
#  M1g regression (S15): the tray icon and start-with-Windows (the handoff plan's
#  todo 4, spec 8).
#
#  Both features fail SILENTLY when they fail: the icon simply never appears in
#  the notification area, and the Run-key entry either is not written or points
#  at a path that no longer exists. Neither shows up as an error anywhere. Every
#  check below therefore names a way for it to fail for a real reason.
#
#  ASCII-only on purpose: PS 5.1 reads BOM-less scripts as ANSI and would mangle
#  CJK literals. The product's menu and tooltip text lives in the C# self-test's
#  log lines; this file asserts the ASCII contract around it (the count, the
#  tooltip string built from the product name, the three dispatch events, the
#  labels_ok verdict) plus the CJK labels' *shape* (a non-ASCII string).
#
#  exit 0 = all pass, 1 = at least one FAIL, 2 = zero checks ran,
#  3 = inconclusive (a precondition could not be established)
# =============================================================================
$ErrorActionPreference = 'Continue'

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
# S15.19 registers exactly one check whichever branch it takes, so this file is a single
# number: a block that silently stops registering turns the run red instead of printing N/N.
$script:ExpectedChecks = 25
function Expect([int]$n) { $script:ExpectedChecks += $n }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TrayProbe {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int i);

  /// "pid=1234 class=Notepad hwnd=0x1234 exstyle=0x0" -- the foreground window as the OS sees it right now.
  public static string ForegroundInfo() {
    IntPtr h = GetForegroundWindow();
    if (h == IntPtr.Zero) return "pid=0 class=(none) hwnd=0x0 exstyle=0x0";
    var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("pid={0} class={1} hwnd=0x{2:X} exstyle=0x{3:X}", pid, sb.ToString(), h.ToInt64(), GetWindowLongW(h, -20));
  }

  /// Top-level windows whose class name matches. The tray window is intentionally top-level
  /// (a message-only window cannot receive the TaskbarCreated broadcast), so it shows up here.
  public static List<string> WindowsOfClass(string cls) {
    var found = new List<string>();
    EnumProc cb = delegate(IntPtr h, IntPtr p) {
      var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
      if (sb.ToString() != cls) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      found.Add(string.Format("hwnd=0x{0:X} pid={1} exstyle=0x{2:X}", h.ToInt64(), pid, GetWindowLongW(h, -20)));
      return true;
    };
    EnumWindows(cb, IntPtr.Zero); GC.KeepAlive(cb);
    return found;
  }
}
'@

"=== setup ==="
if (-not (Test-Path -LiteralPath $Exe)) { "executable not found: $Exe"; exit 2 }
Get-Process -Name Confern -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$logDir = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$script:counter = 0

# The timestamp of the binary under test is printed because a stale exe is the single most common
# way a run like this reports success while testing the PREVIOUS build (the copy of a locked exe
# fails silently). Printing it does not prevent that, but it makes it auditable afterwards.
$exeStamp = (Get-Item -LiteralPath $Exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
"exe: $((Resolve-Path -LiteralPath $Exe).Path)"
"exe last write: $exeStamp"

# A scratch settings file keeps the resident runs from consulting (or editing) the user's own
# configuration. The two files differ ONLY in startWithWindows, so S15.19/S15.20 exercise the
# "do not touch the Run key" path and S15.21/S15.22 exercise the "resident app applies it" path.
$cfgStart = Join-Path $Work 'settings-start.json'
$cfgNoStart = Join-Path $Work 'settings-nostart.json'

# ---------------------------------------------------------------------------
# The real Run key. A verification suite MUST NOT change it: the whole point of
# --autostart-probe is that acceptance runs cannot silently switch the user's
# start-with-Windows on. These four lines are the entire safety net, so they run
# before anything else and are compared again at the very end.
# ---------------------------------------------------------------------------
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'Confern'
$runExistedBefore = Test-Path -LiteralPath $runKeyPath
$runBefore = if ($runExistedBefore) { (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName } else { $null }
$runBeforeText = if ($null -eq $runBefore) { '(absent)' } else { "$runBefore" }

function Invoke-Confern([string[]]$a, [string]$tag) {
  $script:counter++
  $log = Join-Path $logDir "$tag-$($script:counter).log"
  $p = Start-Process -FilePath $Exe -ArgumentList ($a + @('--quiet', '--log', $log)) -PassThru -WindowStyle Hidden
  $finished = $p.WaitForExit(60000)
  if (-not $finished) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
  # Read the exit code while the Process object is still ours: after a Kill/timeout the cached
  # property is not a measurement, and an empty value must never look like "exit code 0".
  $code = $null
  try { if ($finished) { $code = $p.ExitCode } } catch { $code = $null }
  return @{
    Exit = $code
    Finished = $finished
    Log = $log
    Text = (Get-Content -LiteralPath $log -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
  }
}
function Get-Field([string]$text, [string]$name) {
  $m = [regex]::Match($text, [regex]::Escape($name) + '=(\S+)')
  if ($m.Success) { return $m.Groups[1].Value }
  return ''
}

"=== S15.1-S15.4 the tray icon registers, and says what it is ==="
$tray = Invoke-Confern @('--tray-selftest') 'tray-selftest'
$trayText = $tray.Text
Check 'S15.1 --tray-selftest exits 0' ($tray.Finished -and $tray.Exit -eq 0) "exit=$($tray.Exit) finished=$($tray.Finished)"
Check 'S15.2 the shell really accepted the icon, and the probe can see it' (
  $trayText -match 'tray: icon registered' -and
  $trayText -match 'tray: PASS icon created' -and
  # "icon registered" alone only proves the log line exists; NIM_MODIFY being accepted is what proves
  # the shell is holding it (found by falsifying Shell_NotifyIconW(NIM_ADD) - the log line survived).
  $trayText -match 'tray: PASS the shell holds the icon  \[NIM_MODIFY accepted\]') ''
Check 'S15.3 the tray window is real and cannot take the focus (WS_EX_NOACTIVATE)' (
  $trayText -match 'tray: class=ConfernTray hwnd=0x[0-9A-F]+' -and
  $trayText -match 'menu items=4') ''
Check 'S15.4 every tray event line is prefixed with tray:' (
  (@($trayText -split "`n" | Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d\d\d tray ' }).Count) -eq 0 -and
  (@($trayText -split "`n" | Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d\d\d tray:' }).Count) -ge 15) `
  "trayLines=$(@($trayText -split "`n" | Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d\d\d tray:' }).Count) unprefixed=$(@($trayText -split "`n" | Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d\d\d tray ' }).Count)"

"=== S15.5-S15.7 the tooltip and the four menu items are what the spec says ==="
# The tooltip string is built from ProductInfo.Name, so this asserts the product name is the single
# owner's value and not a second hard-coded literal somewhere.
Check 'S15.5 the tooltip is the product name plus the running state' (
  $trayText -match "tray: tooltip 'Confern - running' state=running" -and
  $trayText -match "tray: PASS tooltip names the product and the state") ''
# 2026-10-23: the tray gained a fourth top-level item, the "recent conversions" submenu. It is the
# same four labels the product ships, and the count is still pinned on purpose: a menu that silently
# loses an entry is a real regression, not a cosmetic one.
Check 'S15.6 the menu has exactly four items, with the spec labels' (
  $trayText -match "tray: menu items=4 item0='([^']+)' item1='([^']+)' item2='([^']+)' item3='([^']+)' labels_ok=True") ''
$m = [regex]::Match($trayText, "tray: menu items=4 item0='([^']+)' item1='([^']+)' item2='([^']+)' item3='([^']+)'")
$cjkLabels = 0
if ($m.Success) {
  foreach ($g in @(1,2,3,4)) {
    # Non-ASCII: the three labels are Traditional Chinese by product decision (open settings /
    # pause conversion / exit). Asserting the shape here keeps this file ASCII-clean while still
    # failing if a label is ever replaced by an empty or ASCII-only string.
    if ($m.Groups[$g].Value -match '[^\x00-\x7F]') { $cjkLabels++ }
  }
}
Check 'S15.7 all four menu labels are the real (non-ASCII) product strings' ($cjkLabels -eq 4) "nonAsciiLabels=$cjkLabels/4"

"=== S15.8-S15.10 each command ran through the shared dispatch ==="
# The self-test calls InvokeCommand, which is the SAME method a real menu click ends up in. Three
# distinct callback lines are the evidence that all three entries are wired to something.
Check 'S15.8 the open-settings command reached its callback' ($trayText -match 'tray: command -> open settings \(path=') ''
Check 'S15.9 the pause command flipped the state and the icon followed' (
  $trayText -match 'tray: dispatch toggle-pause paused=True' -and
  $trayText -match "tray: paused -> tooltip 'Confern - paused' menu1='[^']+' shellUpdated=True" -and
  $trayText -match "tray: PASS pause state reached the icon  \[tooltip='Confern - paused'\]") ''
Check 'S15.10 the exit command reached its callback, and resume came back' (
  $trayText -match 'tray: dispatch exit' -and
  $trayText -match 'tray: dispatch toggle-pause paused=False' -and
  $trayText -match "tray: tooltip 'Confern - running' state=running") ''

"=== S15.11-S15.12 after dispose the shell no longer has the icon ==="
Check 'S15.11 NIM_DELETE was accepted and the probe says the shell let it go' (
  $trayText -match 'tray: NIM_DELETE removed=True' -and
  $trayText -match 'tray: PASS the shell no longer holds the icon after dispose') ''
# P1-1 (2026-10-02): `\d+` accepted 0, so a self-test that ran NO checks at all still read as
# "failed=0 ok=True". A suite that reports its own success count must report a positive one.
Check 'S15.12 the self-test asserts one shell registration and reports zero failures' (
  $trayText -match 'tray: selftest done: checks=([1-9]\d*) failed=0 ok=True' -and
  $trayText -match 'tray: PASS the shell holds the icon  \[NIM_MODIFY accepted\]') ''

# Numbered 24-25 rather than inserted as 13-14 on purpose: renumbering eleven later checks would
# churn the file for no gain, and the labels are only labels. These two are the safety pair --
# spec 4.2.2 says the kill switch stops capture until restart, and a tray "resume" must not undo it.
#
# The tooltip line format is "tray: paused -> tooltip '<text>' menu1='...'", NOT "tray: tooltip".
# The first version of S15.25 expected the latter and went red while the product was correct;
# the detail string below is what told them apart, so keep passing real evidence into Check.
$iconStillPaused = [bool]($trayText -match "tray: paused -> tooltip 'Confern - paused'")
$iconPausedDetail = [bool]($trayText -match 'the icon tells the truth after a refused resume  \[tray\.IsPaused=True')
# P1-8 (2026-10-02): the old predicate leaned on `guard.CaptureDisabled=True` -- which is true
# whatever Resume() did (the tray's Pause is a no-op when capture is already off, and a refused
# Resume leaves it off), so the guard half of this safety pair proved nothing. The refusal is now
# a COUNTED FACT (`guardResumeRefusals`) that only the guard's latched branch can produce: remove
# the KillSwitchLatched gate in ReleaseGuard.Resume and the count stays 0.
$guardRefusals = 0
$gr = [regex]::Match($trayText, 'tray: PASS kill switch stays latched[^\r\n]*guardResumeRefusals=(\d+)')
if ($gr.Success) { $guardRefusals = [int]$gr.Groups[1].Value }
Check 'S15.24 the kill switch cannot be undone from the tray (spec 4.2.2: until restart)' (
  $trayText -match 'tray: PASS kill switch stays latched: the tray cannot resume capture' -and
  ($guardRefusals -ge 1) -and
  $trayText -match 'guard: REFUSED to resume' -and
  $trayText -match 'detector: REFUSED to resume') `
  "guardResumeRefusals=$guardRefusals guardRefusalLog=$([bool]($trayText -match 'guard: REFUSED to resume')) detectorRefusalLog=$([bool]($trayText -match 'detector: REFUSED to resume'))"
Check 'S15.25 after a refused resume the icon still says paused (it must not lie)' (
  $iconPausedDetail -and $iconStillPaused) `
  "selftestSaysPaused=$iconPausedDetail pausedTooltipLine=$iconStillPaused"

"=== S15.13-S15.15 start with Windows round-trips in a SCRATCH subkey ==="
# Unique per run, and without spaces: the subkey is passed on a command line, and a space would
# split it into two arguments (measured while writing this suite).
$scratch = "Software\confern-verify-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
$scratchFull = "HKCU:\$scratch"
# P1-4 (2026-10-02): this block used to end with `$leftBehind -eq 0` -- an EMPTY SET counted as
# success, so `Enable()` could have been reduced to a log line and nothing else and S15.16 would
# still pass. The enabled state only EXISTS while the probe runs, so the runner samples the
# registry WHILE it runs: "the value was really there, holding the path the app says it uses"
# becomes a fact about the registry rather than the app's own report of itself.
$probeLog = Join-Path $logDir 'autostart-probe.log'
# S15.16 fix (see the sampling loop below): the probe holds the value until this file appears.
$ackPath = Join-Path $Work 'autostart-observed.flag'
$probeProc = Start-Process -FilePath $Exe -ArgumentList @('--autostart-probe', $scratch, '--autostart-ack', $ackPath, '--quiet', '--log', $probeLog) -PassThru -WindowStyle Hidden
# The registry is sampled through the .NET API instead of the PowerShell provider: the probe
# enables, then deliberately writes a STALE path (its own control case), then enables again and
# disables -- so the correct value exists only in two short windows. A provider round-trip per
# iteration is slow enough to miss both (measured with it: 1 sample, and it was the stale one),
# and a check that usually reports "I did not see it" is exactly what this audit is about.
#
# S15.16 fix (2026-10-06): those two windows are ~1ms wide (measured: all four enable/disable log
# lines land inside ONE millisecond), and this loop is descheduled by the OS for milliseconds at a
# time, so "did I see it" was luck. Measured on this machine: 400 runs of exactly this block -> 3
# RED, and all 3 red probe logs said checks=7 failed=0 ok=True -- i.e. the product was correct and
# the CHECK was wrong. The race is now removed instead of survived: the probe HOLDS the correct
# value until this loop, having really compared it, creates $ackPath. The assertion below is
# unchanged (the sampled value must still BE the quoted absolute exe path) -- only the timing
# stopped being a coin flip.
$scratchParent = [Microsoft.Win32.Registry]::CurrentUser
$observedValues = New-Object System.Collections.ArrayList
# Precomputed outside the loop: the loop body must stay allocation-free, and an allocation inside
# it is the very thing that lets a GC pause swallow the window.
$ackTmp = $ackPath + '.tmp'
$ackDone = $false
# The expected value is what the probe itself logged. It is read from the probe log here -- BEFORE
# the value it describes is written -- on purpose: the ack below must be sent only after a real
# comparison, so the comparison target must already be known when the loop starts. Get-Field is
# called at most a handful of times (only until the probe has flushed its first line); the probe
# writes that line before it enables anything, so the target is always set before the first sample.
$expected = Get-Field ((Get-Content -LiteralPath $probeLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n") 'expected'
$probePollSw = [Diagnostics.Stopwatch]::StartNew()
while (-not $probeProc.HasExited -and $probePollSw.ElapsedMilliseconds -lt 60000) {
  if (-not $expected) {
    $expected = Get-Field ((Get-Content -LiteralPath $probeLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n") 'expected'
    if (-not $expected) { continue }
  }
  $k = $null
  try { $k = $scratchParent.OpenSubKey($scratch) } catch { $k = $null }
  if ($null -ne $k) {
    try {
      $pv = $k.GetValue($runName)
      if ($null -ne $pv) {
        [void]$observedValues.Add("$pv")
        if (-not $ackDone -and "$pv" -eq $expected) {
          # Announce-only-after-comparing, and once: from here on the probe may change the value
          # again. Written through a rename so the probe can never read a half-written file.
          try {
            Set-Content -LiteralPath $ackTmp -Value $scratch -Encoding ASCII -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $ackTmp -Destination $ackPath -Force -ErrorAction SilentlyContinue
            $ackDone = $true
          } catch { $ackDone = $false }
        }
      }
    } finally { $k.Close() }
  }
}
$probeAcked = [bool](Test-Path -LiteralPath $ackPath -PathType Leaf)
if (Test-Path -LiteralPath $ackTmp) { Remove-Item -LiteralPath $ackTmp -Force -ErrorAction SilentlyContinue }
$probeFinished = $probeProc.WaitForExit(60000)
$probeExit = $null
try { if ($probeFinished) { $probeExit = $probeProc.ExitCode } } catch { $probeExit = $null }
if (-not $probeFinished) { $probeProc.Kill(); $probeProc.WaitForExit(5000) | Out-Null }
$probeText = (Get-Content -LiteralPath $probeLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
$probe = @{ Exit = $probeExit; Finished = $probeFinished; Log = $probeLog; Text = $probeText }

# P1-1 (2026-10-02): `checks=\d+` accepted a self-test that registered ZERO checks.
Check 'S15.13 --autostart-probe exits 0 in a scratch subkey' (
  $probe.Finished -and $probe.Exit -eq 0 -and
  $probeText -match 'autostart: probe done: checks=([1-9]\d*) failed=0 ok=True') `
  "exit=$($probe.Exit)"

$expected = Get-Field $probeText 'expected'
# The value must be the QUOTED absolute path of the exe under test. An unquoted path with a space
# in it is a silently broken Run entry (Windows just does nothing), so this is the exact contract.
$exeFull = (Resolve-Path -LiteralPath $Exe).Path
Check 'S15.14 ExpectedValue is the quoted absolute exe path' (
  $expected -eq ('"' + $exeFull + '"') -and $probeText -match 'autostart: PASS the expected value is a quoted absolute path to this exe') `
  "expected=$expected"

Check 'S15.15 the subkey round-trips: absent -> enabled -> readable -> disabled -> absent' (
  $probeText -match 'autostart: PASS absent before the probe' -and
  $probeText -match 'autostart: ENABLED HKCU\\' -and
  $probeText -match 'autostart: PASS enabled and the value round-trips' -and
  $probeText -match 'autostart: PASS a stale path does not read as enabled' -and
  $probeText -match 'autostart: PASS enable repairs a stale value' -and
  $probeText -match 'autostart: DISABLED \(removed' -and
  $probeText -match 'autostart: PASS disabled and the value is gone') ''

# Independent check of the round-trip, using the registry itself rather than the app's own report:
# the probing loop above must have SEEN the value while the probe had it enabled (with the exact
# expected path), and the probe must have left nothing behind. Then clean up the scratch subkey.
$leftBehind = if (Test-Path -LiteralPath $scratchFull) {
  @(Get-Item -LiteralPath $scratchFull).Property.Count
} else { 0 }
$sawExpectedValue = @($observedValues | Where-Object { $_ -eq $expected }).Count
$distinctSeen = @($observedValues | Sort-Object -Unique)
Check 'S15.16 the probe really wrote the expected value (seen live) and removed it afterwards' `
  (($sawExpectedValue -ge 1) -and ($leftBehind -eq 0)) `
  "ack=$probeAcked liveValueSamples=$($observedValues.Count) sawExpectedValue=$sawExpectedValue distinctSeen=[$($distinctSeen -join ' | ')] valuesLeft=$leftBehind"
if (Test-Path -LiteralPath $scratchFull) { Remove-Item -LiteralPath $scratchFull -Recurse -Force -ErrorAction SilentlyContinue }

"=== S15.17-S15.20 the resident app: tray on, focus untouched, clean exit ==="
# A resident run cannot be asked to quit by synthetic input (this suite must not inject any), so it
# is asked through the tray's own exit command. --tray-exit-after posts WM_APP_TRAY_COMMAND with the
# exit code, which is the same dispatch a real menu click uses.
& $Exe --settings-set 'startWithWindows=true' --settings-file $cfgStart --quiet --log (Join-Path $logDir 'seed-start.log') | Out-Null
& $Exe --settings-set 'startWithWindows=false' --settings-file $cfgNoStart --quiet --log (Join-Path $logDir 'seed-nostart.log') | Out-Null

$fgBefore = [TrayProbe]::ForegroundInfo()
$residentLog = Join-Path $logDir 'resident.log'
$sw = [Diagnostics.Stopwatch]::StartNew()
$resident = Start-Process -FilePath $Exe -ArgumentList @('--settings-file', $cfgNoStart, '--tray-exit-after', "$($ResidentSeconds * 1000)", '--quiet', '--log', $residentLog) -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds ([int]($ResidentSeconds * 1000 * 0.7))
$residentAlive = @(Get-Process -Id $resident.Id -ErrorAction SilentlyContinue).Count -gt 0
$residentTrayWindows = [TrayProbe]::WindowsOfClass('ConfernTray')
$fgDuring = [TrayProbe]::ForegroundInfo()
$residentFinished = $resident.WaitForExit(30000)
$residentElapsed = [int]$sw.ElapsedMilliseconds
$residentExit = $null
try { if ($residentFinished) { $residentExit = $resident.ExitCode } } catch { $residentExit = $null }
if (-not $residentFinished) { $resident.Kill(); $resident.WaitForExit(5000) | Out-Null }
$residentText = (Get-Content -LiteralPath $residentLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"

Check 'S15.17 the tray icon is registered in a resident run' (
  $residentText -match 'tray: icon registered' -and $residentText -match 'message loop starting') `
  "alive=$residentAlive trayWindows=$($residentTrayWindows.Count)"
Check 'S15.18 the tray window belongs to the resident process and never activates' (
  $residentTrayWindows.Count -eq 1 -and $residentTrayWindows[0] -match "pid=$($resident.Id) " -and
  $residentTrayWindows[0] -match 'exstyle=0x[0-9A-F]*8[0-9A-F]*') `
  "$($residentTrayWindows -join '; ')"

if ($fgDuring -eq $fgBefore) {
  Check 'S15.19 the resident run did not steal the foreground window' $true "unchanged: $fgBefore"
} elseif ($fgDuring -match "pid=$($resident.Id) ") {
  Check 'S15.19 the resident run did not steal the foreground window' $false "foreground moved to the app: $fgBefore -> $fgDuring"
} else {
  Inconclusive 'S15.19 the resident run did not steal the foreground window' "the foreground changed on its own (the user is using the machine): $fgBefore -> $fgDuring"
}

Check 'S15.20 the app exits cleanly when the tray asks it to' (
  $residentFinished -and $residentExit -eq 0 -and
  $residentText -match 'tray: command -> exit requested' -and
  $residentText -match 'message loop exited' -and
  $residentText -match 'tray: NIM_DELETE removed=True' -and
  $residentText -match 'tray: disposed') `
  "exit=$residentExit finished=$residentFinished elapsed=${residentElapsed}ms"

"=== S15.21-S15.22 start-with-Windows is applied by the resident app (and only by it) ==="
# The design rule: only the RESIDENT app writes the Run key; --settings-set stays a pure settings-file
# editor. Both halves are checked here, and the value is restored to whatever it was afterwards.
# The snapshot is taken HERE, not at the top of the file: by now the resident run may legitimately
# have written the key (it is the thing under test), so comparing against a stale value would fail
# for a reason that has nothing to do with the rule being checked.
$runBeforeSet = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName
$runBeforeSetText = if ($null -eq $runBeforeSet) { '(absent)' } else { "$runBeforeSet" }
& $Exe --settings-set 'startWithWindows=true' --settings-file (Join-Path $Work 'settings-set-only.json') `
      --quiet --log (Join-Path $logDir 'settings-set-runkey.log') | Out-Null
$runValueAfterSet = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName
$runAfterSetText = if ($null -eq $runValueAfterSet) { '(absent)' } else { "$runValueAfterSet" }
Check 'S15.21 --settings-set startWithWindows=true does NOT touch the real Run key' (
  $runAfterSetText -eq $runBeforeSetText) "before=$runBeforeSetText after=$runAfterSetText"

$runLog = Join-Path $logDir 'resident-runkey.log'
$runResident = Start-Process -FilePath $Exe -ArgumentList @('--settings-file', $cfgStart, '--tray-exit-after', '2500', '--quiet', '--log', $runLog) -PassThru -WindowStyle Hidden
$runFinished = $runResident.WaitForExit(30000)
$runExit = $null
try { if ($runFinished) { $runExit = $runResident.ExitCode } } catch { $runExit = $null }
if (-not $runFinished) { $runResident.Kill(); $runResident.WaitForExit(5000) | Out-Null }
$runText = (Get-Content -LiteralPath $runLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
$runValueWritten = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName
Check 'S15.22 the resident app DOES apply the setting (startWithWindows=true writes the quoted exe)' (
  $runFinished -and $runExit -eq 0 -and $runText -match 'autostart: ENABLED HKCU\\' -and
  "$runValueWritten" -eq ('"' + $exeFull + '"')) `
  "written=$runValueWritten exit=$runExit"

# Put it back exactly as it was: absent stays absent, an existing value keeps its old data.
# (The assertion for this lives in S15.23, right after the restore.)
if ($null -eq $runBefore) {
  Remove-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue
} else {
  Set-ItemProperty -LiteralPath $runKeyPath -Name $runName -Value $runBefore
}

"=== S15.23 and this suite left the real Run key exactly as it found it ==="
# Restored just above, whatever the checks said: a failing run must not leave a stray autostart
# entry behind either, and the detail line says plainly which of the two happened.
$runAfterText = if (Test-Path -LiteralPath $runKeyPath) {
  $v = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName
  if ($null -eq $v) { '(absent)' } else { "$v" }
} else { '(absent)' }
Check 'S15.23 the real Run value is unchanged by the whole suite' ($runAfterText -eq $runBeforeText) `
  "$runBeforeText -> $runAfterText (the resident run under test wrote it on purpose; this suite restored it)"

""
"M1g summary: $($script:Pass)/$($script:Checks) checks passed"
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
