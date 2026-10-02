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
  [string]$ArchiveDir = '',
  [int]$S4WindowSeconds = 75,
  [int]$WatchdogSeconds = 3,
  [string]$LogDir = "$env:TEMP\sidecar-verify"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

# MEASURED 2026-10-02: S4 has reported "needs -DragSim" INCONCLUSIVE on every run for weeks, but
# DragSim has existed in this repo since 2026-09-30 and its own comment records it producing
# "OLE DROP RECEIVED (IDropTarget)" against the current receiver. The gap was never capability --
# it was that nobody passed the path. A check that is permanently inconclusive is a check nobody
# reads, so auto-detect it; an explicit -DragSim still wins.
# Verification must not build anything behind the operator's back, so when the binary is missing the
# message names the exact command instead of quietly staying inconclusive forever.
if ($DragSim -eq '') {
  $dragSimCandidate = Join-Path $PSScriptRoot 'dragsim\bin\Release\net8.0-windows\DragSim.exe'
  if (Test-Path -LiteralPath $dragSimCandidate) {
    $DragSim = $dragSimCandidate
    Write-Host "      auto-detected DragSim: $dragSimCandidate"
  } else {
    Write-Host "      no DragSim at $dragSimCandidate"
    Write-Host "      build it with: dotnet build tools\dragsim\DragSim.csproj -c Release -v minimal -nologo"
  }
}

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
      if (sb.ToString() != "ConfernHitLayer") return true;
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

  // --- token integrity: the direct measurement for "runs as a standard user" (A14) ---
  // The application manifest is embedded in the exe as a Win32 resource, so checking for a
  // .manifest file next to the binary is the wrong instrument. Measure the running process.
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);

  /// Returns the last subauthority of the token integrity SID, or -1 on failure.
  /// 0x1000 = low, 0x2000 = medium (standard user), 0x3000 = high (elevated), 0x4000 = system.
  /// Do not mix these up: medium is 8192 = 0x2000, NOT 0x1000.
  public static int IntegrityRid(uint pid) {
    IntPtr p = OpenProcess(0x1000, false, pid);              // PROCESS_QUERY_LIMITED_INFORMATION
    if (p == IntPtr.Zero) return -1;
    IntPtr tok;
    if (!OpenProcessToken(p, 0x0008, out tok)) { CloseHandle(p); return -1; }   // TOKEN_QUERY
    int len = 0;
    GetTokenInformation(tok, 25, IntPtr.Zero, 0, out len);   // TokenIntegrityLevel
    int rid = -1;
    if (len > 0) {
      IntPtr buf = Marshal.AllocHGlobal(len);
      if (GetTokenInformation(tok, 25, buf, len, out len)) {
        IntPtr sid = Marshal.ReadIntPtr(buf);                // TOKEN_MANDATORY_LABEL.Sid
        byte subCount = Marshal.ReadByte(sid, 1);            // SID.SubAuthorityCount
        rid = Marshal.ReadInt32(sid, 8 + (subCount - 1) * 4);
      }
      Marshal.FreeHGlobal(buf);
    }
    CloseHandle(tok); CloseHandle(p);
    return rid;
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
# P1-2 (2026-10-02 falsifiability audit): how many checks this run MUST register, mirroring
# every branch that changes the number. A section that silently stops registering (m1b's
# S6.3-S6.7 did exactly that) would otherwise still end as "N/N passed" with no second number
# to compare against. The driver adds a section's count when it runs it; a branch that
# registers fewer adds a negative correction (Expect -N) where it returns early.
$script:ExpectedChecks = 0
function Expect([int]$n) { $script:ExpectedChecks += $n }
# A section that cannot set up its own precondition must never be reported as PASS or FAIL.
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason)
  Write-Host "INCONCLUSIVE  $reason"
}
function Get-LogLines([string]$p) { if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() } }
function Test-Log([string[]]$lines, [string]$pattern) { return [bool]($lines | Select-String -Pattern $pattern -Quiet) }
function Count-Log([string[]]$lines, [string]$pattern) { return @($lines | Select-String -Pattern $pattern).Count }

