# verify-m1b.ps1 -- M1b acceptance runner (wheel geometry and display)
#
# Sections:
#   S6  two-layer stack: the visual layer must be click-through (automatic, NON-INVASIVE)
#   S7  wheel show/hide visibility + wheel rectangle                          (automatic)
#   S8  synthetic OLE drag: wheel appears while dragging                      (NEEDS THE MOUSE)
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
  [string]$DragSim = '',
  [int]$DemoX = 900,
  [int]$DemoY = 400,
  # The visual-layer window is anchored at (cursor - PadPx), NOT at the cursor:
  # the wheel box sits at cursor+(18,18) inside a bitmap that also has to contain the
  # 32 px ghost card centred on the cursor. WheelGeometry.PadPx owns that value.
  [int]$PadPx = 24,
  [int]$DemoW = 304,
  [int]$DemoH = 330,
  [string]$LogDir = "$env:TEMP\sidecar-verify"
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
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason); Write-Host "INCONCLUSIVE  $reason"
}
function Get-LogLines([string]$p) { if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() } }
function Test-Log([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -Quiet) }
# Select-String is CASE-INSENSITIVE by default, so "FAIL" also matches "failed=0" in the
# app's own summary line. Any check about the presence of a FAIL line must use this one.
function Test-LogCase([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -CaseSensitive -Quiet) }

function Stop-AllSidecar {
  Get-Process Sidecar -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
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
  if ($proc.HasExited) { Check "S6.0 app alive" $false; return }
  $pid32 = [uint32]$proc.Id

  $hit = @([P2]::WindowsOfClass('SidecarHitLayer', $pid32))
  $vis = @([P2]::WindowsOfClass('SidecarVisualLayer', $pid32))
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
  Check "S6.10 neither layer took the foreground" (($fg -notmatch 'SidecarHitLayer') -and ($fg -notmatch 'SidecarVisualLayer'))

  $lines = Get-LogLines $log
  $ms = $null
  foreach ($l in $lines) { if ($l -match 'visual demo shown .* in ([\d.]+) ms') { $ms = [double]$Matches[1] } }
  if ($ms -eq $null) { Check "S6.11 first-show latency was logged" $false }
  else {
    Write-Host ("      first-show latency = {0} ms (budget 100 ms)" -f $ms)
    Check "S6.11 first show under the 100 ms budget" ($ms -lt 100)
  }
  Check "S6.12 visual layer logged TRANSPARENT=set(required)" (Test-Log $lines 'WS_EX_TRANSPARENT=set\(required\)')

  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Start-Sleep -Milliseconds 400
  $left = @([P2]::WindowsOfClass('SidecarVisualLayer', 0))
  Check "S6.13 no visual layer window survives the process" ($left.Count -eq 0)
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
  $vis = @([P2]::WindowsOfClass('SidecarVisualLayer', $pid32))
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
  $vis = @([P2]::WindowsOfClass('SidecarVisualLayer', [uint32]$proc.Id))
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
    '--wheel-simulate-cycles', '3', '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.11 simulate cycles exit 0" ($proc.ExitCode -eq 0)
  Check "S7.12 simulate cycles: no FAIL line" (-not (Test-LogCase $lines 'FAIL'))
  Check "S7.13 simulate cycles: 3 cycles, 0 failed" (Test-Log $lines 'simulate cycle done: 3 cycles; failed=0')

  # ---- payload path: INJECTED, not a real drag (see the note at the top of S7) ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-payload.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-payload', 'C:\Temp\holiday.jpg', '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.14 payload run exits 0" ($proc.ExitCode -eq 0)
  Check "S7.15 payload source format is JPG" (Test-Log $lines 'source=JPG')
  # spec 4.3: the source format must NOT appear among the output sectors
  Check "S7.16 source format excluded from the sectors" (-not (Test-Log $lines 'targets=\[[^\]]*\bJPG\b'))
  Check "S7.17 preview shows the output file name" (Test-Log $lines 'preview=holiday\.png')
  Check "S7.18 preview follows the selection" (Test-Log $lines 'payload select#1: index=1 target=WEBP')
  $m = [regex]::Match(($lines -join "`n"), 'latency=([0-9.]+) ms')
  if ($m.Success) {
    Check "S7.19 payload -> show latency < 100 ms (was $($m.Groups[1].Value) ms)" ([double]$m.Groups[1].Value -lt 100)
  } else { Inconclusive "payload latency line not found" }

  # ---- multi-file payload: the preview must become a count ----
  Stop-AllSidecar
  $log = Join-Path $LogDir 'm1b-s7-multi.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-payload', 'C:\Temp\a.jpg;C:\Temp\b.jpg',
    '--watchdog-seconds', '300', '--quiet', '--log', $log) -PassThru -Wait
  $lines = Get-LogLines $log
  Check "S7.20 multi-file payload exits 0" ($proc.ExitCode -eq 0)
  Check "S7.21 multi-file preview shows a count, not one name" (Test-Log $lines 'preview=2 \S')

  Stop-AllSidecar
  $left = @([P2]::WindowsOfClass('SidecarVisualLayer', 0))
  Check "S7.22 no visual layer window survives the runs" ($left.Count -eq 0)
}

# ------------------------------------------------------------------ drive
# -Section accepts a comma-separated list. Unknown names and "nothing ran" are hard errors.
$knownSections = @('S6', 'S7', 'S8')
$sections = switch ($Section) {
  # "All" deliberately excludes S8: that one injects real mouse input and must be asked for.
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
  switch ($s) {
    'S6' { Invoke-S6 }
    'S7' { Invoke-S7 }
    default { Write-Host "section not implemented yet: $s" }
  }
}

Stop-AllSidecar

$fail = @($results | Where-Object { -not $_.Pass }).Count
if ($results.Count -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
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
