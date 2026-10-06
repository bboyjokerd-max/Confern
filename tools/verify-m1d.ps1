param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1d-verify",
  # The output folder the S12.10 payload run is put in, through a settings file THIS
  # suite writes (see the S12.10 block). Default "" = next to the source. Passing
  # "desktop" runs the very same checks against the real shell desktop. Either way the
  # suite neither reads nor writes the operator's own settings file, so the result is
  # the same whatever that file says -- and this parameter is how both states get
  # exercised without ever touching it.
  [string]$PayloadOutputFolder = ''
)
# =============================================================================
#  M1d / M1e regression (S12): the file contract, the failure contract,
#  and the two-process conversion path.
#
#  ASCII-only on purpose: PS 5.1 reads BOM-less scripts as ANSI and would
#  mangle CJK literals. Expected CJK user messages are therefore built from
#  code points ([char]0x....), which is ugly but cannot be corrupted.
#
#  exit 0 = all pass, 1 = at least one FAIL, 2 = zero checks ran,
#  3 = inconclusive (a precondition could not be established)
# =============================================================================
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

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

# =============================================================================
#  P1-2 (2026-10-02 falsifiability audit): how many checks this run MUST register.
#
#  The problem this solves: a block whose precondition silently fails can register
#  NOTHING, and the summary then prints "N/N" with no second number to compare N
#  against (verify-m1b.ps1's S6.3-S6.7 did exactly that). The number below is the
#  count this file promises for an unconditional run; the Expect calls inside the
#  few branching blocks account for the paths that register a different number.
# =============================================================================
# NOTE (2026-10-06): 56 is an unconditional-run figure. The three conditional
# blocks below register 18 more between them (measured, not guessed: 3 + 3 + 12),
# so a normal run totals 74. Change this number ONLY together with the checks
# themselves -- a mismatch is reported as a red run on purpose.
$script:ExpectedChecks = 56
function Expect([int]$n) { $script:ExpectedChecks += $n }

# CJK expected strings, built from code points so this file stays pure ASCII.
$MSG_DECODE  = [string]::Join('', [char[]]@(0x7121,0x6CD5,0x89E3,0x78BC))            # wu fa jie ma
$MSG_MISSING = [string]::Join('', [char[]]@(0x4F86,0x6E90,0x6A94,0x6848,0x5DF2,0x4E0D,0x5B58,0x5728))  # source file no longer exists
$MSG_UNSUP   = [string]::Join('', [char[]]@(0x4E0D,0x652F,0x63F4,0x7684,0x683C,0x5F0F))  # unsupported format

"=== setup ==="
if (-not (Test-Path -LiteralPath $Exe)) { "executable not found: $Exe"; exit 2 }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$logDir = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logCounter = 0

function Run-Convert([string[]]$files, [string]$target, [string]$tag) {
  $script:logCounter++
  $log = Join-Path $logDir "$tag-$($script:logCounter).log"
  $args = @('--convert', ($files -join ';'), '--target', $target, '--quiet', '--log', $log)
  $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru -Wait -WindowStyle Hidden
  return @{ Exit = $p.ExitCode; Log = $log; Text = (Get-Content -LiteralPath $log -Encoding UTF8) -join "`n" }
}

# ---------------------------------------------------------------------------
#  Helpers shared by the S12.10 payload block and the S12.16 output-folder
#  block. They sit up here because PS 5.1 executes a script top to bottom: a
#  function defined further down is NOT callable from a call site above it.
# ---------------------------------------------------------------------------

# Does this path name the same folder? (case and trailing separators ignored)
function Test-DSameFolder([string]$a, [string]$b) {
  if (-not $a -or -not $b) { return $false }
  $x = [System.IO.Path]::GetFullPath($a).TrimEnd([char[]]@([char]92, [char]47))
  $y = [System.IO.Path]::GetFullPath($b).TrimEnd([char[]]@([char]92, [char]47))
  return [string]::Equals($x, $y, [System.StringComparison]::OrdinalIgnoreCase)
}

# A settings file whose ONLY field matters here. ASCII, no BOM (the loader must not
# be given a different document shape than the one the product writes).
function Write-DSettings([string]$path, [string]$outputFolder) {
  $json = '{ "OutputFolder": "' + $outputFolder + '" }'
  [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

# ---- the line the user actually sees ----------------------------------------
# ConversionSummary.ComposeLine is the ONE rule: "check N" when all succeed, the
# spec 7.1 message when ALL fail, the "check N / X M" counts when only some fail
# (7.3). Two places render it: the headless "convert summary:" line and the
# progress pill (via ConversionSnapshot.Line). On 2026-10-01 those two had
# drifted apart and the pill showed a bare count while the reason existed only in
# the log. S12.6b/S12.7b/S12.8b could not catch that: they only prove the reason
# appears SOMEWHERE in the log, and the worker-result line always prints it.
function Get-SummaryLine([string]$text) {
  $m = [regex]::Match($text, 'convert summary: (.*?)\s*\(ok=')
  if ($m.Success) { return $m.Groups[1].Value.Trim() }
  return ''
}
function Test-SummaryCarries([string]$text, [string]$reason) {
  $line = Get-SummaryLine $text
  if (-not $line) { return $false }
  return $line.Contains($reason)
}
function Get-PillLine([string]$text) {
  $m = [regex]::Match($text, 'progress: (.*?)\s+done=')
  if ($m.Success) { return $m.Groups[1].Value.Trim() }
  return ''
}
$CHECKMARK = [char]0x2713
$CROSSMARK = [char]0x2717

# ---- fixture: a photo-like JPEG with real gradients (not a flat colour, which
#      would hide any pixel-format mistake) --------------------------------
$w = 320; $h = 200
$bmp = [System.Drawing.Bitmap]::new($w, $h)
for ($y = 0; $y -lt $h; $y++) {
  for ($x = 0; $x -lt $w; $x++) {
    $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, ($x * 255 / $w), ($y * 255 / $h), (($x + $y) % 256)))
  }
}
$srcJpg = Join-Path $Work 'art.jpg'
$jpgCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$ep = [System.Drawing.Imaging.EncoderParameters]::new(1)
$ep.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, [int64]95)
$bmp.Save($srcJpg, $jpgCodec, $ep)
$bmp.Dispose()
$srcHash = (Get-FileHash -LiteralPath $srcJpg -Algorithm SHA256).Hash
$srcStamp = (Get-Item -LiteralPath $srcJpg).LastWriteTimeUtc.ToString('o')
$srcLen = (Get-Item -LiteralPath $srcJpg).Length

function Sample-Rgb([string]$path, [int]$x, [int]$y) {
  $b = [System.Drawing.Bitmap]::FromFile($path)
  try { $c = $b.GetPixel($x, $y); return @($c.R, $c.G, $c.B) }
  finally { $b.Dispose() }
}

"=== S12.1-S12.2 conversion correctness ==="
$r = Run-Convert @($srcJpg) 'PNG' 'basic'
$outPng = Join-Path $Work 'art.png'
Check 'S12.1a exit code 0' ($r.Exit -eq 0) "exit=$($r.Exit)"
Check 'S12.1b output exists' (Test-Path -LiteralPath $outPng) $outPng
if (Test-Path -LiteralPath $outPng) {
  Expect 3
  $bytes = [System.IO.File]::ReadAllBytes($outPng)
  $sig = ($bytes[0] -eq 0x89) -and ($bytes[1] -eq 0x50) -and ($bytes[2] -eq 0x4E) -and ($bytes[3] -eq 0x47)
  Check 'S12.1c real PNG signature' $sig ''
  $ib = [System.Drawing.Bitmap]::FromFile($outPng)
  Check 'S12.1d dimensions preserved' (($ib.Width -eq $w) -and ($ib.Height -eq $h)) "$($ib.Width)x$($ib.Height) vs ${w}x${h}"
  $ib.Dispose()

  $samples = 0; $mismatch = 0
  foreach ($fy in @(0.05, 0.25, 0.5, 0.75, 0.95)) {
    foreach ($fx in @(0.05, 0.25, 0.5, 0.75, 0.95)) {
      $px = [int]($fx * ($w - 1)); $py = [int]($fy * ($h - 1))
      $a = Sample-Rgb $srcJpg $px $py
      $b = Sample-Rgb $outPng $px $py
      $samples++
      if (([Math]::Abs($a[0] - $b[0]) -gt 2) -or ([Math]::Abs($a[1] - $b[1]) -gt 2) -or ([Math]::Abs($a[2] - $b[2]) -gt 2)) {
        $mismatch++
      }
    }
  }
  Check 'S12.2 pixels match the source' ($mismatch -eq 0) "$samples samples, $mismatch over tolerance 2"
} else {
  Expect 1
  Inconclusive 'S12.2 pixels match the source' 'no output to compare'
}

