# verify-m1b.ps1 -- M1b acceptance runner (wheel geometry and display)
#
# Sections:
#   S6  two-layer stack: the visual layer must be click-through (automatic, NON-INVASIVE)
#   S7  wheel show/hide visibility + wheel rectangle                          (automatic)
#   S8  a REAL drag must produce DragEnter -> wheel in <=100 ms      (NEEDS A HUMAN, not input)
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 mis-decodes BOM-less UTF-8 script
# literals, so any non-ASCII text here corrupts string parsing.
#
# The window probe below intentionally duplicates the instrument in verify-m1a.ps1 so
# that each runner stays independently runnable. Extracting a shared helper is tracked
# as a T6 cleanup item.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'S6',
  # 4a (2026-10-16) -- ANSWERED, and this parameter is NOT the answer: it is declared and never
  # used anywhere in this file. It is the vestigial hook where "drive S8 by machine" was going to
  # go. What does exist for that is tools\dragsim: a separate process that starts a REAL OLE drag
  # (DoDragDrop called INSIDE WM_LBUTTONDOWN, then SendInput MOVE/LEFTDOWN/LEFTUP). It works - its
  # 2026-10-01 run is what proved that a registered IDropTarget means WM_DROPFILES never arrives
  # (see src\Sidecar\Interaction\DropTarget.cs near line 124) - and it is NOT wired into this
  # suite, on purpose:
  #   * it drives the USER's pointer, and a private desktop does NOT isolate that: SendInput acts
  #     on the INPUT desktop (tools\probe-private-desktop.ps1 Q2 exists to measure exactly this);
  #   * its own usage note says the start point must be a blank part of the desktop, because the
  #     synthesized press can hit a desktop icon - and this suite cannot see the desktop to pick one.
  # So S8 stays human-guided. If it is ever automated it must be opt-in, with the start point given
  # by the caller, and the pointer parked afterwards.
  [string]$DragSim = '',
  [int]$DemoX = 900,
  [int]$DemoY = 400,
  # The visual-layer window is anchored at (cursor - PadPx), NOT at the cursor:
  # the wheel body sits at cursor+(18,18) and the bitmap carries WheelGeometry.MarginPx
  # (42 px) of clearance around the wheel for the glass glow, plus a bottom strip for the
  # keycap hint, the caption and the output-name preview. WheelGeometry.PadPx owns that value.
  # 2026-10-01 (direction D): width and PadPx are unchanged; the height grew from 330 to 349
  # because the keycap + caption strip was added below the wheel.
  [int]$PadPx = 24,
  [int]$DemoW = 304,
  [int]$DemoH = 349,
  [string]$LogDir = "$env:TEMP\sidecar-verify",
  # S8 is human-guided: it prints instructions, waits this long, then inspects the log.
  [int]$S8WindowSeconds = 90
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class P2 {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  // P0-4 (2026-10-02): the runner drives the product's own hide path from the outside. This is
  // the same message the guards post (Native.WM_APP_FORCE_HIDE = WM_APP + 1), so the check that
  // uses it fails when the product's ShowWindow call stops doing anything.
  [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

  /// pid==0 means "any process". One string per matching top-level window.
  public static List<string> WindowsOfClass(string cls, uint pid) {
    var found = new List<string>();
    EnumProc cb = delegate(IntPtr h, IntPtr p) {
      uint wpid; GetWindowThreadProcessId(h, out wpid);
      if (pid != 0 && wpid != pid) return true;
      var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
      if (sb.ToString() != cls) return true;
      RECT r; GetWindowRect(h, out r);
      found.Add(string.Format("hwnd=0x{0:X} {1},{2},{3}x{4} visible={5} exstyle=0x{6:X} pid={7}",
        h.ToInt64(), r.L, r.T, r.R - r.L, r.B - r.T, IsWindowVisible(h), GetWindowLongW(h, -20), wpid));
      return true;
    };
    EnumWindows(cb, IntPtr.Zero); GC.KeepAlive(cb);
    return found;
  }

  public static string HitAt(int x, int y) {
    IntPtr h = WindowFromPoint(new POINT { X = x, Y = y });
    if (h == IntPtr.Zero) return "(none)";
    var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("0x{0:X} class={1} pid={2}", h.ToInt64(), sb.ToString(), pid);
  }
  public static string ForegroundInfo() {
    IntPtr h = GetForegroundWindow();
    if (h == IntPtr.Zero) return "(none)";
    var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("0x{0:X} class={1} pid={2}", h.ToInt64(), sb.ToString(), pid);
  }
}
'@

$results = New-Object System.Collections.ArrayList
$inconclusive = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok) {
  [void]$results.Add([pscustomobject]@{ Name = $name; Pass = $ok })
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" }
}
# P1-2 (2026-10-02): the floor of how many checks a run must register. 0 means "not yet
# calibrated" and skips the assertion; the number is set from a measured run of -Section All.
# A FLOOR, because S6/S7 register a few checks inside branches that are allowed to reach a
# different total, while the failure this exists for -- a block that silently registers NOTHING
# (S6.3-S6.7 did exactly that when their regex stopped matching) -- is always BELOW the floor.
# Equality would turn any legitimate branch difference into a false red.
$script:ExpectedChecks = 0
function Expect([int]$n) { $script:ExpectedChecks += $n }
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason); Write-Host "INCONCLUSIVE  $reason"
}
function Get-LogLines([string]$p) { if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() } }
function Test-Log([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -Quiet) }
# Select-String is CASE-INSENSITIVE by default, so "FAIL" also matches "failed=0" in the
# app's own summary line. Any check about the presence of a FAIL line must use this one.
function Test-LogCase([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -CaseSensitive -Quiet) }
# Count matching lines, case-sensitively: the log strings below are written by the app
# verbatim, and "-CaseSensitive" keeps a near-miss from counting as a hit.
function Count-Log([string[]]$l, [string]$p) { return @($l | Select-String -Pattern $p -CaseSensitive).Count }

