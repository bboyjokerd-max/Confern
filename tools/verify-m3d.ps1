# M3d verification: the crop tool (spec 9.4, the FOURTH tool sector).
#
#   -Exe <path to Confern.exe>   -Section S18   -LogDir <dir>
#   [-FaultInjection swap|ignore-size|ignore-ratio]
#
# WHAT THIS PROVES, AND WHAT IT DOES NOT
#   1. The crop op really travels to the WORKER process (spec 9.1). Five new protocol fields
#      (cropX/cropY/cropW/cropH/cropRatio) are the whole point of this tool, and a value that
#      is correct in-process can still arrive as null/0 on the other side. Every check below
#      therefore goes through --crop (worker process + pipe), never through a direct engine call.
#   2. One normalized rectangle, MANY images: each file is converted with ITS OWN pixel size.
#      The user ruled on 2026-10-06 that a batch means "the same ratio applied to every image"
#      (e.g. "all of them become a central 1:1, whatever the original size"), so the interesting
#      assertion is not "512x384 becomes 384x384" but "512x384 AND 500x500 both come out square,
#      with different pixel counts".
#   3. The measured numbers come from the product's own decoder: this suite reads the output
#      file back and measures it. It never re-derives the size from the request.
#   4. Degenerate input is refused or clamped, and the choice is stated: an empty rectangle is
#      refused (exit 3, no file written), a sub-pixel rectangle is clamped to 1x1 (a file IS
#      written), and a rectangle that equals the whole image is refused (a full-frame crop is a
#      provable no-op that would still cost a lossy re-encode).
#   5. The source file is read-only (spec 5): sha256 + length + mtime before and after every run.
#   6. The crop WINDOW is real and works without a mouse: --crop-ui-selftest drives the same
#      BeginDrag/ContinueDrag/EndDrag methods the mouse handlers call, presses the real buttons,
#      confirms, closes, cancels and reopens; --crop-ui-dump renders it offscreen (not a screen
#      capture). Neither ever injects synthetic input (2026-10-06 rule: the user is using this
#      machine).
#   7. FAULT INJECTION (-FaultInjection): the same checks with a deliberately wrong expected
#      value, so a green run can be trusted to mean something. See the docstring of that switch.
#
# NOT covered here (stated so a green run is not read as more than it is):
#   - the drag HAND FEEL is not verifiable by any script. What is verified is that the three
#     methods the mouse handlers call produce the rectangle the state line reports. Cursor
#     shape, grabbing a 1px handle with a real mouse, and "does it feel right" are human calls.
#   - the crop window's LOOK is asserted structurally (a rendered frame that is not blank, no
#     scrollbar with a non-zero extent), never against a stored reference image.
#   - JPEG output is lossy: a JPEG source is re-encoded at quality 100, so the PIXEL comparison
#     below only runs on a PNG source (where the round trip is lossless and an exact match is
#     a meaningful assertion).
#   - --wheel-payload --wheel-tools --wheel-tool crop (the wheel's own commit path) is NOT run
#     here: it needs the resident path, and therefore the single-instance guard. --tools-list
#     covers the same catalog query without it.
#
# ASCII ONLY. This script must stay parseable by Windows PowerShell 5.1, whose BOM-less
# CJK handling corrupts non-ASCII literals.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'S18',
  [string]$LogDir = "$env:TEMP\sidecar-verify",
  [ValidateSet('', 'swap', 'ignore-size', 'ignore-ratio')][string]$FaultInjection = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

$Work = Join-Path $LogDir 'm3d'
$results = New-Object System.Collections.ArrayList
$inconclusive = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  [void]$results.Add([pscustomobject]@{ Name = $name; Pass = $ok })
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" }
  if ($detail -ne '') { Write-Host "      $detail" }
}
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason); Write-Host "INCONCLUSIVE  $reason"
}
$script:ExpectedChecks = 0
function Expect([int]$n) { $script:ExpectedChecks += $n }
function Get-LogLines([string]$p) {
  if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() }
}
function Test-Log([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -Quiet) }
function Get-LogLine([string[]]$l, [string]$p) {
  $hit = @($l | Select-String -Pattern $p)
  if ($hit.Count -eq 0) { return '' }
  return [string]$hit[$hit.Count - 1].Line
}
function Get-Sha([string]$p) {
  if (-not (Test-Path -LiteralPath $p)) { return 'MISSING' }
  return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
}