"=== S12.3-S12.5 the source is sacred / no overwrite / no temp leftovers ==="
Check 'S12.3a source hash unchanged' ((Get-FileHash -LiteralPath $srcJpg -Algorithm SHA256).Hash -eq $srcHash) ''
Check 'S12.3b source timestamp unchanged' ((Get-Item -LiteralPath $srcJpg).LastWriteTimeUtc.ToString('o') -eq $srcStamp) ''
Check 'S12.3c source size unchanged' ((Get-Item -LiteralPath $srcJpg).Length -eq $srcLen) ''
$pngHash1 = (Get-FileHash -LiteralPath $outPng -Algorithm SHA256).Hash

$r = Run-Convert @($srcJpg) 'PNG' 'collision'
$outPng1 = Join-Path $Work 'art (1).png'
Check 'S12.4a second conversion uses a suffixed name' (Test-Path -LiteralPath $outPng1) $outPng1
Check 'S12.4b first output NOT overwritten' ((Get-FileHash -LiteralPath $outPng -Algorithm SHA256).Hash -eq $pngHash1) ''
Check 'S12.4c no .tmp-* leftovers' (@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File).Count -eq 0) ''

"=== S12.6-S12.8 the failure contract (spec 7.1) ==="
# a truncated real PNG: WIC reports a format/decode failure, not "not an image"
$okPng = Join-Path $Work 'good.png'
$tmpBmp = [System.Drawing.Bitmap]::new(16, 16)
$tmpBmp.Save($okPng, [System.Drawing.Imaging.ImageFormat]::Png); $tmpBmp.Dispose()
$goodBytes = [System.IO.File]::ReadAllBytes($okPng)
$cutBytes = New-Object byte[] 60
[Array]::Copy($goodBytes, $cutBytes, 60)
$corrupt = Join-Path $Work 'corrupt.png'
[System.IO.File]::WriteAllBytes($corrupt, $cutBytes)
Remove-Item -LiteralPath $okPng -Force

$r = Run-Convert @($corrupt) 'PNG' 'corrupt'
Check 'S12.6a corrupt input -> DecodeFailed' ($r.Text -match 'error=DecodeFailed') 'error=DecodeFailed'
Check 'S12.6b corrupt input -> readable message' ($r.Text.Contains($MSG_DECODE)) $MSG_DECODE
Check 'S12.6c corrupt input -> exit code 9' ($r.Exit -eq 9) "exit=$($r.Exit)"
Check 'S12.6d corrupt input -> the RENDERED line carries the reason' (Test-SummaryCarries $r.Text $MSG_DECODE) (Get-SummaryLine $r.Text)

$r = Run-Convert @("$Work\nope-does-not-exist.jpg") 'PNG' 'missing'
Check 'S12.7a missing source -> SourceMissing' ($r.Text -match 'error=SourceMissing') ''
Check 'S12.7b missing source -> readable message' ($r.Text.Contains($MSG_MISSING)) $MSG_MISSING
Check 'S12.7c missing source -> the RENDERED line carries the reason' (Test-SummaryCarries $r.Text $MSG_MISSING) (Get-SummaryLine $r.Text)

$pdf = Join-Path $Work 'doc.pdf'
Set-Content -LiteralPath $pdf -Value '%PDF-1.4 not an image' -Encoding ASCII
$r = Run-Convert @($pdf) 'PNG' 'unsupported'
Check 'S12.8a pdf -> refused with a message' (($r.Text -match 'error=Unsupported') -or ($r.Text -match 'error=DecodeFailed')) ''
Check 'S12.8b pdf -> readable message' ($r.Text.Contains($MSG_UNSUP)) $MSG_UNSUP
Check 'S12.8c pdf -> the RENDERED line carries the reason' (Test-SummaryCarries $r.Text $MSG_UNSUP) (Get-SummaryLine $r.Text)

# A check that can never fail is worse than none. Prove these assertions reject
# the pre-fix shape (bare counts) and accept the fixed shape. Synthetic text only.
$oldShape = "convert summary: $CHECKMARK 0 / $CROSSMARK 1  (ok=0 failed=1)"
$newShape = "convert summary: corrupt.png $MSG_DECODE  (ok=0 failed=1)"
Check 'S12.8d self-test: the assertion REJECTS the pre-fix bare-counts line' (-not (Test-SummaryCarries $oldShape $MSG_DECODE)) 'synthetic'
Check 'S12.8e self-test: the assertion ACCEPTS the reason-bearing line' (Test-SummaryCarries $newShape $MSG_DECODE) 'synthetic'

# spec 7.3: the reason form is ONLY for a total failure. A partial failure keeps
# the counts form -- the other branch of the same rule, and the thing a careless
# "always show the reason" fix would break.
$r = Run-Convert @($srcJpg, $corrupt) 'PNG' 'partial'
Check 'S12.8f partial failure -> the counts form (spec 7.3)' ((Get-SummaryLine $r.Text) -eq "$CHECKMARK 1 / $CROSSMARK 1") (Get-SummaryLine $r.Text)
Check 'S12.8g partial failure -> 1 ok and 1 failed, no silence' ($r.Text -match '\(ok=1 failed=1\)') ''

"=== S12.9-S12.10 the two-process path (spec 9.1) ==="
# offered formats must be exactly the spec-declared AND measured-encodable set
$codecLog = Join-Path $logDir 'codecs.log'
$p = Start-Process -FilePath $Exe -ArgumentList @('--codecs', '--quiet', '--log', $codecLog) -PassThru -Wait -WindowStyle Hidden
$codecText = (Get-Content -LiteralPath $codecLog -Encoding UTF8) -join "`n"
$offeredLine = ($codecText -split "`n" | Where-Object { $_ -match 'offered=\[' } | Select-Object -First 1)
if ($offeredLine) {
  Expect 3
  $offered = ([regex]::Match($offeredLine, 'offered=\[([^\]]*)\]')).Groups[1].Value -split ','
  $sorted = ($offered | Sort-Object) -join ','
  Check 'S12.9 offered outputs = declared n encodable' ($sorted -eq 'BMP,JPG,PNG,TIF') "offered=$sorted"
  Check 'S12.9b GIF never offered (capable but not declared)' (-not ($offered -contains 'GIF')) ''
  Check 'S12.9c WEBP never offered (declared but not encodable here)' (-not ($offered -contains 'WEBP')) ''
} else {
  Expect 1
  Inconclusive 'S12.9 offered outputs' 'no offered=[] line in the codec log'
}

# the whole point of the worker split: it must never time out
$workerLog = Join-Path $env:LOCALAPPDATA 'Confern\worker.log'
# P0-5 (2026-10-02): the worker opens this file with append:false, so its CONTENT is
# per-run -- but only when the worker actually starts. Nothing removed it, so a run in
# which the worker never came up still found the PREVIOUS run's
# "worker: ready protocolVersion=1" line and S12.10f stayed green while the whole
# two-process path was dead. Delete it first: from here on the line can only exist
# because THIS run's worker wrote it, and its timestamp has to be inside this run.
$staleWorkerBytes = if (Test-Path -LiteralPath $workerLog) { (Get-Item -LiteralPath $workerLog).Length } else { 0 }
if (Test-Path -LiteralPath $workerLog) { Remove-Item -LiteralPath $workerLog -Force -ErrorAction SilentlyContinue }
$payloadStarted = Get-Date
$payloadLog = Join-Path $logDir 'payload.log'
# ---------------------------------------------------------------------------
# P1-11 (2026-10-06): the payload run below is a REAL conversion, so its output
# folder is decided by the settings file in effect (spec 9.5). This run had NO
# --settings-file, so it silently inherited the OPERATOR's own
# %LOCALAPPDATA%\Confern\settings.json: with "OutputFolder": "desktop" the
# artifacts landed on the desktop while S12.10i looked next to the source, and the
# suite went red on a product that was behaving correctly. Measured, same binary,
# one field changed: OutputFolder=desktop -> 65/66 (FAIL S12.10i files=0);
# OutputFolder="" -> 66/66. A suite that is red or green depending on the
# operator's own settings measures nothing, so the settings are the SUITE's to own
# here -- the same device S12.16a/b/c already use. $PayloadOutputFolder exists only
# so BOTH states can be exercised without ever touching the operator's real file.
# ---------------------------------------------------------------------------
$cfgPayload = Join-Path $Work 'payload-settings.json'
Write-DSettings $cfgPayload $PayloadOutputFolder
$payloadDesk = [Environment]::GetFolderPath('DesktopDirectory')
$payloadDest = $PayloadOutputFolder
if ($PayloadOutputFolder -eq '') { $payloadDest = Split-Path -Parent $srcJpg }
if ($PayloadOutputFolder -eq 'desktop') { $payloadDest = $payloadDesk }
# The "desktop" state really writes onto the operator's desktop, so read what is
# there BEFORE the run: the cleanup after S12.10i removes exactly the files this run
# added, and never anything that was already there.
$payloadDeskBefore = @(Get-ChildItem -LiteralPath $payloadDesk -Filter 'art*' -File -ErrorAction SilentlyContinue |
                       ForEach-Object { $_.Name })
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings-file', $cfgPayload, '--wheel-payload', $srcJpg, '--no-autostart', '--quiet', '--log', $payloadLog) -PassThru -Wait -WindowStyle Hidden
$payloadText = (Get-Content -LiteralPath $payloadLog -Encoding UTF8) -join "`n"
Check 'S12.10a payload path converts through the worker' ($payloadText -match 'conversion finished: finished=True ok=1 failed=0') ''