function Stop-AllSidecar {
  Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

function Start-Sidecar([string[]]$Argv, [string]$Name) {
  $log = Join-Path $LogDir "$Name.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  # --no-autostart: a resident run applies the startWithWindows setting (spec 8), whose spec default
  # is ON. Without this flag a regression run would write the user's Run key with a path into
  # bin\Release -- a system change nobody asked for. The flag is passed here, once, so every
  # resident launch in this file is isolated from it.
  $proc = Start-Process -FilePath $Exe -ArgumentList ($Argv + @('--no-autostart', '--quiet', '--log', $log)) -PassThru
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

# ---------------------------------------------------------------- S0
function Invoke-S0 {
  Write-Host ""
  Write-Host "=== S0 no elevation (A14) ==="
  $groups = (& whoami /groups) -join "`n"
  Check "S0.1 this session is NOT elevated (medium integrity, S-1-16-8192)" ($groups -match 'S-1-16-8192')

  Stop-AllSidecar
  $run = Start-Sidecar @() 's0'
  $rid = [Probe]::IntegrityRid([uint32]$run.Process.Id)
  Write-Host ("      app token integrity RID = 0x{0:X}" -f $rid)
  Check "S0.2 the app runs at medium integrity, i.e. NOT elevated (0x2000)" ($rid -eq 0x2000)
  if (-not $run.Process.HasExited) { Stop-Process -Id $run.Process.Id -Force }
  Start-Sleep -Milliseconds 300
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
  # P0-3 (2026-10-02): S2.5 used to call WaitForExit() and THEN ask EnumWindows how many
  # windows the process owned. A process that has already exited owns none, so the answer was
  # 0 before the question was asked: replacing the product with a stub could not have moved
  # it. The instrument now runs WHILE the 50 cycles happen, which is the only moment this
  # property is observable -- the runner has to SEE the hit window appear, and SEE it gone
  # again at the end. Started directly instead of through Start-Sidecar because that helper
  # sleeps 1500 ms, which would eat half of the ~3 s window this measurement lives in.
  $log = Join-Path $LogDir 's2.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @('--cycle-test', '50', '--no-autostart', '--quiet', '--log', $log) -PassThru
  $run = [pscustomobject]@{ Process = $proc; Log = $log }
  $visibleSamples = 0
  $liveSamples = 0
  $lastVisible = -1
  $pollDeadline = [DateTime]::UtcNow.AddSeconds(90)
  while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $pollDeadline) {
    $w = @([Probe]::HitWindows([uint32]$proc.Id))
    if ($w.Count -gt 0) {
      $liveSamples++
      $lastVisible = @($w | Where-Object { $_ -match 'visible=True' }).Count
      if ($lastVisible -ge 1) { $visibleSamples++ }
    }
    Start-Sleep -Milliseconds 15
  }
  $proc.WaitForExit()
  # A window the process has already released cannot be observed, so "nothing is left on
  # screen" is measured at the LAST sample taken while it was still alive. A window still
  # raised when the loop ends keeps $lastVisible >= 1 and fails there.
  $leftAlive = @([Probe]::HitWindows(0) | Where-Object { $_ -match 'visible=True' }).Count
  Check "S2.1 --cycle-test 50 exits 0 (everything hidden at the end)" ($run.Process.ExitCode -eq 0)
  $lines = Get-LogLines $run.Log
  Check "S2.2 exactly 50 raises logged" ((Count-Log $lines 'hit layer SHOWN') -eq 50)
  Check "S2.3 ends allHidden=True" (Test-Log $lines 'cycle test done: 50 cycles; allHidden=True')
  Check "S2.4 no leftover window handles" (Test-Log $lines 'hit layer disposed; leftoverWindows=0')
  Write-Host ("      [os] live samples=$liveSamples visible samples=$visibleSamples lastVisibleWhileAlive=$lastVisible visibleAfterExit=$leftAlive")
  Check "S2.5 the OS saw the hit window raised during the cycles and gone at the end (live instrument)" `
    (($liveSamples -ge 1) -and ($visibleSamples -ge 1) -and ($lastVisible -eq 0) -and ($leftAlive -eq 0))
}

# ---------------------------------------------------------------- S3
function Invoke-S3 {
  Write-Host ""
  Write-Host "=== S3 synthetic lifecycle and insurances #1/#2/#5 ==="
  Write-Host "    (--assume-explorer-foreground: this section verifies the lifecycle, not the"
  Write-Host "     foreground precondition, and must not steal focus to do its job)"
  Stop-AllSidecar
  $run = Start-Sidecar @('--assume-explorer-foreground') 's3'
  if ($run.Process.HasExited) { Expect -11; Check "S3.0 app alive" $false; return }

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
  # P0-4 (2026-10-02): every check above reads the app's own log file, which proves what the
  # app SAYS. The runner now asks the OS directly, at exactly the moment the property has to
  # hold (after the 250 ms grace hide), so a layer whose Hide() became a no-op is caught here
  # even though its log line still appears. Measured here, asserted as S3.11 at the end of the
  # section so that no check id below it has to be renumbered.
  $postGrace = @([Probe]::HitWindows([uint32]$run.Process.Id))
  $graceWindows = $postGrace.Count
  $graceVisible = @($postGrace | Where-Object { $_ -match 'visible=True' }).Count
  $graceHidden = ($graceWindows -ge 1) -and ($graceVisible -eq 0)

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

  # P0-4: the OS measurement taken after the 250 ms grace hide, asserted here so that this
  # section's ids stay contiguous (S3.1-S3.11) and nothing below has to be renumbered.
  Write-Host ("      [os] after the grace hide: $graceWindows hit window(s) still existing, visible=$graceVisible")
  Check "S3.11 the OS agrees the layer is hidden after the 250 ms grace (not just the log line)" $graceHidden

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
    Expect -8
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
  # P0-4 (2026-10-02): S4.4 proves the app WROTE "hidden after the drop". This is the same
  # property asked of the OS, at the moment it has to hold. Asserted as S4.8 at the end of the
  # section so nothing below this line has to be renumbered.
  $postDrop = @([Probe]::HitWindows([uint32]$run.Process.Id))
  $dropWindows = $postDrop.Count
  $dropVisible = @($postDrop | Where-Object { $_ -match 'visible=True' }).Count
  $dropHidden = ($dropWindows -ge 1) -and ($dropVisible -eq 0)

  $lines = Get-LogLines $run.Log
  # MEASURED 2026-10-01: once RegisterDragDrop is in place, WM_DROPFILES never arrives.
  # The very same DragSim produced "*** DROP RECEIVED on hit#0" against the commit
  # before RegisterDragDrop, and only "*** OLE DROP RECEIVED (IDropTarget)" after it.
  # This check used to assert the WM_DROPFILES line, so from that commit on it was red
  # no matter what the app did: a check that cannot pass is as worthless as one that
  # cannot fail. The drop list is now delivered by the OLE path; DragAcceptFiles stays
  # as a fallback only.
  $oleDrop = Count-Log $lines '\*\*\* OLE DROP RECEIVED \(IDropTarget\)'
  $wmDrop = Count-Log $lines '\*\*\* DROP RECEIVED on hit#'
  Check "S4.2 drop intercepted (OLE IDropTarget)" ($oleDrop -ge 1) "OLE=$oleDrop WM_DROPFILES=$wmDrop (WM is only a fallback now)"
  Check "S4.3 the drop carried exactly one file" ((Count-Log $lines 'OLE DROP RECEIVED \(IDropTarget\): hit#\d+ files=1') -ge 1) ''
  Check "S4.3b the received path is the sample file" (Test-Log $lines ([regex]::Escape($sample)))
  Check "S4.4 hidden immediately after the drop" (Test-Log $lines 'hit layer HIDDEN \(drop resolved\)')
  Check "S4.5 the app is still alive after the drop" (-not $run.Process.HasExited)

  $after = Get-FileHash -LiteralPath $sample -Algorithm SHA256
  $mtimeAfter = (Get-Item -LiteralPath $sample).LastWriteTimeUtc
  Check "S4.6 source file untouched: same SHA256" ($before.Hash -eq $after.Hash)
  Check "S4.7 source file untouched: same mtime" ($mtimeBefore -eq $mtimeAfter)
  # P0-4: the OS measurement taken right after the drop resolved (see the capture site).
  Write-Host ("      [os] after the drop: $dropWindows hit window(s) still existing, visible=$dropVisible")
  Check "S4.8 the OS agrees the layer is hidden after the drop resolved (not just the log line)" $dropHidden

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
    Check "S4a.2 no drop was intercepted" ((Count-Log $lines '\*\*\* (OLE )?DROP RECEIVED') -eq 0)
  } else {
    # The product contract of a real gesture, in order: the payload is captured DURING
    # the drag (OLE DragEnter), the wheel commits on the mouse RELEASE (spec 4.3), and
    # the worker really converted something. Counted per gesture, so three each.
    Check "S4b.1 payload captured during the drag (OLE DragEnter) x3" ((Count-Log $lines 'OLE DragEnter') -ge 3)
    Check "S4b.2 mouse release committed a conversion x3" ((Count-Log $lines 'release\(mouse up\): wheel visible') -ge 3)
    Check "S4b.3 the worker converted the file x3" ((Count-Log $lines 'worker result: ok=True') -ge 3)
    Check "S4b.4 the layer was hidden after each gesture x3" ((Count-Log $lines 'hit layer HIDDEN') -ge 3)
    $hideReasons = @(Get-LogLines $run.Log | Select-String 'hit layer HIDDEN \(' |
                    ForEach-Object { [regex]::Match($_.Line, 'HIDDEN \((.*?)\)').Groups[1].Value } | Sort-Object -Unique)
    $wmDrops = Count-Log $lines '\*\*\* DROP RECEIVED on hit#'
    Write-Host ("      [info] hidden reasons seen: " + ($hideReasons -join ', '))
    Write-Host ("      [info] WM_DROPFILES lines: " + $wmDrops + " (0 is expected while a drop target is registered)")
  }

  if ($ArchiveDir -ne '') {
    New-Item -ItemType Directory -Force -Path $ArchiveDir | Out-Null
    $arch = Join-Path $ArchiveDir ("s4m-" + $Mode + "-" + (Get-Date).ToString('yyyy-MM-dd-HHmmss') + ".log")
    Copy-Item -LiteralPath $run.Log -Destination $arch -Force
    Write-Host "      [evidence] session log archived -> $arch"
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
# -Section accepts a comma-separated list (e.g. -Section S0,S1,S2,S5). Unknown names are a
# hard error: a runner that silently runs nothing and reports "0/0 passed (exit 0)" is worse
# than no runner at all (see 99-reflection.md: a check that can never fail).
$knownSections = @('S0', 'S1', 'S2', 'S3', 'S3b', 'S4', 'S4a', 'S4b', 'S5')
$sections = switch ($Section) {
  'All' { @('S0', 'S1', 'S2', 'S3', 'S3b', 'S4', 'S5') }
  'S4m' { @('S4a', 'S4b') }
  default { @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }) }
}
$unknown = @($sections | Where-Object { $knownSections -notcontains $_ })
if ($unknown.Count -gt 0) {
  Write-Host "unknown section(s): $($unknown -join ', ')"
  Write-Host "known: All, S4m, $($knownSections -join ', ')"
  exit 2
}
if ($sections.Count -eq 0) { Write-Host "no sections requested"; exit 2 }

Write-Host "verify-m1a: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

foreach ($s in $sections) {
  # P1-2: each section's check count, mirrored here. S3 and S4 can register fewer checks when
  # a precondition fails, and those branches correct the total themselves (see Expect -N).
  switch ($s) {
    'S0'  { Expect 2;  Invoke-S0 }
    'S1'  { Expect 11; Invoke-S1 }
    'S2'  { Expect 5;  Invoke-S2 }
    'S3'  { Expect 12; Invoke-S3 }
    'S3b' { Expect 5;  Invoke-S3b }
    'S4'  { Expect 9;  Invoke-S4 }
    'S4a' { Expect 2;  Invoke-S4m 'S4a' $S4WindowSeconds }
    'S4b' { Expect 4;  Invoke-S4m 'S4b' $S4WindowSeconds }
    'S5'  { Expect 4;  Invoke-S5 }
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
$registered = $results.Count + $inconclusive.Count
if ($results.Count -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
# P1-2: the sections promise a number of checks; anything below it means a block silently did
# not register (the failure mode behind the 19-check numbering drift this project already hit).
Write-Host "checks=$registered pass=$($results.Count - $fail) fail=$fail inconclusive=$($inconclusive.Count) expected=$($script:ExpectedChecks)"
if ($registered -ne $script:ExpectedChecks) {
  Write-Host "FATAL: registered $registered checks but the selected sections promise $($script:ExpectedChecks)."
  Write-Host "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
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
