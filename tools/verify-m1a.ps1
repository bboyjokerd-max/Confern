# verify-m1a.ps1 -- M1a acceptance runner
#
# Sections:
#   S1  monitors / hit-layer window geometry / no WS_EX_TRANSPARENT / idle invisible   (automatic)
#   S2  programmatic 50x raise/hide cycle, must end fully hidden                       (automatic)
#   S3  synthetic lifecycle + insurances #1/#2/#3/#5                                    (automatic)
#   S4  synthetic OLE drag onto the raised hit layer: drop list received, source file
#       untouched, immediate hide                                                       (automatic)
#   S4m real Explorer drags, human gestures (still the only way to cover explorer-as-source
#       arming and a real drop in ONE gesture)                                          (human window)
#   S5  kill the process while raised, restart must be clean (A13)                       (automatic)
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 mis-decodes BOM-less UTF-8
# script literals, so any non-ASCII text here corrupts string parsing.
#
# Synthetic input cannot drive a real OLE drag (M0.5 proved it), but DragDetector
# only reads mouse events -- it does not know whether a real drag is in progress.
# Therefore armed/capturing/raise/release/grace/watchdog/kill-switch are all
# verifiable with SendInput. Only the actual WM_DROPFILES delivery needs a human.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'All',
  [string]$EvidenceDir = '',
  [string]$DragSim = '',
  [int]$S4WindowSeconds = 75,
  [int]$WatchdogSeconds = 3,
  [string]$LogDir = "$env:TEMP\sidecar-verify"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Probe {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }

  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr SendInput(uint n, INPUT[] p, int cb);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

  const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
  const uint MOVE = 0x0001, ABSOLUTE = 0x8000, KEYUP = 0x0002;
  public const uint LEDOWN = 0x0002, LEUP = 0x0004;

  /// pid==0 means "any process". Returns one string per matching window.
  public static List<string> HitWindows(uint pid) {
    var found = new List<string>();
    EnumProc cb = delegate(IntPtr h, IntPtr p) {
      uint wpid; GetWindowThreadProcessId(h, out wpid);
      if (pid != 0 && wpid != pid) return true;
      var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
      if (sb.ToString() != "SidecarHitLayer") return true;
      RECT r; GetWindowRect(h, out r);
      found.Add(string.Format("{0},{1},{2}x{3} visible={4} exstyle=0x{5:X} pid={6}",
        r.L, r.T, r.R - r.L, r.B - r.T, IsWindowVisible(h), GetWindowLongW(h, -20), wpid));
      return true;
    };
    EnumWindows(cb, IntPtr.Zero); GC.KeepAlive(cb);
    return found;
  }

  public static void MoveTo(int x, int y) {
    int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var a = new INPUT[1]; a[0].type = INPUT_MOUSE;
    a[0].U.mi.dx = (int)Math.Round((double)x * 65535.0 / Math.Max(1, vw - 1));
    a[0].U.mi.dy = (int)Math.Round((double)y * 65535.0 / Math.Max(1, vh - 1));
    a[0].U.mi.dwFlags = MOVE | ABSOLUTE;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void MoveRel(int dx, int dy) {
    var a = new INPUT[1]; a[0].type = INPUT_MOUSE;
    a[0].U.mi.dx = dx; a[0].U.mi.dy = dy; a[0].U.mi.dwFlags = MOVE;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Mouse(uint flags) {
    var a = new INPUT[1]; a[0].type = INPUT_MOUSE; a[0].U.mi.dwFlags = flags;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Key(ushort vk, bool up) {
    var a = new INPUT[1]; a[0].type = INPUT_KEYBOARD;
    a[0].U.ki.wVk = vk; a[0].U.ki.dwFlags = up ? KEYUP : 0;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
}
'@

$VK_SPACE = 0x20; $VK_CTRL = 0x11; $VK_ALT = 0x12; $VK_SHIFT = 0x10; $VK_Q = 0x51
$results = New-Object System.Collections.ArrayList
$inconclusive = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok) {
  [void]$results.Add([pscustomobject]@{ Name = $name; Pass = $ok })
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" }
}
# A section that cannot set up its own precondition must never be reported as PASS or FAIL.
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason)
  Write-Host "INCONCLUSIVE  $reason"
}
function Get-LogLines([string]$p) { if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() } }
function Test-Log([string[]]$lines, [string]$pattern) { return [bool]($lines | Select-String -Pattern $pattern -Quiet) }
function Count-Log([string[]]$lines, [string]$pattern) { return @($lines | Select-String -Pattern $pattern).Count }