# ---------------------------------------------------------------------------
# P1-10 (2026-10-02): this trio used to be three ways of saying nothing. The pill
# could be drawn NOWHERE and all three stayed green: "updates=0 frames=0" satisfied
# "one window", `frames == updates` compared one number with itself, and "it faded"
# was read from a line that also appears when the pill was never on screen. Each
# check below now demands POSITIVE evidence that the pill was really painted, that
# it was painted on ONE window, and that the fade really reached invisible.
# ---------------------------------------------------------------------------
$instrument = [regex]::Match($payloadText, 'progress instrument: updates=(\d+) frames=(\d+) visible=(True|False)')
$updatesAtInstrument = 0; $framesAtInstrument = 0; $visibleAtInstrument = $false
if ($instrument.Success) {
  $updatesAtInstrument = [int]$instrument.Groups[1].Value
  $framesAtInstrument = [int]$instrument.Groups[2].Value
  $visibleAtInstrument = ($instrument.Groups[3].Value -eq 'True')
}
# "painted" = the instrument line saw the pill drawn at least once AND the OS still
# called the window visible at that moment (IsVisible asks IsWindowVisible since
# 2026-10-02 P0-2, so that field is now an external reading rather than a self-report).
$painted = $instrument.Success -and ($updatesAtInstrument -ge 1) -and ($framesAtInstrument -ge 1) -and $visibleAtInstrument
Check 'S12.10b the pill was really painted AND on screen when the run reported it' `
  $painted "instrument='$($instrument.Value)' updates=$updatesAtInstrument frames=$framesAtInstrument visible=$visibleAtInstrument"
$pillCreated = ([regex]::Matches($payloadText, 'pill window created hwnd=0x[0-9A-F]+ class=ConfernPillWindow')).Count
$pillDisposed = [regex]::Match($payloadText, 'pill window disposed; frames=(\d+) clicks=(\d+)')
$pillFrames = if ($pillDisposed.Success) { [int]$pillDisposed.Groups[1].Value } else { 0 }
Check 'S12.10c the pill is ONE window, repainted in place over the run' `
  (($pillCreated -eq 1) -and ($pillFrames -ge 3)) "windowsCreated=$pillCreated frames=$pillFrames"
Check 'S12.10d the pill was drawn and then faded out on its own (the OS says invisible afterwards)' `
  ($painted -and ($payloadText -match 'progress faded out on its own: True visible=False')) ''
# The fade has exactly ONE driver: the layer's periodic callback. If that callback is
# starved (the old SetTimer/WM_TIMER version was), the line stays on screen forever.
# So "it faded" is not enough: prove the callback kept ticking for the whole window.
# 2.4 s at 80 ms is ~30 ticks; the threshold is 10, which catches a collapsed chain.
# NOTE: keep this file ASCII-only. Raw CJK in a BOM-less .ps1 makes PS 5.1 misparse
# the following statement (the CJK bytes are decoded as ANSI).
$hideLines = @([regex]::Matches($payloadText, 'progress: hiding after \d+ ms \(updates=(\d+), ticks=(\d+), hold='))
if ($hideLines.Count -ge 2) {
  $tickCounts = @($hideLines | ForEach-Object { [int]$_.Groups[2].Value })
  $updateCounts = @($hideLines | ForEach-Object { [int]$_.Groups[1].Value })
  Check 'S12.10d2 tick driver ran the whole fade (not starved) on both conversions' `
        (($tickCounts | Where-Object { $_ -lt 10 }).Count -eq 0) "ticks=$($tickCounts -join ',')"
  Check 'S12.10d3 both fades repainted the same line (>= 3 alpha steps)' `
        (($updateCounts | Where-Object { $_ -lt 3 }).Count -eq 0) "updates=$($updateCounts -join ',')"
} else {
  # A missing hiding line is NOT "undetermined": the pill is supposed to be drawn and
  # fade on every conversion, so a run that never got through a fade is a failure of
  # the feature. Reporting it as INCONCLUSIVE (which does not fail the suite) is how
  # this pair could stay silent while the pill was never painted at all.
  Check 'S12.10d2 tick driver ran the whole fade (not starved) on both conversions' $false `
        "only $($hideLines.Count) hiding line(s): the pill never got through a fade"
  Check 'S12.10d3 both fades repainted the same line (>= 3 alpha steps)' $false `
        "only $($hideLines.Count) hiding line(s)"
}
# P1-9 (2026-10-02): the old predicate only looked for 'timed out', and the only thing
# that prints that string is the 30 s IDLE watchdog. The failure this check names (a
# deadlock between the UI process and the worker) does NOT print it: it prints
# "worker did not connect within N ms" or "worker: pipe closed before summary".
# Grepping for the wrong string meant the check could not bite the regression it cited.
Check 'S12.10e no worker connect failure and no timeout (regression: pipe deadlock)' `
  ((-not ($payloadText -match 'timed out')) -and
   (-not ($payloadText -match 'worker did not connect')) -and
   (-not ($payloadText -match 'pipe closed before summary'))) `
  "connectFailed=$([bool]($payloadText -match 'worker did not connect')) pipeClosed=$([bool]($payloadText -match 'pipe closed before summary')) idleTimeout=$([bool]($payloadText -match 'timed out'))"
# P0-5: only THIS run's worker lines count. The file was deleted above, so it exists at
# all only because a worker started, and its timestamp has to be inside this run.
$workerBytes = 0; $workerFresh = $false; $workerText = ''
if (Test-Path -LiteralPath $workerLog) {
  $wi = Get-Item -LiteralPath $workerLog
  $workerBytes = $wi.Length
  $workerFresh = $wi.LastWriteTime -ge $payloadStarted
  $workerText = (Get-Content -LiteralPath $workerLog -Encoding UTF8) -join "`n"
}
# 2026-10-05: this pattern used to pin "protocolVersion=1". That pin is incidental to what
# the check is for (only THIS run's worker may satisfy it -- established by deleting the
# file above plus $workerFresh), so every legitimate protocol bump turned it into a false
# negative instead of a caught regression: it went red the moment the protocol became 2 and
# stayed red. Match the readiness line, not the number. Pinning the protocol version, if
# ever wanted, needs its own check whose stated purpose is exactly that.
Check 'S12.10f worker reported ready (this run only: the log was deleted first)' `
  ($workerFresh -and ($workerText -match 'worker: ready protocolVersion=\d+')) `
  "beforeRun=${staleWorkerBytes}B now=${workerBytes}B fresh=$workerFresh"
Check 'S12.10g no leftover worker process' (@(Get-Process -Name Confern -ErrorAction SilentlyContinue).Count -eq 0) ''

# the most natural user action: select a format, then RELEASE the mouse. It needs a real
# gesture, so this asserts the wiring through the very same Released event (spec 4.3).
Check 'S12.10h mouse release commits and converts' ($payloadText -match 'release commit finished: finished=True ok=1 failed=0') ''
$releaseLine = ($payloadText -split "`n" | Where-Object { $_ -match 'release-path: selected index=' } | Select-Object -First 1)
if ($releaseLine) {
  $target = ([regex]::Match($releaseLine, 'target=([A-Z]+)')).Groups[1].Value
  $expectedExt = '.' + $target.ToLower()
  # WHERE the artifact is looked for is owned by the settings file THIS suite wrote
  # ($payloadDest), not by a guess. At HEAD this line hard-coded the source folder, so
  # the check was really asking "is the operator's OutputFolder empty?" rather than
  # "did the released drop produce its own file". And the product's OWN report for the
  # same run ("wheel destination: outputDir=...", spec 9.5) must agree with it: a run
  # that ignores the settings file, or that writes somewhere else, fails here instead
  # of quietly passing.
  $destLine = ($payloadText -split "`n" | Where-Object { $_ -match 'wheel destination: outputDir=' } | Select-Object -Last 1)
  $reportedDir = ''
  if ($destLine) {
    $dm = [regex]::Match($destLine, 'outputDir=(.*?) sourceFolder=')
    if ($dm.Success) { $reportedDir = $dm.Groups[1].Value }
  }
  if ($reportedDir -eq '(source folder)') { $reportedDir = Split-Path -Parent $srcJpg }
  $destAgrees = Test-DSameFolder $reportedDir $payloadDest
  # A file THIS run wrote: with the "desktop" state the folder is shared with the user,
  # and a leftover from an earlier run must not be able to satisfy the check.
  $found = @(Get-ChildItem -LiteralPath $payloadDest -File -Filter "*$expectedExt" -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -like 'art*' -and ($_.LastWriteTime -ge $payloadStarted) })
  Check 'S12.10i the released drop produced its own file' `
    (($found.Count -ge 1) -and $destAgrees) `
    "target=$target folder='$payloadDest' reported='$reportedDir' agrees=$destAgrees files=$($found.Count)"
} else {
  Inconclusive 'S12.10i the released drop produced its own file' 'no release-path line'
}
# Desktop hygiene: with -PayloadOutputFolder desktop the payload run above really wrote
# onto the operator's desktop, so the art* files that were NOT there before it go away
# again (counted, never assumed). Reported, not asserted: this suite's check count is
# fixed by $ExpectedChecks.
$deskStray = @(Get-ChildItem -LiteralPath $payloadDesk -Filter 'art*' -File -ErrorAction SilentlyContinue |
               Where-Object { $payloadDeskBefore -notcontains $_.Name })