# Milliseconds between the first line matching $from and the next line matching $to.
# Both timestamps come from Evidence.Line's "HH:mm:ss.fff" prefix. Returns -1 when either
# line is missing, so "the human did nothing" can never read as "0 ms -> PASS".
function Measure-LogGapMs([string[]]$l, [string]$from, [string]$to) {
  $start = -1
  for ($i = 0; $i -lt $l.Count; $i++) { if ($l[$i] -cmatch $from) { $start = $i; break } }
  if ($start -lt 0) { return -1 }
  for ($j = $start; $j -lt $l.Count; $j++) {
    if ($l[$j] -cmatch $to) {
      $a = [regex]::Match($l[$start], '^(\d{2}:\d{2}:\d{2}\.\d{3})')
      $b = [regex]::Match($l[$j], '^(\d{2}:\d{2}:\d{2}\.\d{3})')
      if (-not $a.Success -or -not $b.Success) { return -1 }
      $ta = [datetime]::ParseExact($a.Groups[1].Value, 'HH:mm:ss.fff', $null)
      $tb = [datetime]::ParseExact($b.Groups[1].Value, 'HH:mm:ss.fff', $null)
      return [int]($tb - $ta).TotalMilliseconds
    }
  }
  return -1
}

function Stop-AllSidecar {
  Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

# Run one injected-payload diagnostic and hand back its exit code and log lines.
# NOTE: the notice text is Chinese, so it cannot be asserted from this ASCII-only file.
# The checks therefore assert the *status* plus "some notice is present" (no "notice=-").
function Get-PayloadRun([string]$payload, [string]$tag) {
  Stop-AllSidecar
  $log = Join-Path $LogDir "m1b-s7-$tag.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  # --no-autostart: --wheel-payload goes all the way through the resident path (it really converts,
  # then serves the message loop), which is where the startWithWindows setting is applied. Its spec
  # default is ON, so without this flag every regression run would write the user's Run key with a
  # path into bin\Release.
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-payload', $payload, '--no-autostart', '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru -Wait
  return [pscustomobject]@{ Exit = $proc.ExitCode; Lines = (Get-LogLines $log) }
}

# M1c: the sector list is what the app OFFERS (spec 6.2 declared output matrix intersected
# with what this machine actually encodes), not a constant and not raw capability either.
# S7 must therefore not hardcode it -- it cross-checks the wheel against the --codecs run.
# Returns the offered list as a CSV string ("PNG,JPG,BMP,TIF") or $null when the probe
# itself failed (in which case the caller reports INCONCLUSIVE instead of passing vacuously).
function Get-OfferedOutputsCsv {
  $log = Join-Path $LogDir 'm1c-codecs.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @('--codecs', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  $m = [regex]::Match(($lines -join "`n"), 'offered=\[([^\]]*)\]')
  if (-not $m.Success) { return $null }
  return $m.Groups[1].Value
}

# The sector list a run actually showed, as a CSV string, or $null when no wheel was shown.
function Get-ShownTargetsCsv([string[]]$lines) {
  foreach ($l in $lines) {
    $m = [regex]::Match($l, 'targets=\[([^\]]*)\]')
    if ($m.Success) { return $m.Groups[1].Value }
  }
  return $null
}

function Remove-FromCsv([string]$csv, [string]$item) {
  if ($null -eq $csv) { return $null }
  return ((@($csv -split ',' | Where-Object { $_ -ne '' -and $_ -ne $item })) -join ',')
}