function Stop-AllSidecar {
  Get-Process Sidecar -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

function Start-Sidecar([string[]]$Argv, [string]$Name) {
  $log = Join-Path $LogDir "$Name.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList ($Argv + @('--quiet', '--log', $log)) -PassThru
  Start-Sleep -Milliseconds 1500
  return [pscustomobject]@{ Process = $proc; Log = $log }
}

function Invoke-Capture([string[]]$Argv) {
  # WinExe: '& $exe' does not wait; Start-Process -Wait -PassThru is mandatory.
  $out = Join-Path $LogDir 'capture.out.txt'
  $err = Join-Path $LogDir 'capture.err.txt'
  $proc = Start-Process -FilePath $Exe -ArgumentList $Argv -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $text = if (Test-Path -LiteralPath $out) { (Get-Content -LiteralPath $out -Raw -Encoding UTF8) } else { '' }
  return [pscustomobject]@{ Exit = $proc.ExitCode; Stdout = $text }
}

# ---------------------------------------------------------------- S1
function Invoke-S1 {
  Write-Host ""
  Write-Host "=== S1 monitors / window geometry / styles / idle invisibility ==="
  Stop-AllSidecar

  $dump = Invoke-Capture @('--dump-monitors')
  Check "S1.1 --dump-monitors exits 0" ($dump.Exit -eq 0)
  $dumpLines = @($dump.Stdout -split "\r?\n" | Where-Object { $_ -match '^monitor#' })
  Check "S1.2 at least one monitor reported ($($dumpLines.Count))" ($dumpLines.Count -ge 1)

  $monitors = @()
  foreach ($line in $dumpLines) {
    if ($line -match '^monitor#(\d+) (\S+) bounds=\((-?\d+),(-?\d+),(\d+)x(\d+)\).*primary=(True|False)') {
      $monitors += [pscustomobject]@{
        Index = [int]$Matches[1]; Device = $Matches[2]
        L = [int]$Matches[3]; T = [int]$Matches[4]; W = [int]$Matches[5]; H = [int]$Matches[6]
        Primary = ($Matches[7] -eq 'True')
      }
    }
  }
  Check "S1.3 all monitor lines parsed ($($monitors.Count)/$($dumpLines.Count))" ($monitors.Count -eq $dumpLines.Count)
  Check "S1.4 exactly one primary monitor" ((@($monitors | Where-Object { $_.Primary }).Count) -eq 1)

  $run = Start-Sidecar @() 's1'
  Check "S1.5 app stays alive while idle" (-not $run.Process.HasExited)

  $wins = @([Probe]::HitWindows([uint32]$run.Process.Id))
  Check "S1.6 one hit window per monitor ($($wins.Count) windows / $($monitors.Count) monitors)" ($wins.Count -eq $monitors.Count)

  $allHidden = $true; $allClean = $true; $geomOk = $true
  for ($i = 0; $i -lt $wins.Count; $i++) {
    $w = $wins[$i]
    $m = $monitors[$i]
    if ($w -notmatch '^(-?\d+),(-?\d+),(\d+)x(\d+) visible=(True|False) exstyle=0x([0-9A-F]+) pid=\d+$') { $allClean = $false; continue }
    if ($Matches[5] -ne 'False') { $allHidden = $false }
    $ex = [Convert]::ToInt64($Matches[6], 16)
    if (($ex -band 0x20) -ne 0) { $allClean = $false }
    if (([int]$Matches[1] -ne $m.L) -or ([int]$Matches[2] -ne $m.T) -or ([int]$Matches[3] -ne $m.W) -or ([int]$Matches[4] -ne $m.H)) { $geomOk = $false }
    Write-Host "      hit window $i : $w   (expected $($m.L),$($m.T),$($m.W)x$($m.H))"
  }
  Check "S1.7 every hit window is invisible while idle (zero-interference precondition)" $allHidden
  Check "S1.8 no window has WS_EX_TRANSPARENT (mechanism alive)" $allClean
  Check "S1.9 window geometry equals monitor geometry" $geomOk

  $lines = Get-LogLines $run.Log
  Check "S1.10 app self-report agrees: no WS_EX_TRANSPARENT" ((Test-Log $lines 'WS_EX_TRANSPARENT=not set\(OK\)') -and -not (Test-Log $lines 'SET\(FATAL\)'))
  Check "S1.11 app self-report agrees: allHidden=True" (Test-Log $lines 'hit layer ready: \d+ window\(s\), allHidden=True')
  Stop-Process -Id $run.Process.Id -Force
  Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------- S2
function Invoke-S2 {
  Write-Host ""
  Write-Host "=== S2 50x programmatic raise/hide cycle ==="
  Stop-AllSidecar
  $run = Start-Sidecar @('--cycle-test', '50') 's2'
  $run.Process.WaitForExit()
  Check "S2.1 --cycle-test 50 exits 0 (everything hidden at the end)" ($run.Process.ExitCode -eq 0)
  $lines = Get-LogLines $run.Log
  Check "S2.2 exactly 50 raises logged" ((Count-Log $lines 'hit layer SHOWN') -eq 50)
  Check "S2.3 ends allHidden=True" (Test-Log $lines 'cycle test done: 50 cycles; allHidden=True')
  Check "S2.4 no leftover window handles" (Test-Log $lines 'hit layer disposed; leftoverWindows=0')
  $left = @([Probe]::HitWindows([uint32]$run.Process.Id))
  Check "S2.5 no hit window survives the process" ($left.Count -eq 0)
}

# ---------------------------------------------------------------- S3
function Invoke-S3 {
  Write-Host ""
  Write-Host "=== S3 synthetic lifecycle and insurances #1/#2/#5 ==="
  Write-Host "    (--assume-explorer-foreground: this section verifies the lifecycle, not the"
  Write-Host "     foreground precondition, and must not steal focus to do its job)"
  Stop-AllSidecar
  $run = Start-Sidecar @('--assume-explorer-foreground') 's3'
  if ($run.Process.HasExited) { Check "S3.0 app alive" $false; return }

  $lines = Get-LogLines $run.Log
  Check "S3.0 foreground check disabled for this run and logged" (Test-Log $lines 'foreground check DISABLED \(--assume-explorer-foreground\)')

  # -- trigger release drives insurance #2
  [Probe]::MoveTo(900, 800) | Out-Null
  Start-Sleep -Milliseconds 250
  [Probe]::Mouse([Probe]::LEDOWN) | Out-Null
  Start-Sleep -Milliseconds 200
  [Probe]::Key($VK_SPACE, $false) | Out-Null
  Start-Sleep -Milliseconds 150
  for ($i = 0; $i -lt 4; $i++) { [Probe]::MoveRel(30, 20) | Out-Null; Start-Sleep -Milliseconds 60 }
  Start-Sleep -Milliseconds 400
  $lines = Get-LogLines $run.Log
  Check "S3.1 state machine Idle->Armed->Capturing" ((Test-Log $lines 'Idle -> Armed') -and (Test-Log $lines 'Armed -> Capturing'))
  Check "S3.2 hit layer raised on capture" (Test-Log $lines 'hit layer SHOWN')

  $raised = @([Probe]::HitWindows([uint32]$run.Process.Id))
  Check "S3.3 raised window is visible to the OS" ((@($raised | Where-Object { $_ -match 'visible=True' }).Count) -ge 1)

  [Probe]::Key($VK_SPACE, $true) | Out-Null
  Start-Sleep -Milliseconds 900
  $lines = Get-LogLines $run.Log
  Check "S3.4 trigger release requests release (insurance #2)" (Test-Log $lines 'release requested: trigger released')
  Check "S3.5 hidden after 250 ms grace" (Test-Log $lines 'hit layer HIDDEN \(250 ms grace elapsed\)')

  # -- mouse up drives insurance #1
  [Probe]::Mouse([Probe]::LEUP) | Out-Null
  Start-Sleep -Milliseconds 400
  [Probe]::MoveTo(900, 800) | Out-Null      # same vetted empty spot as round 1
  Start-Sleep -Milliseconds 250
  [Probe]::Mouse([Probe]::LEDOWN) | Out-Null
  Start-Sleep -Milliseconds 200
  [Probe]::Key($VK_SPACE, $false) | Out-Null
  Start-Sleep -Milliseconds 150
  for ($i = 0; $i -lt 4; $i++) { [Probe]::MoveRel(-30, -20) | Out-Null; Start-Sleep -Milliseconds 60 }
  Start-Sleep -Milliseconds 400
  [Probe]::Mouse([Probe]::LEUP) | Out-Null
  Start-Sleep -Milliseconds 900
  $lines = Get-LogLines $run.Log
  Check "S3.6 second raise happened" ((Count-Log $lines 'hit layer SHOWN') -ge 2)
  Check "S3.7 mouse up requests release (insurance #1)" (Test-Log $lines 'release requested: mouse up')
  [Probe]::Key($VK_SPACE, $true) | Out-Null
  Start-Sleep -Milliseconds 300

  # -- insurance #5
  [Probe]::Key($VK_CTRL, $false) | Out-Null; [Probe]::Key($VK_ALT, $false) | Out-Null; [Probe]::Key($VK_SHIFT, $false) | Out-Null
  Start-Sleep -Milliseconds 120
  [Probe]::Key($VK_Q, $false) | Out-Null
  Start-Sleep -Milliseconds 250
  [Probe]::Key($VK_Q, $true) | Out-Null; [Probe]::Key($VK_SHIFT, $true) | Out-Null; [Probe]::Key($VK_ALT, $true) | Out-Null; [Probe]::Key($VK_CTRL, $true) | Out-Null
  Start-Sleep -Milliseconds 800
  $lines = Get-LogLines $run.Log
  Check "S3.8 kill switch fired (insurance #5)" (Test-Log $lines '\*\*\* KILL SWITCH')
  Check "S3.9 detector disabled" (Test-Log $lines 'detector DISABLED')

  $before = Count-Log $lines 'hit layer SHOWN'
  [Probe]::Mouse([Probe]::LEDOWN) | Out-Null
  Start-Sleep -Milliseconds 150
  [Probe]::Key($VK_SPACE, $false) | Out-Null
  Start-Sleep -Milliseconds 100
  for ($i = 0; $i -lt 4; $i++) { [Probe]::MoveRel(25, 25) | Out-Null; Start-Sleep -Milliseconds 60 }
  [Probe]::Key($VK_SPACE, $true) | Out-Null
  [Probe]::Mouse([Probe]::LEUP) | Out-Null
  Start-Sleep -Milliseconds 800
  $lines = Get-LogLines $run.Log
  $after = Count-Log $lines 'hit layer SHOWN'
  Check "S3.10 no further raise after kill switch ($before -> $after)" ($after -eq $before)

  Stop-Process -Id $run.Process.Id -Force
  Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------- S3b watchdog
function Invoke-S3b {
  Write-Host ""
  Write-Host "=== S3b insurance #3 watchdog (default value and actual firing) ==="
  Stop-AllSidecar

  $run = Start-Sidecar @('--raise') 's3b-default'
  $lines = Get-LogLines $run.Log
  Check "S3b.1 default watchdog timeout is 30000 ms in evidence" (Test-Log $lines 'guard #3 armed \(30000 ms\)')
  Stop-Process -Id $run.Process.Id -Force
  Start-Sleep -Milliseconds 400

  $run2 = Start-Sidecar @('--raise', '--watchdog-seconds', "$WatchdogSeconds") 's3b-fire'
  $l1 = Get-LogLines $run2.Log
  Check "S3b.2 diagnostic override is written to evidence" (Test-Log $l1 "guard #3 armed \($($WatchdogSeconds * 1000) ms\)")
  Check "S3b.3 raised" (Test-Log $l1 'hit layer SHOWN')
  Start-Sleep -Milliseconds ($WatchdogSeconds * 1000 + 1200)
  $l2 = Get-LogLines $run2.Log
  Check "S3b.4 watchdog hides on timeout" (Test-Log $l2 'hit layer HIDDEN \(guard #3 watchdog\)')
  $wins = @([Probe]::HitWindows([uint32]$run2.Process.Id))
  Check "S3b.5 OS confirms hidden after watchdog" ($wins.Count -gt 0 -and (@($wins | Where-Object { $_ -match 'visible=True' }).Count) -eq 0)
  Stop-Process -Id $run2.Process.Id -Force
  Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------- S4
function Invoke-S4 {
  Write-Host ""
  Write-Host "=== S4 synthetic OLE drag onto the raised hit layer ==="
  if ($DragSim -eq '' -or -not (Test-Path -LiteralPath $DragSim)) {
    Inconclusive "S4 needs -DragSim <path to DragSim.exe>; drop delivery not verified"
    return
  }
  Stop-AllSidecar

  $sample = Join-Path $LogDir 's4-sample.txt'
  Set-Content -LiteralPath $sample -Value ('m1a s4 sample ' + [guid]::NewGuid().ToString()) -Encoding ASCII
  $before = Get-FileHash -LiteralPath $sample -Algorithm SHA256
  $mtimeBefore = (Get-Item -LiteralPath $sample).LastWriteTimeUtc

  $run = Start-Sidecar @('--raise', '--watchdog-seconds', '120') 's4'
  $wins = @([Probe]::HitWindows([uint32]$run.Process.Id))
  Check "S4.1 hit layer raised for the drop test" ((@($wins | Where-Object { $_ -match 'visible=True' }).Count) -ge 1)

  $cx = [int]([Probe]::GetSystemMetrics(0) / 2)
  $cy = [int]([Probe]::GetSystemMetrics(1) / 2)
  $sx = $cx - 500; $sy = $cy
  Write-Host "      drag $sample from ($sx,$sy) to ($cx,$cy)"
  $drag = Start-Process -FilePath $DragSim -ArgumentList @('--drag', $sample, "$sx", "$sy", "$cx", "$cy") -Wait -PassThru -RedirectStandardOutput (Join-Path $LogDir 's4-drag.out.txt') -RedirectStandardError (Join-Path $LogDir 's4-drag.err.txt')
  Write-Host "      drag exit=$($drag.ExitCode)"
  Start-Sleep -Milliseconds 800

  $lines = Get-LogLines $run.Log
  Check "S4.2 drop intercepted (WM_DROPFILES delivered)" ((Count-Log $lines '\*\*\* DROP RECEIVED') -ge 1)
  Check "S4.3 the received path is the sample file" (Test-Log $lines ([regex]::Escape($sample)))
  Check "S4.4 hidden immediately after the drop" (Test-Log $lines 'hit layer HIDDEN \(drop resolved\)')
  Check "S4.5 the app is still alive after the drop" (-not $run.Process.HasExited)

  $after = Get-FileHash -LiteralPath $sample -Algorithm SHA256
  $mtimeAfter = (Get-Item -LiteralPath $sample).LastWriteTimeUtc
  Check "S4.6 source file untouched: same SHA256" ($before.Hash -eq $after.Hash)
  Check "S4.7 source file untouched: same mtime" ($mtimeBefore -eq $mtimeAfter)

  if (-not $run.Process.HasExited) { Stop-Process -Id $run.Process.Id -Force }
  Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------- S4m (human)
function Invoke-S4m([string]$Mode, [int]$Window) {
  Write-Host ""
  if ($Mode -eq 'S4a') {
    Write-Host "=== S4a HUMAN: drag files WITHOUT holding the trigger key (expect nothing at all) ==="
  } else {
    Write-Host "=== S4b HUMAN: drag files while HOLDING the trigger key (expect drop list + immediate hide) ==="
  }
  Stop-AllSidecar
  $run = Start-Sidecar @() $Mode
  $run.Process.Refresh()
  if ($run.Process.HasExited) { Check "$Mode app alive" $false; return }

  $instr = if ($Mode -eq 'S4a') {
    "Arrange Explorer yourself. Drag one or more files to a folder or the desktop, WITHOUT pressing the trigger key. Do it 3 times."
  } else {
    "Arrange Explorer yourself. Drag one or more files, HOLD the trigger key (space by default) while dragging, and drop them anywhere. Do it 3 times."
  }
  Write-Host "      $instr"
  Write-Host "      waiting $Window seconds ..."
  Start-Sleep -Seconds $Window

  $lines = Get-LogLines $run.Log
  if ($Mode -eq 'S4a') {
    Check "S4a.1 nothing was raised (A7 zero interference)" ((Count-Log $lines 'hit layer SHOWN') -eq 0)
    Check "S4a.2 no drop was intercepted" ((Count-Log $lines 'DROP RECEIVED') -eq 0)
  } else {
    Check "S4b.1 at least 3 drops intercepted (A2)" ((Count-Log $lines '\*\*\* DROP RECEIVED') -ge 3)
    Check "S4b.2 hidden immediately after drop" ((Count-Log $lines 'hit layer HIDDEN \(drop resolved\)') -ge 3)
    Check "S4b.3 file paths were read" ((Count-Log $lines '^.*\|?\s{4}[A-Za-z]:\\') -ge 3)
  }
  $run.Process.Refresh()
  if (-not $run.Process.HasExited) { Stop-Process -Id $run.Process.Id -Force }
  Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------- S5
function Invoke-S5 {
  Write-Host ""
  Write-Host "=== S5 kill the process while raised, restart must be clean (A13) ==="
  Stop-AllSidecar

  $run = Start-Sidecar @('--raise', '--watchdog-seconds', '120') 's5'
  $wins = @([Probe]::HitWindows([uint32]$run.Process.Id))
  Check "S5.1 raised before the kill" ((@($wins | Where-Object { $_ -match 'visible=True' }).Count) -ge 1)

  Stop-Process -Id $run.Process.Id -Force
  Start-Sleep -Milliseconds 900
  $left = @([Probe]::HitWindows(0))
  Check "S5.2 no hit window survives the kill" ($left.Count -eq 0)

  $dump = Invoke-Capture @('--dump-monitors')
  Check "S5.3 restart works right after the kill" ($dump.Exit -eq 0)
  $run2 = Start-Sidecar @() 's5-restart'
  $run2.Process.Refresh()
  Check "S5.4 new instance runs normally" (-not $run2.Process.HasExited)
  Stop-Process -Id $run2.Process.Id -Force
  Start-Sleep -Milliseconds 400
  Stop-AllSidecar
}

# ---------------------------------------------------------------- drive
$sections = switch ($Section) {
  'All' { @('S1', 'S2', 'S3', 'S3b', 'S4', 'S5') }
  'S4m' { @('S4a', 'S4b') }
  default { @($Section) }
}

Write-Host "verify-m1a: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

foreach ($s in $sections) {
  switch ($s) {
    'S1'  { Invoke-S1 }
    'S2'  { Invoke-S2 }
    'S3'  { Invoke-S3 }
    'S3b' { Invoke-S3b }
    'S4'  { Invoke-S4 }
    'S4a' { Invoke-S4m 'S4a' $S4WindowSeconds }
    'S4b' { Invoke-S4m 'S4b' $S4WindowSeconds }
    'S5'  { Invoke-S5 }
    default { Write-Host "unknown section: $s" }
  }
}

Stop-AllSidecar

if ($EvidenceDir -ne '') {
  if (-not (Test-Path -LiteralPath $EvidenceDir)) { New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null }
  foreach ($f in Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -ErrorAction SilentlyContinue) {
    Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $EvidenceDir $f.Name) -Force
  }
  Write-Host "logs copied to $EvidenceDir"
}

$fail = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host "M1a summary: $($results.Count - $fail)/$($results.Count) checks passed ($Section)"
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