foreach ($df in $deskStray) { Remove-Item -LiteralPath $df.FullName -Force -ErrorAction SilentlyContinue }
"[cleanup] desktop: this run added $($deskStray.Count) art* file(s) ($($deskStray.Name -join ', ')) and removed " +
"them; art* files left on the desktop = $(@(Get-ChildItem -LiteralPath $payloadDesk -Filter 'art*' -File -ErrorAction SilentlyContinue).Count)"

# The 2026-10-01 defect lived HERE, not in the headless path: the pill renders
# ConversionSnapshot.Line, and that snapshot carried its own copy of the rule.
# The failure contract must therefore be asserted through the payload/UI path as
# well, or the same drift can return with every check still green.
$failPayloadLog = Join-Path $logDir 'payload-failure.log'
# P0-1 (2026-10-02): S12.15c used to count `corrupt.*` files in the work directory --
# and that "1" is the fixture itself. Nothing read the file and nothing compared a hash,
# so a failure path that EMPTIED or rewrote the user's source file still passed, while
# spec 5's first red line says the source is never touched. Record the identity of the
# source before the run and assert it afterwards, together with the reason (so "no
# output was written" cannot pass because the conversion never ran at all).
$corruptHash = (Get-FileHash -LiteralPath $corrupt -Algorithm SHA256).Hash
$corruptStamp = (Get-Item -LiteralPath $corrupt).LastWriteTimeUtc.ToString('o')
$corruptLen = (Get-Item -LiteralPath $corrupt).Length
# Same settings file as the S12.10 payload run (P1-11): this run writes no output (the
# fixture cannot be decoded), but it must not be the one place left that decides a
# destination from the operator's own settings either.
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings-file', $cfgPayload, '--wheel-payload', $corrupt, '--no-autostart', '--quiet', '--log', $failPayloadLog) -PassThru -Wait -WindowStyle Hidden
$failText = (Get-Content -LiteralPath $failPayloadLog -Encoding UTF8) -join "`n"
$failPill = Get-PillLine $failText
Check 'S12.15a the pill shows the reason, not a bare count' ($failPill.Contains($MSG_DECODE)) "pill='$failPill'"
Check 'S12.15b the finished line carries the reason' ($failText -match "finished=True ok=0 failed=1 error=- line='.*$MSG_DECODE") ''
$sourceSame = (Get-FileHash -LiteralPath $corrupt -Algorithm SHA256).Hash -eq $corruptHash
$sourceStampSame = (Get-Item -LiteralPath $corrupt).LastWriteTimeUtc.ToString('o') -eq $corruptStamp
$sourceLenSame = (Get-Item -LiteralPath $corrupt).Length -eq $corruptLen
$failReasonOk = ($failText -match 'worker result: ok=False output=- error=DecodeFailed') -and
                ($failText -match "conversion finished: finished=True ok=0 failed=1 error=- line='.*$MSG_DECODE")
# NOTE: Test-SummaryCarries is the wrong helper on a --wheel-payload log -- it reads the
# `convert summary:` line, which only the --convert path writes (measured: it returned False on a
# log that plainly carried the reason). The first version of this check used it and went red for
# a reason that had nothing to do with the property under test.
Check 'S12.15c a failed conversion leaves the source byte-identical and writes nothing' `
  ($sourceSame -and $sourceStampSame -and $sourceLenSame -and $failReasonOk -and
   (@(Get-ChildItem -LiteralPath $Work -Filter 'corrupt.*' -File).Count -eq 1) -and
   (@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File).Count -eq 0)) `
  "sourceShaSame=$sourceSame sourceStampSame=$sourceStampSame sourceLenSame=$sourceLenSame reason=$failReasonOk corruptFiles=$(@(Get-ChildItem -LiteralPath $Work -Filter 'corrupt.*' -File).Count) tmpFiles=$(@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File).Count)"
# 2026-10-05: after the user's real-hand test the failure hold is the same 2 s as a success.
# 5 s was never in the spec -- spec 4.4 only says "2 s after finishing" -- so this is a return
# to the spec, not a spec change. The old expectation was measured red BEFORE being updated
# (FAIL S12.15d, checks=54 pass=53 fail=1, exit=1), so this check pins a real number.
# ASCII-only comment on purpose: see the note above the hideLines block. A CJK comment here
# silently ate the newline and the Check below never ran (the registration count caught it).
$failHide = [regex]::Match($failText, 'progress: hiding after (\d+) ms \(updates=\d+, ticks=\d+, hold=2000 ms\)')
$failElapsed = if ($failHide.Success) { [int]$failHide.Groups[1].Value } else { -1 }
Check 'S12.15d a failure fades on the same 2 s budget as a success, and really took that long' `
  ($failHide.Success -and ($failElapsed -ge 2000) -and ($failElapsed -le 3200)) `
  "elapsed=$failElapsed ms expected 2000..3200 (hold=2000 + fade); the audit called the old form WEAK because it never compared this number"

"=== S12.11-S12.12 settings (spec 8) ==="
$badSettings = Join-Path $Work 'settings-out-of-range.json'
Set-Content -LiteralPath $badSettings -Encoding UTF8 -Value '{ "JpgQuality": 40, "WebpQuality": 200, "CaptureTimeoutSeconds": 9999, "TriggerKey": "Space", "NamingCollision": "auto-suffix", "EmergencyHotkey": "Ctrl+Alt+Shift+Q", "Diagnostics": false }'
$sLog = Join-Path $logDir 'settings.log'
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings-probe', $badSettings, '--quiet', '--log', $sLog) -PassThru -Wait -WindowStyle Hidden
$sText = (Get-Content -LiteralPath $sLog -Encoding UTF8) -join "`n"
Check 'S12.11a jpgQuality clamped into 60-100' ($sText -match 'jpgQuality=60\b') ''
Check 'S12.11b webpQuality clamped into 60-100' ($sText -match 'webpQuality=100\b') ''
Check 'S12.11c capture timeout clamped into 5-120' ($sText -match 'capture=120\b') ''
Check 'S12.11d clamping is reported, not silent' ($sText -match 'clamped JpgQuality 40 -> 60') ''

$brokenSettings = Join-Path $Work 'settings-broken.json'
Set-Content -LiteralPath $brokenSettings -Encoding UTF8 -Value '{ this is not json'
$sLog2 = Join-Path $logDir 'settings-broken.log'
$p = Start-Process -FilePath $Exe -ArgumentList @('--settings-probe', $brokenSettings, '--quiet', '--log', $sLog2) -PassThru -Wait -WindowStyle Hidden
$sText2 = (Get-Content -LiteralPath $sLog2 -Encoding UTF8) -join "`n"
Check 'S12.12a broken settings file does not crash' ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
Check 'S12.12b broken settings file falls back to defaults' ($sText2 -match 'jpgQuality=85\b') ''

