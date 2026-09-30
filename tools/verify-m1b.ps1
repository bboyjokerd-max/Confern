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
  [int]$DemoSize = 260,
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
    Check "S6.7 visual layer rect equals the requested position/size" `
      (($vx -eq $DemoX) -and ($vy -eq $DemoY) -and ($vw -eq $DemoSize) -and ($vh -eq $DemoSize))
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
    @(($DemoX + $DemoSize + 60), ($DemoY + 130)),
    @(($DemoX + 130), ($DemoY - 60)),
    @(($DemoX + 130), ($DemoY + $DemoSize + 60))
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

# ------------------------------------------------------------------ drive
$sections = switch ($Section) {
  'All' { @('S6') }
  default { @($Section) }
}

Write-Host "verify-m1b: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

foreach ($s in $sections) {
  switch ($s) {
    'S6' { Invoke-S6 }
    default { Write-Host "section not implemented yet: $s" }
  }
}

Stop-AllSidecar

$fail = @($results | Where-Object { -not $_.Pass }).Count
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