# ------------------------------------------------------------------ S6
function Invoke-S6 {
  Write-Host ""
  Write-Host "=== S6 two-layer stack: visual layer must be click-through (non-invasive) ==="
  Stop-AllSidecar

  $log = Join-Path $LogDir 'm1b-s6.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-demo', "$DemoX", "$DemoY", '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru
  Start-Sleep -Milliseconds 1800
  $proc.Refresh()
  if ($proc.HasExited) { Expect -13; Check "S6.0 app alive" $false; return }
  $pid32 = [uint32]$proc.Id

  $hit = @([P2]::WindowsOfClass('ConfernHitLayer', $pid32))
  $vis = @([P2]::WindowsOfClass('ConfernVisualLayer', $pid32))
  Write-Host "      hit  : $($hit -join ' | ')"
  Write-Host "      vis  : $($vis -join ' | ')"

  Check "S6.1 hit layer window exists" ($hit.Count -ge 1)
  Check "S6.2 visual layer window exists" ($vis.Count -eq 1)

  if ($vis.Count -eq 1 -and $vis[0] -match 'hwnd=0x([0-9A-F]+) (-?\d+),(-?\d+),(\d+)x(\d+) visible=(True|False) exstyle=0x([0-9A-F]+)') {
    $vhwnd = $Matches[1]; $vx = [int]$Matches[2]; $vy = [int]$Matches[3]
    $vw = [int]$Matches[4]; $vh = [int]$Matches[5]; $vvis = ($Matches[6] -eq 'True')
    $ex = [Convert]::ToInt64($Matches[7], 16)
    Write-Host ("      visual exstyle bits: LAYERED={0} TRANSPARENT={1} NOACTIVATE={2} TOPMOST={3} TOOLWINDOW={4}" -f `
      (($ex -band 0x80000) -ne 0), (($ex -band 0x20) -ne 0), (($ex -band 0x8000000) -ne 0), `
      (($ex -band 0x8) -ne 0), (($ex -band 0x80) -ne 0))
    Check "S6.3 visual layer has WS_EX_TRANSPARENT (drops pass through)" ((($ex -band 0x20) -ne 0))
    Check "S6.4 visual layer has WS_EX_NOACTIVATE (cannot steal focus)" ((($ex -band 0x8000000) -ne 0))
    Check "S6.5 visual layer is layered + topmost + toolwindow" `
      ((($ex -band 0x80000) -ne 0) -and (($ex -band 0x8) -ne 0) -and (($ex -band 0x80) -ne 0))
    Check "S6.6 visual layer is visible for the test" $vvis
    $wantX = $DemoX - $PadPx
    $wantY = $DemoY - $PadPx
    Check "S6.7 visual layer rect is (cursor-PadPx, cursor-PadPx, $DemoW x $DemoH)" `
      (($vx -eq $wantX) -and ($vy -eq $wantY) -and ($vw -eq $DemoW) -and ($vh -eq $DemoH))
  }

  # The decisive check: with BOTH layers visible, every sampled point must resolve to the
  # HIT layer, never the visual layer. WindowFromPoint skips WS_EX_TRANSPARENT windows --
  # which is exactly the property the drop relies on.
  $hitHwnd = if ($hit.Count -ge 1 -and $hit[0] -match 'hwnd=0x([0-9A-F]+)') { $Matches[1] } else { '' }
  # PowerShell gotcha: the comma operator binds TIGHTER than arithmetic, so
  # @($x + 1, $y + 2) parses as $x + (1, $y) + 2 -> "op_Addition on Object[]".
  # Always parenthesise each element of a computed point list.
  $inside = @()
  foreach ($ox in 40, 130, 220) { foreach ($oy in 40, 130, 220) { $inside += , @(($DemoX + $ox), ($DemoY + $oy)) } }
  $insideMiss = @()
  foreach ($pt in $inside) {
    $got = [P2]::HitAt($pt[0], $pt[1])
    if ($got -notmatch "^0x$hitHwnd ") { $insideMiss += "$($pt[0]),$($pt[1]) -> $got" }
  }
  Write-Host "      sampled $($inside.Count) points inside the wheel rect; misses=$($insideMiss.Count)"
  if ($insideMiss.Count -gt 0) { $insideMiss | Select-Object -First 4 | ForEach-Object { Write-Host "        $_" } }
  Check "S6.8 all $($inside.Count) points inside the wheel resolve to the hit layer" ($insideMiss.Count -eq 0)

  $outside = @(
    @(($DemoX - 60), ($DemoY + 130)),
    @(($DemoX + $DemoW + 60), ($DemoY + 130)),
    @(($DemoX + 130), ($DemoY - 60)),
    @(($DemoX + 130), ($DemoY + $DemoH + 60))
  )
  $outsideMiss = @()
  foreach ($pt in $outside) {
    $got = [P2]::HitAt($pt[0], $pt[1])
    if ($got -notmatch "^0x$hitHwnd ") { $outsideMiss += "$($pt[0]),$($pt[1]) -> $got" }
  }
  Check "S6.9 all 4 points outside the wheel resolve to the hit layer" ($outsideMiss.Count -eq 0)

  $fg = [P2]::ForegroundInfo()
  Write-Host "      foreground: $fg"
  Check "S6.10 neither layer took the foreground" (($fg -notmatch 'ConfernHitLayer') -and ($fg -notmatch 'ConfernVisualLayer'))

  $lines = Get-LogLines $log
  $ms = $null
  foreach ($l in $lines) { if ($l -match 'visual demo shown .* in ([\d.]+) ms') { $ms = [double]$Matches[1] } }
  if ($ms -eq $null) { Check "S6.11 first-show latency was logged" $false }
  else {
    Write-Host ("      first-show latency = {0} ms (budget 100 ms)" -f $ms)
    Check "S6.11 first show under the 100 ms budget" ($ms -lt 100)
  }
  Check "S6.12 visual layer logged TRANSPARENT=set(required)" (Test-Log $lines 'WS_EX_TRANSPARENT=set\(required\)')

  # P0-4 (2026-10-02): everything above asks the OS about the window while it is up. This one
  # drives the product's own hide path from outside the process: it posts WM_APP_FORCE_HIDE
  # (the message every guard posts, Native.cs: 0x8001) to the visual layer's HWND and then asks
  # the OS whether the window really went away. Until now "the layer hides" was only ever
  # proven by the app's own log line, which reads identically when ShowWindow has been turned
  # into a no-op -- that is exactly the regression this project's 2026-10-02 audit was about.
  $forceHideOk = $false
  $forceHideDetail = 'no visual layer hwnd was observed'
  if ($vhwnd) {
    [void][P2]::PostMessageW([IntPtr][Convert]::ToInt64($vhwnd, 16), 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $afterForceHide = @([P2]::WindowsOfClass('ConfernVisualLayer', $pid32))
    $stillVisible = @($afterForceHide | Where-Object { $_ -match 'visible=True' }).Count
    $forceHideOk = ($afterForceHide.Count -ge 1) -and ($stillVisible -eq 0)
    $forceHideDetail = "windows=$($afterForceHide.Count) stillVisible=$stillVisible"
  }

  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Start-Sleep -Milliseconds 400
  $left = @([P2]::WindowsOfClass('ConfernVisualLayer', 0))
  Check "S6.13 no visual layer window survives the process" ($left.Count -eq 0)
  Write-Host "      [os] after posting WM_APP_FORCE_HIDE: $forceHideDetail"
  Check "S6.14 posting WM_APP_FORCE_HIDE really hides the visual layer (OS-verified)" $forceHideOk
}

# ------------------------------------------------------------------ S7
# Wheel show/hide, window rectangle, flip-edge and the post-DragEnter path.
# All NON-INVASIVE: no synthetic input is injected, so this is safe to run while the
# user is working. What it does NOT prove: that the real OLE drag pipeline delivers
# the payload (that is S8) - S7 feeds the payload in by hand and says so out loud.
function Invoke-S7 {
  Write-Host ""
  Write-Host "=== S7 wheel rectangle, flip-edge, wiring and payload path (non-invasive) ==="

  # ---- geometry self-test ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-geom.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-geometry-selftest', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.1 geometry self-test exits 0" ($proc.ExitCode -eq 0)
  Check "S7.2 geometry self-test has no FAIL line" (-not (Test-LogCase $lines 'FAIL'))

  # ---- wheel frame dump (one-shot: shows, dumps and exits) ----
  $dump = Join-Path $LogDir 'm1b-s7-frame.png'
  if (Test-Path -LiteralPath $dump) { Remove-Item -LiteralPath $dump -Force }
  $null = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-demo', "$DemoX", "$DemoY", '--wheel-dump', $dump, '--quiet',
    '--log', (Join-Path $LogDir 'm1b-s7-dump.log')) -PassThru -Wait
  Check "S7.7 wheel frame dumped" ((Test-Path -LiteralPath $dump) -and ((Get-Item -LiteralPath $dump).Length -gt 8000))

  # ---- wheel rectangle at the demo cursor (this run must STAY alive: it is what we probe) ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-rect.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-demo', "$DemoX", "$DemoY",
    '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru
  Start-Sleep -Milliseconds 1800
  $proc.Refresh()
  if ($proc.HasExited) { Check "S7.3 app alive during the rectangle run" $false; return }
  $pid32 = [uint32]$proc.Id
  $vis = @([P2]::WindowsOfClass('ConfernVisualLayer', $pid32))
  Check "S7.3 exactly one visual layer window" ($vis.Count -eq 1)

  $pat = 'hwnd=0x([0-9A-F]+) (-?\d+),(-?\d+),(\d+)x(\d+) visible=(True|False) exstyle=0x([0-9A-F]+)'
  $rectOk = $false
  if ($vis.Count -eq 1 -and $vis[0] -match $pat) {
    $rectOk = $true
    $vx = [int]$Matches[2]; $vy = [int]$Matches[3]; $vw = [int]$Matches[4]; $vh = [int]$Matches[5]
    Check "S7.4 visual layer is visible" ($Matches[6] -eq 'True')
    Check "S7.5 wheel window anchored at cursor - PadPx" (($vx -eq ($DemoX - $PadPx)) -and ($vy -eq ($DemoY - $PadPx)))
    Check "S7.6 wheel bitmap size" (($vw -eq $DemoW) -and ($vh -eq $DemoH))
  } else { Check "S7.4 visual layer rectangle readable" $false }

  Check "S7.8 demo run logged the wheel show" (Test-Log (Get-LogLines $log) 'wheel shown cursor=')

  # P0-4 (2026-10-02): the same OS-side drive as S6.14, on the rectangle run. S7.4/S7.5/S7.6 and
  # S7.8 all read either the window style or the app's log; none of them can see the product's
  # ShowWindow calls stop working. Posting WM_APP_FORCE_HIDE and re-asking the OS can.
  $rectForceHideOk = $false
  $rectForceHideDetail = 'no visual layer hwnd was observed'
  if ($vis.Count -eq 1 -and $vis[0] -match 'hwnd=0x([0-9A-F]+)') {
    $rectHwnd = $Matches[1]
    [void][P2]::PostMessageW([IntPtr][Convert]::ToInt64($rectHwnd, 16), 0x8001, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $afterRectHide = @([P2]::WindowsOfClass('ConfernVisualLayer', $pid32))
    $rectStillVisible = @($afterRectHide | Where-Object { $_ -match 'visible=True' }).Count
    $rectForceHideOk = ($afterRectHide.Count -ge 1) -and ($rectStillVisible -eq 0)
    $rectForceHideDetail = "windows=$($afterRectHide.Count) stillVisible=$rectStillVisible"
  }

  # ---- flip-edge: the wheel must move to the other side near the work-area edge (spec 4.3) ----
  Stop-AllSidecar
  Add-Type -AssemblyName System.Windows.Forms
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $edgeX = $wa.Right - 4
  $edgeY = $wa.Top + 300
  $log = Join-Path $LogDir 'm1b-s7-flip.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-demo', "$edgeX", "$edgeY", '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru
  Start-Sleep -Milliseconds 1800
  $proc.Refresh()
  if ($proc.HasExited) { Check "S7.9 app alive during the flip run" $false; return }
  $vis = @([P2]::WindowsOfClass('ConfernVisualLayer', [uint32]$proc.Id))
  if ($vis.Count -eq 1 -and $vis[0] -match $pat) {
    $fx = [int]$Matches[2]; $fw = [int]$Matches[4]
    # Without a flip the origin would be cursor - PadPx; a flip puts it far to the left.
    Check "S7.9 near the right edge the wheel flips" ($fx -lt ($edgeX - $PadPx - 100))
    Check "S7.10 flipped wheel stays inside the work area" (($fx -ge $wa.Left) -and (($fx + $fw) -le $wa.Right))
  } else { Check "S7.9 flip run: visual layer rectangle readable" $false }

  # ---- wiring: capture -> show -> ring select -> Esc -> both layers hide ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-cycles.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-simulate-cycles', '3', '--no-autostart', '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.11 simulate cycles exit 0" ($proc.ExitCode -eq 0)
  Check "S7.12 simulate cycles: no FAIL line" (-not (Test-LogCase $lines 'FAIL'))
  Check "S7.13 simulate cycles: 3 cycles, 0 failed" (Test-Log $lines 'simulate cycle done: 3 cycles; failed=0')

  # ---- payload path: INJECTED, not a real drag (see the note at the top of S7) ----
  $measured = Get-OfferedOutputsCsv
  if ($null -eq $measured) { Inconclusive "M1c --codecs probe produced no offered-output set" }
  else { Write-Host "  (offered outputs on this machine: [$measured])" }

  $r = Get-PayloadRun 'C:\Temp\holiday.jpg' 'single'
  Check "S7.14 single-image payload run exits 0" ($r.Exit -eq 0)
  Check "S7.15 payload source format is JPG" (Test-Log $r.Lines 'source=JPG')
  # spec 4.3: the source format must NOT appear among the output sectors
  Check "S7.16 source format excluded from the sectors" (-not (Test-Log $r.Lines 'targets=\[[^\]]*\bJPG\b'))
  # M1c: sectors must equal the OFFERED set minus the source (cross-instrument check)
  $shown = Get-ShownTargetsCsv $r.Lines
  if ($null -eq $measured) { Inconclusive "sectors vs offered set (probe unavailable)" }
  elseif ($null -eq $shown) { Check "S7.16b sectors equal the offered set minus the source" $false }
  else {
    Check "S7.16b sectors equal offered [$measured] minus source ($shown)" ($shown -eq (Remove-FromCsv $measured 'JPG'))
  }
  Check "S7.17 preview shows the output file name" (Test-Log $r.Lines 'preview=holiday\.png')
  # The ring advances in order, so select#1 must land on the SECOND sector -- whatever it is.
  # (Hardcoding "WEBP" here recorded one day's implementation instead of the contract.)
  $shownList = @()
  if ($null -ne $shown) { $shownList = @($shown -split ',' | Where-Object { $_ -ne '' }) }
  if ($shownList.Count -lt 2) { Inconclusive "second sector unknown (no sector list)" }
  else {
    Check "S7.18 selection advances to the second sector ($($shownList[1]))" `
      (Test-Log $r.Lines ("payload select#1: index=1 target=" + [regex]::Escape($shownList[1])))
  }
  Check "S7.19 verdict is Ready" (Test-Log $r.Lines 'status=Ready')
  Check "S7.20 Enter commits and the wheel goes away" (Test-Log $r.Lines 'committed=True wheelVisibleAfter=False')
  $m = [regex]::Match(($r.Lines -join "`n"), 'latency=([0-9.]+) ms')
  if ($m.Success) {
    Check "S7.21 payload -> show latency < 100 ms (was $($m.Groups[1].Value) ms)" ([double]$m.Groups[1].Value -lt 100)
  } else { Inconclusive "payload latency line not found" }

  # ---- same-category batch stays convertible (spec 4.4) ----
  $r = Get-PayloadRun 'C:\Temp\a.jpg;C:\Temp\b.png' 'samecat'
  Check "S7.22 same-category batch exits 0" ($r.Exit -eq 0)
  Check "S7.23 same-category batch is Ready, not Mixed" (Test-Log $r.Lines 'status=Ready')
  Check "S7.24 multi-file preview shows a count, not one name" (Test-Log $r.Lines 'preview=2 \S')

  # ---- mixed selection must be REFUSED (spec 4.3) ----
  $r = Get-PayloadRun 'C:\Temp\a.jpg;C:\Temp\doc.pdf' 'mixed'
  Check "S7.25 different categories -> Mixed" (Test-Log $r.Lines 'status=Mixed')
  Check "S7.26 Mixed shows a notice" (-not (Test-Log $r.Lines 'notice=-'))
  Check "S7.27 Mixed leaves no selectable target" (Test-Log $r.Lines 'targets=0')
  Check "S7.28 Mixed: Enter refused and the wheel stays" (Test-Log $r.Lines 'committed=False wheelVisibleAfter=True')

  # ---- a single unsupported file is a DIFFERENT message than mixed (spec 4.3) ----
  # This sample EXPIRED on 2026-10-07: that slice deliberately made "PDF has an applicable tool"
  # mean Ready (the tool layer opens and the merge-pdf sector appears), so a PDF is no longer
  # Unsupported. The assertion's intent (a single unsupported file is NOT a mixed selection) is
  # still right - only its sample was wrong. 2026-10-15: use .txt instead (the text family is
  # second-milestone in 3.2 and this product does not support it), and .txt will not silently
  # become Ready the way PDF did.
  $r = Get-PayloadRun 'C:\Temp\doc.txt' 'unsupported'
  Check "S7.29 single non-image -> Unsupported" (Test-Log $r.Lines 'status=Unsupported')
  Check "S7.30 Unsupported: Enter refused and the wheel stays" (Test-Log $r.Lines 'committed=False wheelVisibleAfter=True')

  # ---- mouse hover: the cursor -> sector mapping (where DragOver lands) ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-hover.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-hover-selftest', '--no-autostart', '--quiet', '--watchdog-seconds', '300', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.31 hover self-test exits 0" ($proc.ExitCode -eq 0)
  Check "S7.32 hover self-test has no FAIL line" (-not (Test-LogCase $lines 'FAIL'))
  $hits = @($lines | Select-String -Pattern 'hover sector' -CaseSensitive).Count
  # Derive the expectation from the wheel's OWN sector list. The sector count is
  # (offered outputs for the source format) and it changes with the catalog: a hardcoded
  # ">= 5" silently became wrong once offered was pinned to declared n measured.
  # Equality (not >=) is the real invariant: EVERY offered sector must be reachable.
  $targetLine = ($lines | Select-String -Pattern 'targets=\[' | Select-Object -First 1)
  $expected = 0
  if ($targetLine) {
    $expected = @((([regex]::Match($targetLine.Line, 'targets=\[([^\]]*)\]')).Groups[1].Value -split ',')).Count
  }
  Check "S7.33 every offered sector is individually reachable by hover (saw $hits of $expected)" (($expected -gt 0) -and ($hits -eq $expected))
  Check "S7.34 centre disc keeps the selection" (Test-Log $lines 'centre disc does not move the selection')
  Check "S7.35 outside the wheel keeps the selection" (Test-Log $lines 'outside the wheel does not move the selection')

  Stop-AllSidecar
  $left = @([P2]::WindowsOfClass('ConfernVisualLayer', 0))
  Check "S7.36 no visual layer window survives the runs" ($left.Count -eq 0)
  Write-Host "      [os] rect run after posting WM_APP_FORCE_HIDE: $rectForceHideDetail"
  Check "S7.37 posting WM_APP_FORCE_HIDE really hides the visual layer during the rect run (OS-verified)" $rectForceHideOk
}

# ------------------------------------------------------------------ S8 (human-guided)
# S8 needs a REAL drag, and a synthetic one CANNOT stand in: M05 already proved that a
# scripted DoDragDrop never produces DragEnter (a control WinForms window received nothing
# either -- residual R4). So this section prints instructions, waits, then inspects the log.
# It NEVER moves the pointer itself: nothing in here injects input.
function Invoke-S8([int]$Window) {
  Write-Host ""
  Write-Host "=== S8 HUMAN: a real trigger-held drag -> DragEnter -> wheel in <=100 ms ==="
  # 4a (2026-10-16): the payload half of this section is NOT unverifiable - --wheel-payload injects
  # the same payload the OLE drag would carry, and the wheel's geometry/selection/hide logic are
  # covered by S6/S7 here and by verify-m1a/m1c. What genuinely needs a human is the POINTER half:
  # a real drag loop calling IDropTarget::DragEnter/DragOver/Drop. Coordinates can only arrive that
  # way (WM_MOUSEMOVE is swallowed by the shell's drag loop), and no synthetic message can fake it:
  # a registered IDropTarget means WM_DROPFILES never arrives at all. Without the gesture these 13
  # checks are ENVIRONMENT RED BY DESIGN - that is the intended loud failure, not a broken suite.
  Write-Host "      (no human within $Window s => these checks fail on purpose; see the DragSim note above)"
  Stop-AllSidecar

  $root = (Get-Item -LiteralPath $Exe).Directory
  while ($root -ne $null -and -not (Test-Path -LiteralPath (Join-Path $root.FullName 'docs\aegis'))) {
    $root = $root.Parent
  }
  $src = if ($root) { Join-Path $root.FullName 'docs\aegis\work\2026-09-30-m1b-wheel\wheel-frame.png' } else { '' }
  if (-not $src -or -not (Test-Path -LiteralPath $src)) {
    Expect -12
    Inconclusive "S8 needs the sample PNG under <repo>\docs\aegis\work\2026-09-30-m1b-wheel\ (not found)"
    return
  }
  # A deterministic sample: exact file name, exact expected source format, and a hash we
  # can compare afterwards (dragging must be a read-only operation).
  $sample = Join-Path $env:TEMP 's8-sample.png'
  Copy-Item -LiteralPath $src -Destination $sample -Force
  $before = (Get-FileHash -LiteralPath $sample -Algorithm SHA256).Hash

  $log = Join-Path $LogDir 'm1b-s8.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  # --no-autostart: this is a resident run, and a resident run applies the startWithWindows setting
  # (spec 8, default ON). Without the flag a regression run would write the user's Run key.
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--no-autostart', '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru
  Start-Sleep -Milliseconds 1500
  $proc.Refresh()
  if ($proc.HasExited) { Check "S8.0 app is alive and idle before the gesture" $false; return }

  Write-Host "      the runner does NOT touch the pointer. Please do this:"
  Write-Host "        1. paste this folder into Explorer's address bar and press Enter:"
  Write-Host "             $env:TEMP"
  Write-Host "        2. select the file   s8-sample.png"
  Write-Host "        3. HOLD the trigger key (space) BEFORE starting the drag"
  Write-Host "        4. drag it out of Explorer, keep the button held"
  Write-Host "        5. move the cursor over a wheel sector (selection should follow)"
  Write-Host "        6. release the mouse button over that sector"
  Write-Host "        7. release the trigger key"
  Write-Host "      waiting $Window seconds ..."
  Start-Sleep -Seconds $Window

  $lines = Get-LogLines $log
  $enters = Count-Log $lines 'OLE DragEnter:'
  Check "S8.1 a real DragEnter was observed (count=$enters)" ($enters -ge 1)
  # The injected diagnostic writes its own marker, so it can never satisfy the checks below.
  Check "S8.2 no injected-payload marker in this log" ((Count-Log $lines '\*\*\* INJECTED') -eq 0)
  Check "S8.3 a DragEnter carried exactly one file" ((Count-Log $lines 'OLE DragEnter:.*files=1') -ge 1)
  Check "S8.4 the payload path is the sample that was dragged" ((Count-Log $lines ([regex]::Escape($sample))) -ge 1)
  Check "S8.5 the source format is real PNG, not a placeholder" ((Count-Log $lines 'wheel from OLE payload:.*source=PNG') -ge 1)
  # NOT vacuous on purpose: "PNG is not in the sector list" is trivially true when no wheel
  # ever appeared, so this also requires that a wheel was actually shown from a payload.
  Check "S8.6 source format excluded from the sectors" `
        (((Count-Log $lines 'wheel from OLE payload:') -ge 1) -and
         (-not (Test-Log $lines 'targets=\[[^\]]*\bPNG\b')))
  Check "S8.7 the preview is derived from the real file name" ((Count-Log $lines 'preview=s8-sample\.') -ge 1)

  $gap = Measure-LogGapMs $lines 'OLE DragEnter:' 'wheel shown'
  if ($gap -ge 0) {
    Check "S8.8 DragEnter -> wheel shown in <=100 ms (measured $gap ms)" ($gap -le 100)
  } else {
    Check "S8.8 DragEnter -> wheel shown latency measurable" $false
  }

  Check "S8.9 hovering a sector moved the selection" ((Count-Log $lines 'wheel hover selection') -ge 1)
  Check "S8.10 the drop hid both layers" (((Count-Log $lines 'hit layer HIDDEN') -ge 1) -and
                                          ((Count-Log $lines 'visual layer HIDDEN') -ge 1))

  $after = (Get-FileHash -LiteralPath $sample -Algorithm SHA256).Hash
  Check "S8.11 the dragged file is byte-identical afterwards" ($before -eq $after)

  # P0-4 (2026-10-02): S8.10 proves the app LOGGED that both layers were hidden after the drop.
  # This is the same property asked of the OS, with a positive premise (a wheel really was shown
  # from a payload) so it cannot pass by the gesture never having happened. Injected input is
  # deliberately absent here: this section is the one that exercises the real OLE path.
  $payloadWheelShown = (Count-Log $lines 'wheel from OLE payload:') -ge 1
  $afterDrop = @([P2]::WindowsOfClass('ConfernVisualLayer', [uint32]$proc.Id))
  $afterDropVisible = @($afterDrop | Where-Object { $_ -match 'visible=True' }).Count
  $dropHiddenOs = $payloadWheelShown -and ($afterDrop.Count -ge 1) -and ($afterDropVisible -eq 0)

  $proc.Refresh()
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Start-Sleep -Milliseconds 500
  $left = @([P2]::WindowsOfClass('ConfernVisualLayer', 0))
  Check "S8.12 no visual layer window survives" ($left.Count -eq 0)
  Write-Host "      [os] after the real drop: wheelShownFromPayload=$payloadWheelShown windows=$($afterDrop.Count) visible=$afterDropVisible"
  Check "S8.13 the OS agrees the visual layer is hidden after the real drop (not just the log line)" $dropHiddenOs

  # Observation only, NOT asserted: which path delivered the file list? M1d needs one
  # authority, and the answer is decided from these counts rather than guessed here.
  Write-Host "      OBSERVED: DragEnter x$enters, IDropTarget DROP x$(Count-Log $lines 'OLE DROP RECEIVED'), WM_DROPFILES x$(Count-Log $lines '\*\*\* DROP RECEIVED')"
  Write-Host "      (file-list authority for M1d is decided from these counts; not asserted here)"
}

# ------------------------------------------------------------------ drive
# -Section accepts a comma-separated list. Unknown names and "nothing ran" are hard errors.
$knownSections = @('S6', 'S7', 'S8')
$sections = switch ($Section) {
  # "All" deliberately excludes S8: it needs a human to perform a real drag, so it must be
  # asked for explicitly (-Section S8). Its checks FAIL loudly when nobody does the gesture.
  'All' { @('S6', 'S7') }
  default { @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) }
}
$unknown = @($sections | Where-Object { $knownSections -notcontains $_ })
if ($unknown.Count -gt 0) {
  Write-Host "unknown section(s): $($unknown -join ', ')"
  Write-Host "known: All, $($knownSections -join ', ')"
  exit 2
}
if ($sections.Count -eq 0) { Write-Host "no sections requested"; exit 2 }

Write-Host "verify-m1b: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

foreach ($s in $sections) {
  # P1-2: each section's check count. FLOORS, because a few checks sit inside branches that may
  # legitimately reach a different total (S6.3-S6.7 only exist while the window string parses,
  # which IS how that block silently vanished before). The failure this catches -- a block that
  # registers NOTHING -- is always below the floor. The two "app died" early returns in S7 do
  # not mirror a correction on purpose: a section that lost the process under test cannot report
  # a result, so tripping the floor there is the intended outcome.
  switch ($s) {
    'S6' { Expect 14; Invoke-S6 }
    'S7' { Expect 38; Invoke-S7 }
    'S8' { Expect 13; Invoke-S8 $S8WindowSeconds }
    default { Write-Host "section not implemented yet: $s" }
  }
}

Stop-AllSidecar

$fail = @($results | Where-Object { -not $_.Pass }).Count
$registered = $results.Count + $inconclusive.Count
if ($results.Count -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
Write-Host "checks=$registered pass=$($results.Count - $fail) fail=$fail inconclusive=$($inconclusive.Count) expectedFloor=$($script:ExpectedChecks)"
# P1-2: a suite that registers fewer checks than it promises has a block that silently did not
# register. That is a red run, not an N/N print.
if ($script:ExpectedChecks -gt 0 -and $registered -lt $script:ExpectedChecks) {
  Write-Host "FATAL: registered $registered checks but this suite must register at least $($script:ExpectedChecks)."
  Write-Host "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
Write-Host ""
Write-Host "M1b summary: $($results.Count - $fail)/$($results.Count) checks passed ($Section)"
if ($inconclusive.Count -gt 0) {
  Write-Host "INCONCLUSIVE ($($inconclusive.Count)):"
  $inconclusive | ForEach-Object { Write-Host "  $_" }
}
if ($fail -gt 0) {
  Write-Host "FAILURES:"
  $results | Where-Object { -not $_.Pass } | ForEach-Object { Write-Host "  $($_.Name)" }
  exit 1
}
if ($inconclusive.Count -gt 0) { exit 3 }
exit 0