"=== S12.13 batch + collision chain ==="
$batchDir = Join-Path $Work 'batch'
New-Item -ItemType Directory -Force -Path $batchDir | Out-Null
$batchFiles = @()
for ($i = 1; $i -le 3; $i++) {
  $b = [System.Drawing.Bitmap]::new(40, 30)
  $p2 = Join-Path $batchDir "shot$i.jpg"
  $b.Save($p2, $jpgCodec, $ep); $b.Dispose()
  $batchFiles += $p2
}
$r = Run-Convert $batchFiles 'PNG' 'batch'
Check 'S12.13a batch reports 3 ok' ($r.Text -match 'convert summary: .*ok=3') ''
$firstBatch = @(Get-ChildItem -LiteralPath $batchDir -Filter '*.png' -File)
Check 'S12.13b all three outputs exist' ($firstBatch.Count -eq 3) ''
$firstHashes = @{}
foreach ($f in $firstBatch) { $firstHashes[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }

$r2 = Run-Convert $batchFiles 'PNG' 'batch-again'
$allNames = @(Get-ChildItem -LiteralPath $batchDir -Filter '*.png' -File | ForEach-Object { $_.Name })
# collision numbering is PER FILE (each source collides exactly once -> " (1)"), so assert
# the actual contract: both variants exist for every source, and nothing was overwritten.
$hasBoth = $true
foreach ($i in 1..3) {
  if (-not ($allNames -contains "shot$i.png")) { $hasBoth = $false }
  if (-not ($allNames -contains "shot$i (1).png")) { $hasBoth = $false }
}
Check 'S12.13c second batch suffixes (1) per file' (($allNames.Count -eq 6) -and $hasBoth) (($allNames | Sort-Object) -join ',')
$untouched = $true
foreach ($name in $firstHashes.Keys) {
  if ((Get-FileHash -LiteralPath (Join-Path $batchDir $name) -Algorithm SHA256).Hash -ne $firstHashes[$name]) { $untouched = $false }
}
Check 'S12.13e second batch did not overwrite the first' $untouched ''
Check 'S12.13d no .tmp-* after batch' (@(Get-ChildItem -LiteralPath $Work -Filter '*.tmp-*' -Recurse -File).Count -eq 0) ''

# =============================================================================
#  D slice (2026-10-06): the one-off measurements of 2026-10-05/06 turned into
#  assertions. APPEND ONLY -- nothing above this line was touched and no check
#  here re-judges an existing one.
#
#  Why each check can fail:
#    S12.16a-d  the output folder (spec 9.5): EMPTY (= the default writing) means
#                next to the source, "desktop" means the real shell desktop, a
#                custom path means that folder. The artifact is read from the
#                tool's OWN log line (output=<path>) -- never re-derived from the
#                naming rule here, or this would be a second owner of the path.
#    S12.17a-b  a configured folder that does NOT exist falls back next to the
#                source AND says so. Silent fallback is the thing spec 9.5 forbids.
#    S12.18a-d  strip-metadata: the fixture is PROVED to carry metadata first (raw
#                segments AND through the decoder), then the output carries none,
#                the decoded pixels are byte-identical, and the source is untouched.
#    S12.19     anti-drift: WorkerProtocol.Version in the source vs. the number the
#                worker of THIS run reported. On 2026-10-05 the protocol went 2 -> 3
#                and S12.10f stayed red because it pinned the old number; that check
#                was fixed to match the line, not the number. This one is the other
#                direction: a bump that is not really compiled in (or a stale exe)
#                fails here instead of silently pairing mismatched processes.
#    S12.20     with NO key held the wheel stays on the FORMAT layer even though the
#                tool catalog is not empty (tools=N, N >= 1), and the payload path
#                really dumps that frame. The sector list and the preview text of the
#                same path are already asserted by m1b's S7.16b/S7.17 -- deliberately
#                NOT repeated here.
#
#                CHANGED 2026-10-06 (M3 third sector, tool id "compress"): the tool
#                catalog went 2 -> 3, so the count is now pinned to the real number
#                instead of the weak lower bound this check used to carry.
#                  old: ($layerTools -ge 1)  -- passed for ANY non-empty catalog, so it
#                       could not notice a tool being lost or silently dropped either
#                  new: ($layerTools -eq 3)  -- ROTATE + CLEAN + COMPRESS. Adding a
#                       fourth tool must update this number on purpose; the failure
#                       message prints the value that was actually measured.
#                CHANGED 2026-10-06 (M3 fourth sector, tool id "crop"): the tool catalog
#                went 3 -> 4, so the pin moves with it.
#                  old: ($layerTools -eq 3)  -- ROTATE + CLEAN + COMPRESS
#                  new: ($layerTools -eq 4)  -- ROTATE + CLEAN + COMPRESS + CROP
#                       (the fourth sector is the last one: ToolCatalog.Tools keeps
#                       the drawing order, and crop is appended). The failure message
#                       and the check's own name say "4 tools" now, because a stale
#                       name is how a green run starts lying about what it measured.
#                A tool count of 0 still fails, exactly as before.
#
#                NOTE (why this pin can be trusted from a distance): the tool layer's
#                composition is also queryable WITHOUT the wheel and WITHOUT the
#                single-instance guard, through `--tools-list` / `--tools-list`, which
#                prints the same ToolCatalog.ToolsFor list with ids, labels and icons.
#                That path is asserted by tools\verify-m3d.ps1 S18.03, so a wrong
#                number here is cross-checked by a check that can run while the user's
#                own Confern is open (this suite cannot).
#                The semantics did NOT change: this is still "no key held => the wheel
#                stays on the format layer, and the tool catalog is not empty". A tool
#                count of 0 still fails, exactly as before.
#
#  Desktop hygiene: the "desktop" case writes ONE file onto the user's real desktop.
#  It is deleted and its absence is re-checked before the next check runs.
# =============================================================================
Expect 12

# The tool's own report: "... output=<path>" on the last matching line, or "".
function Get-DToolOutputPath([string]$text, [string]$marker) {
  $line = ($text -split "`n" | Where-Object { $_ -match $marker } | Select-Object -Last 1)
  if (-not $line) { return '' }
  $at = $line.IndexOf('output=')
  if ($at -lt 0) { return '' }
  $value = $line.Substring($at + 7).Trim()
  if ($value.Length -eq 0 -or $value -eq '-') { return '' }
  return $value
}

# Raw JPEG segment names, in file order, up to the start of the scan data.
function Get-DJpegSegments([string]$path) {
  $b = [System.IO.File]::ReadAllBytes($path)
  $names = New-Object System.Collections.ArrayList
  if ($b.Length -lt 2 -or $b[0] -ne 0xFF -or $b[1] -ne 0xD8) { return @('NOT-JPEG') }
  $i = 2
  while ($i + 3 -lt $b.Length) {
    if ($b[$i] -ne 0xFF) { [void]$names.Add("BAD@$i"); break }
    $m = $b[$i + 1]
    if ($m -eq 0xDA -or $m -eq 0xD9) {
      if ($m -eq 0xD9) { [void]$names.Add('EOI') } else { [void]$names.Add('SOS') }
      break
    }
    if ($m -eq 0xE0) { [void]$names.Add('APP0') }
    elseif ($m -ge 0xE1 -and $m -le 0xEF) { [void]$names.Add("APP$($m - 0xE0)") }
    elseif ($m -eq 0xFE) { [void]$names.Add('COM') }
    else { [void]$names.Add(('0x{0:X2}' -f $m)) }
    $len = ($b[$i + 2] -shl 8) + $b[$i + 3]
    if ($len -lt 2) { [void]$names.Add('BADLEN'); break }
    $i += 2 + $len
  }
  return @($names)
}

# The decoded pixels of a file, as raw bytes (32bpp ARGB). Two equal buffers mean
# every channel of every pixel is equal -- byte-identical, not "close enough".
function Get-DPixelBytes([string]$path) {
  $img = [System.Drawing.Bitmap]::FromFile($path)
  try {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $img.Width, $img.Height
    $data = $img.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $bytes = New-Object byte[] ($data.Stride * $data.Height)
      [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
      return $bytes
    } finally { $img.UnlockBits($data) }
  } finally { $img.Dispose() }
}

function Invoke-DTool([string[]]$arguments) {
  $script:logCounter++
  $log = Join-Path $logDir "dslice-$($script:logCounter).log"
  $started = Get-Date
  $p = Start-Process -FilePath $Exe -ArgumentList ($arguments + @('--quiet', '--log', $log)) `
       -PassThru -Wait -WindowStyle Hidden
  return @{ Exit = $p.ExitCode; Log = $log; Start = $started;
            Text = (Get-Content -LiteralPath $log -Encoding UTF8) -join "`n" }
}

"=== S12.16-S12.20 D slice: the 2026-10-05/06 measurements, as assertions ==="

# ---- fixture: a real JPEG that really carries metadata ----
$metaW = 64; $metaH = 48
$metaBmp = [System.Drawing.Bitmap]::new($metaW, $metaH)
for ($y = 0; $y -lt $metaH; $y++) {
  for ($x = 0; $x -lt $metaW; $x++) {
    $metaBmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, ($x * 255 / $metaW),
                                                                  ($y * 255 / $metaH),
                                                                  (($x * 3 + $y * 5) % 256)))
  }
}
$metaSrc = Join-Path $Work 'm1d-meta.jpg'
$metaBmp.Save($metaSrc, $jpgCodec, $ep)
$metaBmp.Dispose()