# One headless Confern run. NOTE: this suite never calls Stop-AllConfern and never runs a
# diagnostic that needs the single-instance mutex: the user may be using their own copy of
# the app while this runs, and killing it (or being refused by the guard) would be worse than
# not verifying. --crop / --crop-ui-* / --tools-list are all dispatched BEFORE the guard.
function Invoke-Confern([string]$name, [string[]]$argv) {
  $log = Join-Path $Work "$name.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $all = @() + $argv + @('--log', $log, '--quiet', '--no-autostart')
  $p = Start-Process -FilePath $Exe -ArgumentList $all -Wait -PassThru -WindowStyle Hidden
  return [pscustomobject]@{ Name = $name; Exit = $p.ExitCode; Log = $log; Lines = (Get-LogLines $log) }
}
function Invoke-ConfernStdout([string]$name, [string[]]$argv) {
  $out = Join-Path $Work "$name.stdout.txt"
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
  $p = Start-Process -FilePath $Exe -ArgumentList $argv -Wait -PassThru -WindowStyle Hidden `
                     -RedirectStandardOutput $out
  return [pscustomobject]@{ Name = $name; Exit = $p.ExitCode; Lines = (Get-LogLines $out) }
}

# A gradient fixture: R rises with x, G rises with y. That makes "was the RIGHT region taken"
# answerable by sampling one pixel - a flat image would pass any rectangle.
function New-FixturePng([string]$path, [int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
      $r = [int](255 * $x / [math]::Max(1, ($w - 1)))
      $g = [int](255 * $y / [math]::Max(1, ($h - 1)))
      $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $r, $g, 128))
    }
  }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}
function Get-ImageSize([string]$path) {
  try {
    $bmp = New-Object System.Drawing.Bitmap($path)
    $s = "$($bmp.Width)x$($bmp.Height)"
    $bmp.Dispose()
    return $s
  } catch { return 'MISSING' }
}
function Get-ImageSizeMatch([string]$size, [int]$w, [int]$h) { return ($size -eq "$($w)x$($h)") }
function Get-Pixel([string]$path, [int]$x, [int]$y) {
  $bmp = New-Object System.Drawing.Bitmap($path)
  $c = $bmp.GetPixel($x, $y)
  $s = "$($c.R),$($c.G),$($c.B)"
  $bmp.Dispose()
  return $s
}
function Get-DistinctColours([string]$path) {
  $bmp = New-Object System.Drawing.Bitmap($path)
  $seen = New-Object 'System.Collections.Generic.HashSet[int]'
  for ($y = 0; $y -lt $bmp.Height; $y += 3) {
    for ($x = 0; $x -lt $bmp.Width; $x += 3) {
      $c = $bmp.GetPixel($x, $y)
      [void]$seen.Add(($c.R * 65536) + ($c.G * 256) + $c.B)
      if ($seen.Count -ge 4096) { break }
    }
    if ($seen.Count -ge 4096) { break }
  }
  $n = $seen.Count
  $bmp.Dispose()
  return $n
}

# The output of one crop run: the product never overwrites, so it lands next to the source with
# a " (n)" suffix. Picking the NEWEST file (not a name this script composed) keeps the naming
# rule in exactly one place: NamingPolicy.
function Get-NewestOutput([string]$dir, [string]$source) {
  $stem = [System.IO.Path]::GetFileNameWithoutExtension($source)
  $c = @(Get-ChildItem -LiteralPath $dir -File |
         Where-Object { ($_.FullName -ne $source) -and ($_.Name -like "$stem*") } |
         Sort-Object LastWriteTimeUtc -Descending)
  if ($c.Count -eq 0) { return $null }
  return $c[0].FullName
}

# One crop run on a fresh copy of the fixture. Returns everything the checks need.
function Invoke-Crop([string]$name, [string]$fixture, [string]$rect, [string]$ratio, [string[]]$extra = @()) {
  $d = Join-Path $Work "run-$name"
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  $src = Join-Path $d (Split-Path -Leaf $fixture)
  Copy-Item -LiteralPath $fixture -Destination $src -Force
  $sha = Get-Sha $src
  $len = (Get-Item -LiteralPath $src).Length
  $mtime = (Get-Item -LiteralPath $src).LastWriteTimeUtc

  $argv = @('--crop', $src, '--crop-rect', $rect, '--crop-ratio', $ratio) + $extra
  $r = Invoke-Confern $name $argv

  $outFile = Get-NewestOutput $d $src
  $outSize = 'MISSING'
  if ($null -ne $outFile) { $outSize = Get-ImageSize $outFile }
  $untouched = ((Get-Sha $src) -eq $sha) -and
               ((Get-Item -LiteralPath $src).Length -eq $len) -and
               ((Get-Item -LiteralPath $src).LastWriteTimeUtc -eq $mtime)
  return [pscustomobject]@{
    Name = $name; Exit = $r.Exit; Lines = $r.Lines; Src = $src; Out = $outFile
    OutSize = $outSize; Untouched = $untouched
    OutputLine = (Get-LogLine $r.Lines 'crop output: ')
    JobLine = (Get-LogLine $r.Lines 'worker: job \d+ sent .*op=crop')
    SnapLine = (Get-LogLine $r.Lines 'crop snapshot:')
  }
}

# ---------------------------------------------------------------- fault injection
# -FaultInjection <name> runs the SAME checks but with one deliberately wrong expectation, so
# the raw output shows exactly which check catches which bug. It is the answer to "how do I
# know these checks can fail at all?" - a suite that only ever goes green proves nothing.
#
#   swap         expect the rectangle transposed (W and H swapped on the way to the engine).
#                Catches "the two fields crossed over" in the new protocol fields.
#   ignore-size  expect the FIRST image's pixel size to be used for every file in the batch.
#                Catches "the batch was converted as if every image were the same size",
#                which is the single most likely way to get the user's stated rule wrong.
#   ignore-ratio expect the free rectangle for a 1:1 request. Catches "cropRatio was lost on
#                the way over the pipe" (the field is absent and the engine falls back to free).
function Get-FaultNote() {
  switch ($FaultInjection) {
    'swap' { return 'FAULT INJECTION swap: expectations transposed on purpose' }
    'ignore-size' { return 'FAULT INJECTION ignore-size: expectations use the first image size for all' }
    'ignore-ratio' { return 'FAULT INJECTION ignore-ratio: 1:1 expectations replaced by the free rectangle' }
    default { return 'no fault injection: expectations are the documented ones' }
  }
}

# ---------------------------------------------------------------- S18
function Invoke-S18 {
  Write-Host "=== S18 crop tool: normalized rectangle over the worker pipe, batch = per-image size ==="
  Write-Host "=== $(Get-FaultNote)"

  # ---------------------------------------------------------------- fixture
  $fdir = Join-Path $Work 'f'
  New-Item -ItemType Directory -Force -Path $fdir | Out-Null
  $f512 = Join-Path $fdir 'img-512x384.png'
  $f500 = Join-Path $fdir 'img-500x500.png'
  $f800 = Join-Path $fdir 'img-800x600.png'
  $f300 = Join-Path $fdir 'img-300x700.png'
  New-FixturePng $f512 512 384
  New-FixturePng $f500 500 500
  New-FixturePng $f800 800 600
  New-FixturePng $f300 300 700

  # ---------------------------------------------------------------- S18.01 build freshness
  $repo = Split-Path -Parent $PSScriptRoot
  $exeTime = (Get-Item -LiteralPath $Exe).LastWriteTimeUtc
  $sources = @()
  if (Test-Path -LiteralPath (Join-Path $repo 'src')) {
    $sources = @(Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj |
                 Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
  }
  $newest = $sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  $newestText = '(none found)'
  if ($null -ne $newest) { $newestText = "$($newest.Name) $($newest.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss'))Z" }
  Check 'S18.01 the exe under test is newer than every source file it should have been built from' `
    (($null -ne $newest) -and ($exeTime -gt $newest.LastWriteTimeUtc)) `
    "exe=$($exeTime.ToString('yyyy-MM-dd HH:mm:ss'))Z sources=$($sources.Count) newest=$newestText"

  # ---------------------------------------------------------------- S18.02 the diagnostics exist
  $help = Invoke-ConfernStdout 'help' @('--help')
  Check 'S18.02 the crop diagnostics are documented in --help (they are not secret switches)' `
    ((Test-Log $help.Lines '--crop <p1;p2>') -and (Test-Log $help.Lines '--crop-rect x,y,w,h') -and
     (Test-Log $help.Lines '--crop-ratio <id>') -and (Test-Log $help.Lines '--crop-ui-selftest') -and
     (Test-Log $help.Lines '--crop-ui-dump <png>') -and (Test-Log $help.Lines '--tools-list')) `
    "help lines mentioning the crop flags were found in $($help.Lines.Count) line(s) of output"

  # ---------------------------------------------------------------- S18.03 the fourth sector
  # The tool layer's composition, asked WITHOUT the wheel and WITHOUT the single-instance guard.
  # This is the same catalog the wheel draws (ToolCatalog.ToolsFor), so "four sectors, crop last"
  # is a fact that can be re-checked at any time - including while the user's own copy is running.
  $tl = Invoke-Confern 'tools-list' @('--tools-list')
  Check 'S18.03 the tool layer has exactly four sectors, in order, and crop is the fourth' `
    (($tl.Exit -eq 0) -and
     (Test-Log $tl.Lines 'tool list: count=4 tools=\[rotate90,strip-metadata,compress,crop\]') -and
     (Test-Log $tl.Lines 'tool list: 3 id=crop ')) `
    "exit=$($tl.Exit) line='$(Get-LogLine $tl.Lines 'tool list: count=')' crop='$(Get-LogLine $tl.Lines 'tool list: 3 ')'"

  # ---------------------------------------------------------------- S18.04 the op crosses the pipe
  $one = Invoke-Crop 'one' $f512 '0.125,0,0.75,1' '1:1'
  Check 'S18.04 the crop op and its five fields really travel to the WORKER process' `
    (($one.Exit -eq 0) -and (Test-Log $one.Lines 'worker: ready') -and
     ($one.JobLine -match 'op=crop') -and
     ($one.JobLine -match 'crop=rect=\(0\.125,0,0\.75,1\) ratio=1:1') -and
     ($one.SnapLine -match 'finished=True') -and ($one.SnapLine -match 'ok=1 failed=0')) `
    "job='$($one.JobLine)' snapshot='$($one.SnapLine)'"

  # ---------------------------------------------------------------- S18.05 the acceptance case
  # 512x384 with a central 3/4-width, full-height box, asked for 1:1.
  # left=round(0.125*512)=64, right=round(0.875*512)=448, top=0, bottom=384 -> 384x384 box,
  # the biggest centred square inside it is the box itself. Expected: 384x384.
  $expectOne = '384x384'
  if ($FaultInjection -eq 'swap') { $expectOne = '384x288' }
  if ($FaultInjection -eq 'ignore-ratio') { $expectOne = '384x384' }
  Check 'S18.05 1:1 on 512x384 with the central box gives 384x384 (measured, not computed)' `
    ($one.OutSize -eq $expectOne) `
    "measured=$($one.OutSize) expected=$expectOne measured='$($one.OutputLine)'"

  # ---------------------------------------------------------------- S18.06 its own size
  # The SAME normalized rectangle and the SAME ratio on a DIFFERENT image. Edge-based half-up
  # rounding: left=round(62.5)=63, right=round(437.5)=438 -> 375 wide, 500 tall -> 375x375.
  # A batch that used the first image's size for everything would produce 384x384 here.
  $two = Invoke-Crop 'two' $f500 '0.125,0,0.75,1' '1:1'
  $expectTwo = '375x375'
  if ($FaultInjection -eq 'ignore-size') { $expectTwo = '384x384' }
  Check 'S18.06 the same normalized rectangle on a 500x500 image uses ITS OWN size (375x375, not 384x384)' `
    ($two.OutSize -eq $expectTwo) `
    "measured=$($two.OutSize) expected=$expectTwo measured='$($two.OutputLine)'"

  # ---------------------------------------------------------------- S18.07 the batch rule
  # One job, two images of different sizes, one rectangle: the user's stated rule is "the same
  # ratio for every image", and the thing that makes it a rule is that the two outputs differ.
  $bd = Join-Path $Work 'batch'
  New-Item -ItemType Directory -Force -Path $bd | Out-Null
  $b1 = Join-Path $bd 'a-512x384.png'
  $b2 = Join-Path $bd 'b-300x700.png'
  Copy-Item -LiteralPath $f512 -Destination $b1 -Force
  Copy-Item -LiteralPath $f300 -Destination $b2 -Force
  $bSha1 = Get-Sha $b1; $bSha2 = Get-Sha $b2
  $batchLog = Join-Path $Work 'batch.log'
  if (Test-Path -LiteralPath $batchLog) { Remove-Item -LiteralPath $batchLog -Force }
  $bp = Start-Process -FilePath $Exe -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
    '--crop', "$b1;$b2", '--crop-rect', '0.125,0,0.75,1', '--crop-ratio', '1:1',
    '--log', $batchLog, '--quiet', '--no-autostart')
  $bLines = Get-LogLines $batchLog
  $o1 = Get-NewestOutput $bd $b1
  $o2 = Get-NewestOutput $bd $b2
  $s1 = 'MISSING'; $s2 = 'MISSING'
  if ($null -ne $o1) { $s1 = Get-ImageSize $o1 }
  if ($null -ne $o2) { $s2 = Get-ImageSize $o2 }
  $bUntouched = ((Get-Sha $b1) -eq $bSha1) -and ((Get-Sha $b2) -eq $bSha2)
  # Both outputs are 1:1 (square) but NOT the same pixel count: 384x384 and 375x375.
  # 300x700 with the same box: left=round(37.5)=38, right=round(262.5)=263 -> 225 wide,
  # 700 tall -> the biggest centred square is 225x225.
  $expectB2 = '225x225'
  $expectB1 = '384x384'
  if ($FaultInjection -eq 'ignore-size') { $expectB2 = '384x384' }
  Check 'S18.07 one job, two different sizes: each output is square, each uses its own pixels (384x384 and 225x225)' `
    (($bp.ExitCode -eq 0) -and ($s1 -eq $expectB1) -and ($s2 -eq $expectB2) -and $bUntouched -and
     (Test-Log $bLines 'ok=2 failed=0')) `
    "exit=$($bp.ExitCode) a=$s1 b=$s2 (expected $expectB1 and $expectB2) sourcesUntouched=$bUntouched"

  # ---------------------------------------------------------------- S18.08 ratio presets
  # 800x600, box 0.05,0.05,0.9,0.9 -> left=40, right=760 (720 wide), top=30, bottom=570 (540 tall).
  #   free     -> 720x540 (untouched box)           ratio 1.3333
  #   1:1      -> 540x540 (height limited)          ratio 1.0000
  #   16:9     -> 720x405 (width limited)           ratio 1.7778
  #   4:3      -> 720x540 (exactly fits)            ratio 1.3333
  #   original -> 720x540 (this image's own 4:3)    ratio 1.3333
  # Tolerance: the constrained side is rounded to a whole pixel, so the ratio can be off by half
  # a pixel on that side; 540 px gives 0.5/540 = 0.09 %, and this check allows 1 %.
  $presets = @(
    @{ Id = 'free';     W = 720; H = 540 },
    @{ Id = '1:1';      W = 540; H = 540 },
    @{ Id = '16:9';     W = 720; H = 405 },
    @{ Id = '4:3';      W = 720; H = 540 },
    @{ Id = 'original'; W = 720; H = 540 }
  )
  $ratioLog = New-Object System.Collections.ArrayList
  $ratioOk = $true
  foreach ($p in $presets) {
    $r = Invoke-Crop ("ratio-" + ($p.Id -replace ':', '-')) $f800 '0.05,0.05,0.9,0.9' $p.Id
    $w = -1; $h = -1
    if ($r.OutSize -match '^(\d+)x(\d+)$') { $w = [int]$Matches[1]; $h = [int]$Matches[2] }
    $wantW = $p.W; $wantH = $p.H
    if ($FaultInjection -eq 'ignore-ratio' -and $p.Id -eq '1:1') { $wantW = 720; $wantH = 540 }
    $sizeOk = (($w -eq $wantW) -and ($h -eq $wantH))
    $targetRatio = 0.0
    switch ($p.Id) {
      '1:1' { $targetRatio = 1.0 }
      '16:9' { $targetRatio = 16.0 / 9.0 }
      '4:3' { $targetRatio = 4.0 / 3.0 }
      'original' { $targetRatio = 800.0 / 600.0 }
      default { $targetRatio = 0.0 }
    }
    $actualRatio = 0.0
    if ($h -gt 0) { $actualRatio = $w / $h }
    $ratioWithin = $true
    if ($targetRatio -gt 0) { $ratioWithin = ([math]::Abs($actualRatio - $targetRatio) / $targetRatio) -le 0.01 }
    [void]$ratioLog.Add("$($p.Id): $($r.OutSize) (want $($wantW)x$($wantH)) ratio=$([math]::Round($actualRatio,4)) target=$([math]::Round($targetRatio,4)) within1pct=$ratioWithin")
    if (-not ($sizeOk -and $ratioWithin -and $r.Untouched)) { $ratioOk = $false }
  }
  Check 'S18.08 every aspect preset reaches the engine and the output ratio is within 1 percent of the target' `
    $ratioOk `
    ($ratioLog -join ' | ')

  # ---------------------------------------------------------------- S18.09 whole image = refused
  # A rectangle equal to the whole image is REFUSED, not silently converted: the crop path is
  # decode -> process -> RE-ENCODE, so a full-frame crop is a provable no-op that would still
  # cost a lossy JPEG generation. Refusing it is the honest answer.
  $full = Invoke-Crop 'full-frame' $f512 '0,0,1,1' 'free'
  Check 'S18.09 a full-frame rectangle is refused (no file written, named reason) and the source is untouched' `
    (($full.Exit -eq 3) -and ($full.OutSize -eq 'MISSING') -and $full.Untouched -and
     (Test-Log $full.Lines 'CropNothingToDo')) `
    "exit=$($full.Exit) output=$($full.OutSize) measured='$($full.OutputLine)'"

  # ---------------------------------------------------------------- S18.10 sub-pixel = 1x1
  # A rectangle smaller than one pixel is CLAMPED to 1x1, not refused: the user's intent (crop
  # this tiny thing) is still expressible, and 1 px is the smallest non-empty crop. A 1x1 output
  # is the evidence that the clamp happened instead of a 0-width rectangle being written.
  $tiny = Invoke-Crop 'tiny' $f512 '0.5,0.5,0.0001,0.0001' 'free'
  # NOTE: the engine's own per-file "crop: <file>: <note>" lines are written by the WORKER
  # process into the worker's own log file, not into this run's --log. So this check asserts on
  # the EFFECT (a 1x1 file really exists) rather than on the note's text.
  Check 'S18.10 a sub-pixel rectangle is clamped to 1x1 (a file IS written) and the source is untouched' `
    (($tiny.Exit -eq 0) -and ($tiny.OutSize -eq '1x1') -and $tiny.Untouched) `
    "exit=$($tiny.Exit) output=$($tiny.OutSize) measured='$($tiny.OutputLine)'"

  # ---------------------------------------------------------------- S18.11 edges and corners
  $edge = Invoke-Crop 'edge' $f512 '0,0,0.5,1' 'free'
  $corner = Invoke-Crop 'corner' $f512 '0.5,0.5,0.5,0.5' 'free'
  Check 'S18.11 rectangles touching the edges and a corner are exact (0,0,0.5,1 -> 256x384; the corner -> 256x192)' `
    (($edge.OutSize -eq '256x384') -and ($corner.OutSize -eq '256x192') -and
     $edge.Untouched -and $corner.Untouched) `
    "edge=$($edge.OutSize) corner=$($corner.OutSize) edgeLine='$($edge.OutputLine)' cornerLine='$($corner.OutputLine)'"

  # ---------------------------------------------------------------- S18.12 right region?
  # Sizes alone cannot tell "the right region" from "a region of the right size". The fixture is
  # a gradient, so the first pixel of the output must equal the source pixel at (left, top).
  # PNG only: the round trip is lossless, so an exact match is a meaningful assertion.
  $regionOk = $false
  $regionDetail = 'no output to sample'
  if (($null -ne $one.Out) -and (Test-Path -LiteralPath $one.Out)) {
    $srcPix = Get-Pixel $one.Src 64 0
    $outPix = Get-Pixel $one.Out 0 0
    $regionOk = ($srcPix -eq $outPix)
    $regionDetail = "output(0,0)=$outPix source(64,0)=$srcPix (expected equal: the box starts at x=64)"
  }
  Check 'S18.12 the output really is the requested region of the source (first pixel matches the source at the box corner)' `
    $regionOk $regionDetail

  # ---------------------------------------------------------------- S18.13 sources are read-only
  $runs = @($one, $two, $tiny, $edge, $corner, $full)
  $allUntouched = $true
  $untouchedText = ''
  foreach ($r in $runs) {
    if (-not $r.Untouched) { $allUntouched = $false }
    $untouchedText += "$($r.Name)=$($r.Untouched) "
  }
  Check 'S18.13 every source file is byte-for-byte untouched (sha256 + length + mtime, spec 5)' `
    $allUntouched `
    "$($runs.Count) run(s) checked: $untouchedText"

  # ---------------------------------------------------------------- S18.14 bad input is refused
  $badRatio = Invoke-Crop 'bad-ratio' $f512 '0.1,0.1,0.5,0.5' '3:2'
  $badRect = Invoke-Crop 'bad-rect' $f512 '0.1,0.1,0.5' 'free'
  Check 'S18.14 an unknown ratio id and a malformed rectangle are refused WITHOUT sending anything to the worker' `
    (($badRatio.Exit -eq 2) -and ($badRect.Exit -eq 2) -and
     (Test-Log $badRatio.Lines 'nothing was sent to the worker') -and
     (Test-Log $badRect.Lines 'nothing was sent to the worker') -and
     (-not (Test-Log $badRatio.Lines 'worker: job')) -and
     (-not (Test-Log $badRect.Lines 'worker: job'))) `
    "ratioExit=$($badRatio.Exit) rectExit=$($badRect.Exit)"

  # ---------------------------------------------------------------- S18.15 the window, no mouse
  # Everything a user would do, driven through the real objects: the real window, the real
  # buttons (RaiseEvent -> Click), and the same BeginDrag/ContinueDrag/EndDrag the mouse
  # handlers call. No synthetic input is ever injected.
  $ui = Invoke-Confern 'ui-selftest' @('--crop-ui-selftest', '--crop-source', $f512)
  $uiOk = ($ui.Exit -eq 0)
  $uiKeys = @('opened=True', 'windowPresent=True', 'ignored (single instance)',
              'settingsOpen=True cropWindowStillPresent=True', 'the three mouse handlers) ok=True',
              'refused=True stillOpen=True', 'clampedTo1px=True', 'matchesWhatWasDrawn=True',
              'closed=True', 'reopened=True windowPresent=True')
  $missing = @()
  foreach ($k in $uiKeys) { if (-not (Test-Log $ui.Lines ([regex]::Escape($k)))) { $missing += $k } }
  Check 'S18.15 the crop window opens, drags, honours every preset, refuses the whole image, clamps a sub-pixel box, confirms, closes, cancels and reopens' `
    ($uiOk -and ($missing.Count -eq 0)) `
    "exit=$($ui.Exit) missing='" + ($missing -join ' | ') + "' summary='" + (Get-LogLine $ui.Lines 'selftest done:') + "'"

  # The ratio buttons: each one must be reachable AND must actually change the rectangle.
  $ratioStates = @($ui.Lines | Select-String -Pattern "ratio '(free|1:1|16:9|4:3|original)' ->")
  $ratioStatesOk = ($ratioStates.Count -eq 5)
  foreach ($s in $ratioStates) {
    if ([string]$s.Line -notmatch 'ratio-ok=True') { $ratioStatesOk = $false }
  }
  Check 'S18.15b all five preset buttons are reachable, and the ones with a target ratio report ratio-ok=True' `
    $ratioStatesOk `
    (($ratioStates | ForEach-Object { ([string]$_.Line).Trim() }) -join ' || ')

  # ---------------------------------------------------------------- S18.16 the dump
  $dump = Join-Path $Work 'crop-ui.png'
  if (Test-Path -LiteralPath $dump) { Remove-Item -LiteralPath $dump -Force }
  $dp = Invoke-Confern 'ui-dump' @('--crop-ui-dump', $dump, '--crop-source', $f512,
                                   '--crop-rect', '0.125,0,0.75,1')
  $dumpExists = Test-Path -LiteralPath $dump
  $dumpColours = 0
  $dumpSize = 'MISSING'
  if ($dumpExists) { $dumpColours = Get-DistinctColours $dump; $dumpSize = Get-ImageSize $dump }
  Check 'S18.16 the crop window renders offscreen to a PNG that is not blank (offscreen composite, not a screen capture)' `
    (($dp.Exit -eq 0) -and $dumpExists -and ($dumpColours -ge 32)) `
    "exit=$($dp.Exit) size=$dumpSize distinctColours=$dumpColours detail='$(Get-LogLine $dp.Lines 'crop ui: dump:')'"

  # A scrollbar would show up as a scrollable extent in the visual tree. The window is built to
  # fit; if it ever stops fitting, this check is what says so (a screenshot would not be readable
  # by a machine).
  $scroll = Get-LogLine $ui.Lines 'scroll='
  Check 'S18.16b the crop window has no scrollbar with a non-zero extent (nothing is cut off or hidden)' `
    (Test-Log $ui.Lines 'scroll=0') `
    "line='$scroll'"

  # ---------------------------------------------------------------- S18.17 what the window shows
  # The preview must be the frame the ENGINE will crop: same decoder, same EXIF orientation.
  Check 'S18.17 the window previews the same frame the engine crops (512x384, ratio list from CropRatios)' `
    ((Test-Log $ui.Lines 'image=512x384') -and (Test-Log $ui.Lines 'normalized=rect=') -and
     (Test-Log $ui.Lines 'stage=860x520')) `
    "state='$(Get-LogLine $ui.Lines 'crop ui selftest: opened=')'"
}

# ------------------------------------------------------------------ main
$sections = @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
Write-Host "verify-m3d: exe=$Exe sections=$($sections -join ',') logdir=$LogDir fault='$FaultInjection'"
if ($FaultInjection -ne '') { Write-Host "verify-m3d: $(Get-FaultNote)" }

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

foreach ($s in $sections) {
  switch ($s) {
    'S18' { Expect 19; Invoke-S18 }
    default { Write-Host "section not implemented yet: $s" }
  }
}

$fail = @($results | Where-Object { -not $_.Pass }).Count
$registered = $results.Count + $inconclusive.Count
if ($results.Count -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
Write-Host "checks=$registered pass=$($results.Count - $fail) fail=$fail inconclusive=$($inconclusive.Count) expected=$($script:ExpectedChecks)"
if ($registered -ne $script:ExpectedChecks) {
  Write-Host "FATAL: registered $registered checks but the selected sections promise $($script:ExpectedChecks)."
  Write-Host "       A check block silently did not register (P1-2). Refusing to report a result."
  exit 1
}
Write-Host ""
Write-Host "M3d summary: $($results.Count - $fail)/$($results.Count) checks passed ($Section) fault='$FaultInjection'"
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