# Inject what a camera writes: APP1/EXIF (with an Orientation tag) and a COM comment,
# right after SOI. Built byte by byte so "it has metadata" is a fact, not a hope.
$metaRaw = [System.IO.File]::ReadAllBytes($metaSrc)
$metaMs = New-Object System.IO.MemoryStream
$metaMs.WriteByte(0xFF); $metaMs.WriteByte(0xD8)
$exif = New-Object System.Collections.Generic.List[byte]
foreach ($c in [System.Text.Encoding]::ASCII.GetBytes('Exif')) { $exif.Add($c) }
$exif.Add(0); $exif.Add(0)
# TIFF little-endian, one IFD entry: Orientation (0x0112) type 3 count 1 value 1
foreach ($c in [byte[]](0x49,0x49,0x2A,0x00, 0x08,0x00,0x00,0x00, 0x01,0x00,
                        0x12,0x01,0x03,0x00,0x01,0x00,0x00,0x00,0x01,0x00,0x00,0x00,
                        0x00,0x00,0x00,0x00)) { $exif.Add($c) }
$app1Len = $exif.Count + 2
$metaMs.WriteByte(0xFF); $metaMs.WriteByte(0xE1)
$metaMs.WriteByte([byte](($app1Len -shr 8) -band 0xFF)); $metaMs.WriteByte([byte]($app1Len -band 0xFF))
$metaMs.Write($exif.ToArray(), 0, $exif.Count)
$comText = [System.Text.Encoding]::ASCII.GetBytes('made by the m1d verifier')
$comLen = $comText.Length + 2
$metaMs.WriteByte(0xFF); $metaMs.WriteByte(0xFE)
$metaMs.WriteByte([byte](($comLen -shr 8) -band 0xFF)); $metaMs.WriteByte([byte]($comLen -band 0xFF))
$metaMs.Write($comText, 0, $comText.Length)
$metaMs.Write($metaRaw, 2, $metaRaw.Length - 2)
[System.IO.File]::WriteAllBytes($metaSrc, $metaMs.ToArray())
$metaMs.Dispose()

$metaSegs = @(Get-DJpegSegments $metaSrc)
$metaBmpView = [System.Drawing.Bitmap]::FromFile($metaSrc)
$metaItems = @($metaBmpView.PropertyItems | ForEach-Object { '0x{0:X4}' -f $_.Id })
$metaBmpView.Dispose()
# (S12.18a is asserted further down, right before the strip run it is a precondition for.)

# ---- S12.16a output folder EMPTY: next to the source ----
$cfgSource = Join-Path $Work 'dslice-source.json'
Write-DSettings $cfgSource ''
$rotUnsetSrc = Join-Path $Work 'm1d-unset.jpg'
Copy-Item -LiteralPath $metaSrc -Destination $rotUnsetSrc -Force
$runUnset = Invoke-DTool @('--settings-file', $cfgSource, '--rotate90', $rotUnsetSrc)
$outUnset = Get-DToolOutputPath $runUnset.Text 'tool rotate90: ok='
Check 'S12.16a output folder unset (empty string) -> the file lands next to the source' `
  (($runUnset.Exit -eq 0) -and ($outUnset -ne '') -and (Test-DSameFolder ([System.IO.Path]::GetDirectoryName($outUnset)) $Work)) `
  "exit=$($runUnset.Exit) output='$outUnset'"

# ---- S12.16b/c output folder "desktop": the real shell desktop, then cleaned up ----
$dDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$deskStem = 'm1d-desk-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
$deskSrc = Join-Path $Work "$deskStem.jpg"
Copy-Item -LiteralPath $metaSrc -Destination $deskSrc -Force
$cfgDesktop = Join-Path $Work 'dslice-desktop.json'
Write-DSettings $cfgDesktop 'desktop'
$deskOut = ''
$deskExit = -1
$deskText = ''
try {
  $runDesk = Invoke-DTool @('--settings-file', $cfgDesktop, '--rotate90', $deskSrc)
  $deskExit = $runDesk.Exit
  $deskOut = Get-DToolOutputPath $runDesk.Text 'tool rotate90: ok='
} finally {
  # The artifact this suite created must not survive the suite, whatever happened.
  $deskBefore = @(Get-ChildItem -LiteralPath $dDesktop -Filter 'm1d-desk-*' -File -ErrorAction SilentlyContinue)
  foreach ($f in $deskBefore) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }
  $script:deskRemoved = $deskBefore.Count
  $script:deskLeft = @(Get-ChildItem -LiteralPath $dDesktop -Filter 'm1d-desk-*' -File -ErrorAction SilentlyContinue)
  "[cleanup] desktop: removed $($script:deskRemoved) artifact(s) this suite created " +
  "($($deskBefore.Name -join ',')), remaining m1d-desk-* files now = $($script:deskLeft.Count)"
}
Check 'S12.16b output folder "desktop" -> the file lands on the real shell desktop' `
  (($deskExit -eq 0) -and ($deskOut -ne '') -and (Test-DSameFolder ([System.IO.Path]::GetDirectoryName($deskOut)) $dDesktop)) `
  "exit=$deskExit desktop='$dDesktop' exists=$(Test-Path -LiteralPath $dDesktop) output='$deskOut'"
Check 'S12.16c that desktop artifact was deleted and is confirmed gone' `
  (($script:deskLeft.Count -eq 0) -and `
   (($deskOut -eq '') -or (-not (Test-DSameFolder ([System.IO.Path]::GetDirectoryName($deskOut)) $dDesktop)) -or `
    (-not (Test-Path -LiteralPath $deskOut)))) `
  "removed=$($script:deskRemoved) remaining=$($script:deskLeft.Count) reportedOutput='$deskOut' stillThere=$(if ($deskOut) { Test-Path -LiteralPath $deskOut } else { 'n/a' })"

# ---- S12.16d output folder = a custom (existing) folder ----
$customOut = Join-Path $Work 'dslice-custom-out'
New-Item -ItemType Directory -Force -Path $customOut | Out-Null
$cfgCustom = Join-Path $Work 'dslice-custom.json'
Write-DSettings $cfgCustom ($customOut -replace '\\', '\\')
$rotCustomSrc = Join-Path $Work 'm1d-custom.jpg'
Copy-Item -LiteralPath $metaSrc -Destination $rotCustomSrc -Force
$runCustom = Invoke-DTool @('--settings-file', $cfgCustom, '--rotate90', $rotCustomSrc)
$outCustom = Get-DToolOutputPath $runCustom.Text 'tool rotate90: ok='
Check 'S12.16d output folder = a custom path -> the file lands in that folder' `
  (($runCustom.Exit -eq 0) -and ($outCustom -ne '') -and (Test-DSameFolder ([System.IO.Path]::GetDirectoryName($outCustom)) $customOut)) `
  "exit=$($runCustom.Exit) wanted='$customOut' output='$outCustom'"

# ---- S12.17 a configured folder that does not exist: fall back, and SAY so ----
$goneOut = Join-Path $Work ('dslice-no-such-dir-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)))
$cfgGone = Join-Path $Work 'dslice-gone.json'
Write-DSettings $cfgGone ($goneOut -replace '\\', '\\')
$rotGoneSrc = Join-Path $Work 'm1d-gone.jpg'
Copy-Item -LiteralPath $metaSrc -Destination $rotGoneSrc -Force
$runGone = Invoke-DTool @('--settings-file', $cfgGone, '--rotate90', $rotGoneSrc)
$outGone = Get-DToolOutputPath $runGone.Text 'tool rotate90: ok='
Check 'S12.17a output folder that does not exist -> the file falls back next to the source' `
  (($runGone.Exit -eq 0) -and ($outGone -ne '') -and (Test-DSameFolder ([System.IO.Path]::GetDirectoryName($outGone)) $Work) -and (-not (Test-Path -LiteralPath $goneOut))) `
  "exit=$($runGone.Exit) wanted='$goneOut' output='$outGone'"
$fallbackLine = ($runGone.Text -split "`n" | Where-Object { $_ -match 'fell back to the source folder' } | Select-Object -Last 1)
Check 'S12.17b the fallback is reported, not silent (spec 9.5)' `
  ([bool]$fallbackLine -and $fallbackLine.Contains("output dir: '$goneOut' unavailable -> fell back to the source folder")) `
  "$($fallbackLine -replace '^\d\d:\d\d:\d\d\.\d\d\d ', '')"

# ---- S12.18b-d strip-metadata: metadata gone, pixels identical, source untouched ----
# S12.18a first: if this fixture did not really carry metadata, everything below
# would pass while proving nothing at all.
Check 'S12.18a the strip fixture really carries metadata (proved, not assumed)' `
  (($metaSegs -contains 'APP1') -and ($metaSegs -contains 'COM') -and ($metaItems -contains '0x0112')) `
  "segments=$($metaSegs -join ',') exifItems=$($metaItems -join ',')"
$metaHash = (Get-FileHash -LiteralPath $metaSrc -Algorithm SHA256).Hash
$metaStamp = (Get-Item -LiteralPath $metaSrc).LastWriteTimeUtc.ToString('o')
$metaLen = (Get-Item -LiteralPath $metaSrc).Length
$runStrip = Invoke-DTool @('--settings-file', $cfgSource, '--strip-metadata', $metaSrc)
$stripOut = Get-DToolOutputPath $runStrip.Text 'tool strip-metadata: ok='
$outSegs = @()
if ($stripOut -and (Test-Path -LiteralPath $stripOut)) { $outSegs = @(Get-DJpegSegments $stripOut) }
$containerPath = [bool]($runStrip.Text -match 'strip-metadata: .* container path \(jpeg\) removed ')
$removedLine = ($runStrip.Text -split "`n" | Where-Object { $_ -match 'container path \(jpeg\) removed' } | Select-Object -Last 1)
Check 'S12.18b strip-metadata: the metadata is really gone from the output (container path)' `
  (($runStrip.Exit -eq 0) -and ($stripOut -ne '') -and (Test-Path -LiteralPath $stripOut) -and $containerPath -and `
   (-not ($outSegs -contains 'APP1')) -and (-not ($outSegs -contains 'COM'))) `
  "exit=$($runStrip.Exit) output='$stripOut' segments=$($outSegs -join ',') $($removedLine -replace '^\d\d:\d\d:\d\d\.\d\d\d ', '')"

$pixelDiff = -1
$pixelBytes = 0
if ($stripOut -and (Test-Path -LiteralPath $stripOut)) {
  $pxBefore = Get-DPixelBytes $metaSrc
  $pxAfter = Get-DPixelBytes $stripOut
  $pixelBytes = $pxBefore.Length
  if ($pxBefore.Length -ne $pxAfter.Length) { $pixelDiff = -2 }
  else {
    $pixelDiff = 0
    for ($k = 0; $k -lt $pxBefore.Length; $k++) { if ($pxBefore[$k] -ne $pxAfter[$k]) { $pixelDiff++ } }
  }
}
Check 'S12.18c strip-metadata: every channel of every decoded pixel is identical' `
  (($pixelDiff -eq 0) -and ($pixelBytes -gt 0)) `
  "compared=$pixelBytes bytes (32bpp ARGB of ${metaW}x${metaH}) differing=$pixelDiff"
Check 'S12.18d strip-metadata: the source file is untouched (hash + mtime + length)' `
  (((Get-FileHash -LiteralPath $metaSrc -Algorithm SHA256).Hash -eq $metaHash) -and `
   ((Get-Item -LiteralPath $metaSrc).LastWriteTimeUtc.ToString('o') -eq $metaStamp) -and `
   ((Get-Item -LiteralPath $metaSrc).Length -eq $metaLen) -and `
   [bool]($runStrip.Text -match 'tool strip-metadata: source-untouched=True')) `
  "sha256 same=$((Get-FileHash -LiteralPath $metaSrc -Algorithm SHA256).Hash -eq $metaHash) mtime same=$((Get-Item -LiteralPath $metaSrc).LastWriteTimeUtc.ToString('o') -eq $metaStamp) len=$metaLen"

# ---- S12.19 anti-drift: the protocol number in the source == the one the worker reported ----
$srcRoot = (Get-Item -LiteralPath $Exe).Directory
while ($srcRoot -ne $null -and -not (Test-Path -LiteralPath (Join-Path $srcRoot.FullName 'src\Sidecar\Convert\WorkerServer.cs'))) {
  $srcRoot = $srcRoot.Parent
}
if (-not $srcRoot) {
  Expect -1
  Inconclusive 'S12.19 WorkerProtocol.Version matches the running worker' `
    'src\Sidecar\Convert\WorkerServer.cs not reachable from the exe path'
} else {
  $wsText = [System.IO.File]::ReadAllText((Join-Path $srcRoot.FullName 'src\Sidecar\Convert\WorkerServer.cs'))
  $verMatch = [regex]::Match($wsText, 'public\s+const\s+int\s+Version\s*=\s*(\d+)')
  $srcVersion = if ($verMatch.Success) { [int]$verMatch.Groups[1].Value } else { -1 }
  $workerLog = Join-Path $env:LOCALAPPDATA 'Confern\worker.log'
  if (Test-Path -LiteralPath $workerLog) { Remove-Item -LiteralPath $workerLog -Force -ErrorAction SilentlyContinue }
  $verRun = Invoke-DTool @('--settings-file', $cfgSource, '--wheel-payload', $metaSrc, '--no-autostart')
  $verWorker = ''
  $verFresh = $false
  if (Test-Path -LiteralPath $workerLog) {
    $wi = Get-Item -LiteralPath $workerLog
    $verFresh = $wi.LastWriteTime -ge $verRun.Start
    $verWorker = ((Get-Content -LiteralPath $workerLog -Encoding UTF8 | Where-Object { $_ -match 'worker: ready protocolVersion=' }) | Select-Object -Last 1)
  }
  $runVersion = -1
  $vm = [regex]::Match([string]$verWorker, 'protocolVersion=(\d+)')
  if ($vm.Success) { $runVersion = [int]$vm.Groups[1].Value }
  Check 'S12.19 WorkerProtocol.Version in the source == the version this run worker reported' `
    (($srcVersion -gt 0) -and ($srcVersion -eq $runVersion) -and $verFresh) `
    "source=$srcVersion worker=$runVersion freshLog=$verFresh (a bump that is not compiled in, or a stale exe, fails here)"
}

# ---- S12.20 no key held: the FORMAT layer is what a payload shows, and it dumps ----
$layerPng = Join-Path $Work 'm1d-layer.png'
$layerBmp = [System.Drawing.Bitmap]::new(96, 64)
$layerBmp.Save($layerPng, [System.Drawing.Imaging.ImageFormat]::Png)
$layerBmp.Dispose()
$layerDump = Join-Path $Work 'm1d-layer-frame.png'
if (Test-Path -LiteralPath $layerDump) { Remove-Item -LiteralPath $layerDump -Force }
$layerRun = Invoke-DTool @('--settings-file', $cfgSource, '--wheel-payload', $layerPng,
                           '--wheel-dump', $layerDump, '--no-autostart')
$payloadLine = ($layerRun.Text -split "`n" | Where-Object { $_ -match 'wheel from OLE payload:' } | Select-Object -Last 1)
$layerTools = -1
if ($payloadLine) {
  $tm = [regex]::Match($payloadLine, 'tools=(\d+)')
  if ($tm.Success) { $layerTools = [int]$tm.Groups[1].Value }
}
$dumpLen = if (Test-Path -LiteralPath $layerDump) { (Get-Item -LiteralPath $layerDump).Length } else { -1 }
Check 'S12.20 with no key held the payload wheel stays on the FORMAT layer (catalog=4 tools) and dumps that frame' `
  (($layerRun.Exit -eq 0) -and [bool]($payloadLine -match 'layer=format') -and ($layerTools -eq 4) -and ($dumpLen -gt 8000)) `
  "exit=$($layerRun.Exit) tools=$layerTools (expected 4: rotate90+strip-metadata+compress+crop; update this number only when a tool is really added/removed) dumpBytes=$dumpLen layerLine='$(($payloadLine -replace '^.*wheel from OLE payload: ', '') -replace ' source=.* targets=', ' targets=')'"

# =============================================================================
#  S12.21 CONCURRENCY (2026-10-06): two conversions running AT THE SAME TIME must
#  BOTH succeed. This is a regression guard for two real, measured defects.
#
#  Defect 1 -- two `--convert` runs converting the SAME source folder both picked
#  the same output name, because NamingPolicy.ResolveOutputPath only did
#  File.Exists ("check") and the real non-overwrite guarantee is File.Move(...,
#  overwrite:false) ("act"). The loser got ERROR_ALREADY_EXISTS (0x800700B7),
#  which was ALSO classified as Unreadable ("cannot read the SOURCE file") -- so
#  the operator was sent to inspect a source that had not changed by one byte.
#  Measured before the fix: job A exit=9 ok=0 failed=40, every line naming
#  0x800700B7; job B ok=40.
#
#  Defect 2 -- every worker was handed the SAME %LOCALAPPDATA%\Confern\worker.log
#  while Evidence opens it with append:false (= FileMode.Create, FileShare.Read,
#  i.e. exclusive). The second worker died in its Evidence constructor and never
#  connected to the pipe, so the UI side waited out the 8000 ms connect timeout.
#
#  Both checks below are AND-ed on purpose. "One of the two succeeded" is NOT a
#  pass: the whole point is that they do not destroy each other.
# =============================================================================
"=== S12.21 two conversions at the same time must BOTH succeed (2026-10-06) ==="
$concDir = Join-Path $Work 'concurrent-src'
$concOut = Join-Path $Work 'concurrent-logs'
New-Item -ItemType Directory -Force -Path $concDir, $concOut | Out-Null
$concCount = 40
for ($i = 0; $i -lt $concCount; $i++) {
  $cb = [System.Drawing.Bitmap]::new(64, 48)
  for ($y = 0; $y -lt 48; $y += 6) {
    for ($x = 0; $x -lt 64; $x += 6) {
      $cb.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, (($x + $i) % 256), ($y % 256), 90))
    }
  }
  $cb.Save((Join-Path $concDir ("c{0:D3}.jpg" -f $i)), $jpgCodec, $ep)
  $cb.Dispose()
}
$concList = (Get-ChildItem -LiteralPath $concDir -Filter '*.jpg' | ForEach-Object { $_.FullName }) -join ';'

# Both processes get their OWN --log (evidence is per run), and they write their
# outputs into the SAME source folder -- that is what makes the name race real.
$concLA = Join-Path $concOut 'a.log'
$concLB = Join-Path $concOut 'b.log'
$cA = Start-Process -FilePath $Exe -ArgumentList @('--convert', $concList, '--target', 'PNG',
        '--quiet', '--log', $concLA) -PassThru -WindowStyle Hidden
$cB = Start-Process -FilePath $Exe -ArgumentList @('--convert', $concList, '--target', 'PNG',
        '--quiet', '--log', $concLB) -PassThru -WindowStyle Hidden
$cA.WaitForExit()
$cB.WaitForExit()
$concTextA = if (Test-Path -LiteralPath $concLA) { (Get-Content -LiteralPath $concLA -Encoding UTF8) -join "`n" } else { '' }
$concTextB = if (Test-Path -LiteralPath $concLB) { (Get-Content -LiteralPath $concLB -Encoding UTF8) -join "`n" } else { '' }

# How many files each source produced. Two runs must leave TWO outputs per source:
# if they collided on one name, one of them was silently lost (or never written).
$concPerSource = @{}
foreach ($f in (Get-ChildItem -LiteralPath $concDir -Filter '*.png' -File)) {
  $stem = $f.Name -replace '\s*\(\d+\)\.png$', ''
  $stem = $stem -replace '\.png$', ''
  if ($concPerSource.ContainsKey($stem)) { $concPerSource[$stem] = $concPerSource[$stem] + 1 }
  else { $concPerSource[$stem] = 1 }
}
$concShort = @($concPerSource.Keys | Where-Object { $concPerSource[$_] -lt 2 }).Count
$concTotal = @(Get-ChildItem -LiteralPath $concDir -Filter '*.png' -File).Count
$concLeftover = @(Get-ChildItem -LiteralPath $concDir -Filter '*.tmp-*' -File -Recurse).Count

# Self-test: the counting really rejects a set with a lost output. Synthetic
# counts only, so this cannot silently agree with a broken product.
$syntheticGood = @{ 'c000' = 2; 'c001' = 2; 'c002' = 2 }
$syntheticLost = @{ 'c000' = 2; 'c001' = 1; 'c002' = 2 }
$rejectsLost = @($syntheticLost.Keys | Where-Object { $syntheticLost[$_] -lt 2 }).Count -gt 0
$acceptsGood = @($syntheticGood.Keys | Where-Object { $syntheticGood[$_] -lt 2 }).Count -eq 0
Check 'S12.21a self-test: the per-source count REJECTS a lost output' $rejectsLost 'synthetic 1 of 3 sources short'
Check 'S12.21b self-test: the per-source count ACCEPTS a complete set' $acceptsGood 'synthetic'

Check 'S12.21c both concurrent --convert runs exit 0 (neither is collateral damage)' `
  (($cA.ExitCode -eq 0) -and ($cB.ExitCode -eq 0)) `
  "exitA=$($cA.ExitCode) exitB=$($cB.ExitCode) (before the fix one of these was 9 with ok=0 failed=$concCount, IOException 0x800700B7)"
Check 'S12.21d each concurrent run reported a full success (no name-race failure)' `
  (($concTextA -match 'convert summary: ') -and ($concTextB -match 'convert summary: ') -and `
   ($concTextA -match [regex]::Escape("$CHECKMARK $concCount  (ok=$concCount failed=0)")) -and `
   ($concTextB -match [regex]::Escape("$CHECKMARK $concCount  (ok=$concCount failed=0)"))) `
  "A='$(Get-SummaryLine $concTextA)' B='$(Get-SummaryLine $concTextB)'"
Check 'S12.21e no source lost an output (2 files each, no overwrite, no .tmp-* left)' `
  (($concTotal -eq ($concCount * 2)) -and ($concShort -eq 0) -and ($concLeftover -eq 0)) `
  "png=$concTotal expected=$($concCount * 2) sourcesShort=$concShort tmpLeftovers=$concLeftover"

# ---- two WORKERS alive at the same time (spec 9.1: one worker per job) --------
# Spawned directly, one named pipe each, exactly as ConversionClient does it.
# The OLD code handed both the same worker.log; Evidence opened it exclusively,
# so the second one died before connecting (measured: exit with
# "Unhandled exception System.IO.IOException ... Evidence.cs:34").
$wkPipeA = 'm1d-conc-a-' + [Guid]::NewGuid().ToString('N')
$wkPipeB = 'm1d-conc-b-' + [Guid]::NewGuid().ToString('N')
$wkSrvA = New-Object System.IO.Pipes.NamedPipeServerStream($wkPipeA, [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
$wkSrvB = New-Object System.IO.Pipes.NamedPipeServerStream($wkPipeB, [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
$wkTaskA = $wkSrvA.WaitForConnectionAsync()
$wkTaskB = $wkSrvB.WaitForConnectionAsync()
$wkErrA = Join-Path $concOut 'worker-a.err'
$wkErrB = Join-Path $concOut 'worker-b.err'
$wkShared = Join-Path $env:LOCALAPPDATA 'Confern\worker.log'
$wkStarted = Get-Date
# No --log: the worker owns its evidence path (this is what the client relies on).
$wkA = Start-Process -FilePath $Exe -ArgumentList @('--worker', $wkPipeA, '--quiet') -PassThru -RedirectStandardError $wkErrA -WindowStyle Hidden
$wkB = Start-Process -FilePath $Exe -ArgumentList @('--worker', $wkPipeB, '--quiet') -PassThru -RedirectStandardError $wkErrB -WindowStyle Hidden
$wkOkA = $wkTaskA.Wait(8000)
$wkOkB = $wkTaskB.Wait(8000)
$wkErrTextA = if ((Test-Path $wkErrA) -and (Get-Item $wkErrA).Length -gt 0) { (Get-Content -LiteralPath $wkErrA -Encoding UTF8) -join "`n" } else { '' }
$wkErrTextB = if ((Test-Path $wkErrB) -and (Get-Item $wkErrB).Length -gt 0) { (Get-Content -LiteralPath $wkErrB -Encoding UTF8) -join "`n" } else { '' }
try { $wkSrvA.Dispose() } catch { }
try { $wkSrvB.Dispose() } catch { }
foreach ($wp in @($wkA, $wkB)) {
  try { $wp.WaitForExit(3000) | Out-Null } catch { }
  if (-not $wp.HasExited) { try { $wp.Kill() } catch { } }
}
$wkSharedText = if (Test-Path -LiteralPath $wkShared) { (Get-Content -LiteralPath $wkShared -Encoding UTF8) -join "`n" } else { '' }
$wkSharedFresh = $false
if (Test-Path -LiteralPath $wkShared) { $wkSharedFresh = (Get-Item -LiteralPath $wkShared).LastWriteTime -ge $wkStarted }
$wkPerJob = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Confern') -Filter 'worker-*.log' -File -ErrorAction SilentlyContinue |
              Where-Object { $_.LastWriteTime -ge $wkStarted })

Check 'S12.21f two workers at the same time BOTH connect (one evidence file each)' `
  ($wkOkA -and $wkOkB -and ($wkErrTextA -notmatch 'Unhandled exception') -and ($wkErrTextB -notmatch 'Unhandled exception')) `
  "connectedA=$wkOkA connectedB=$wkOkB perJobLogsThisRun=$($wkPerJob.Count) (before the fix the second worker died in its Evidence constructor)"
Check 'S12.21g both workers wrote their OWN evidence file (no shared exclusive path)' `
  ($wkPerJob.Count -ge 2) "worker-*.log written during this check = $($wkPerJob.Count)"
Check 'S12.21h the shared worker.log is THIS run and readable (the existing readers keep working)' `
  (($wkSharedText -match 'worker: ready protocolVersion=') -and $wkSharedFresh) `
  "freshLog=$wkSharedFresh hasReadyLine=$([bool]($wkSharedText -match 'worker: ready protocolVersion='))"

"=== result ==="
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive) expected=$($script:ExpectedChecks)"
if ($script:Checks -eq 0) { "NO CHECKS RAN"; exit 2 }
# P1-2: a suite that ran fewer checks than it promises has a block that silently failed
# to register. That is a red run, not an N/N print.
if ($script:Checks -ne $script:ExpectedChecks) {
  "FATAL: registered $($script:Checks) checks but this suite must register exactly $($script:ExpectedChecks)."
  "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
if ($script:Fail -gt 0) { exit 1 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
