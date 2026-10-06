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
#   8. BIG IMAGES ARE PART OF THE SUITE (S18.18-S18.20). Every other fixture here is small, and a
#      512x384 fixture cannot answer "does it work on MY photo": while this file reported 19/19
#      green, the crop WINDOW selftest was red on any image bigger than its fixture, because that
#      assertion had the fixture's pixel size written into it (512x384). The user's own pictures
#      are 3000x2250 and up, so 3000x2250 is now checked end to end - size, region and the
#      full-frame refusal, both through the engine and through the window.
#   9. A ROTATED SOURCE (S18.21-S18.22). Orientation 6 (a phone held sideways) + crop: the
#      rectangle must be taken on the UPRIGHT picture, and the tag must be neutralised afterwards
#      or a viewer would rotate the result a second time.
#
# NOT covered here (stated so a green run is not read as more than it is):
#   - the drag HAND FEEL is not verifiable by any script. What is verified is that the three
#     methods the mouse handlers call produce the rectangle the state line reports. Cursor
#     shape, grabbing a 1px handle with a real mouse, and "does it feel right" are human calls.
#   - the window's screen->pixel mapping is exact only where round(W*scale) lands back on W*scale
#     (the stage is a fixed 860x520 canvas and the drawn size is rounded to whole pixels, while
#     the conversion divides by the unrounded scale). The residual is bounded by 0.5 display px,
#     i.e. ~1.5 image px on the 3000x2250 fixture and 0 px on a 3000x4000 one. No check here pins
#     that residual, and it is NOT what made the big case red.
#   - the crop window's LOOK is asserted structurally (a rendered frame that is not blank, no
#     scrollbar with a non-zero extent), never against a stored reference image.
#   - JPEG output is lossy: a JPEG source is re-encoded at quality 100, so the PIXEL comparison
#     below only runs on a PNG source (where the round trip is lossless and an exact match is
#     a meaningful assertion). The rotated-source checks therefore assert the nearest of four
#     quadrant colours, not an exact RGB value.
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
# PresentationCore / WindowsBase are here for the EXIF fixture and for reading the orientation tag
# back out of a cropped JPEG (BitmapFrame + BitmapMetadata). Same recipe as
# verify-orientation-matrix.ps1, which is what proves orientations 1-8 actually reach the pixels.
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
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

# A BIG fixture made by ONE native fill. LinearGradientBrush paints a 3000x2250 area in C++ code,
# so a big fixture costs the same as a small one. The per-pixel SetPixel loop of New-FixturePng
# would take about 20 s at that size, and a suite that gets slow stops being run.
# The fill runs at 45 degrees, so the colour depends on (x + y): a crop that took the wrong offset
# - in EITHER axis - lands on a different colour. The source is read back with the same reader as
# the output, so the exact ramp never has to be predicted here.
function New-BigGradientPng([string]$path, [int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $rect = New-Object System.Drawing.Rectangle -ArgumentList @(0, 0, $w, $h)
  $dark = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
  $light = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
             -ArgumentList @($rect, $dark, $light, [single]45.0)
  $g.FillRectangle($brush, 0, 0, $w, $h)
  $brush.Dispose(); $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

# Four flat quadrants: A=red B=green C=blue D=yellow, the vocabulary verify-orientation-matrix.ps1
# already uses. Cheap (four fills) and unambiguous: with the source and the expected region known,
# "a region of the right SIZE" and "the RIGHT region" stop looking alike.
# The EXIF recipe is the one that script proved for orientations 1-8: encode with WPF, then write
# tag 274 back with BitmapMetadata. No per-pixel loop, so it works at any size.
function New-QuadrantJpeg([string]$path, [int]$w, [int]$h, [int]$orientation) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hw = [int]($w / 2); $hh = [int]($h / 2)
  $a = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 255, 0, 0))
  $b = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 0, 255, 0))
  $c = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 0, 0, 255))
  $d = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 255, 255, 0))
  $g.FillRectangle($a, 0, 0, $hw, $hh)
  $g.FillRectangle($b, $hw, 0, $w - $hw, $hh)
  $g.FillRectangle($c, 0, $hh, $hw, $h - $hh)
  $g.FillRectangle($d, $hw, $hh, $w - $hw, $h - $hh)
  $g.Dispose()
  foreach ($brush in @($a, $b, $c, $d)) { $brush.Dispose() }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $bmp.Dispose()
  if ($orientation -le 1) { return }

  $s = [System.IO.File]::OpenRead($path)
  $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($s,
      [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
      [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
  $meta = New-Object System.Windows.Media.Imaging.BitmapMetadata -ArgumentList @('jpg')
  $meta.SetQuery('/app1/ifd/{ushort=274}', [uint16]$orientation)
  $enc = New-Object System.Windows.Media.Imaging.JpegBitmapEncoder
  $enc.QualityLevel = 100
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame, $null, $meta, $null))
  $tmp = "$path.tmp"
  $fs = [System.IO.File]::Create($tmp)
  $enc.Save($fs); $fs.Dispose(); $s.Dispose()
  Move-Item -LiteralPath $tmp -Destination $path -Force
}

# Nearest of the four quadrant colours. JPEG is lossy (and subsamples chroma), so an exact RGB
# match would be the wrong assertion for a .jpg output.
function Get-ColourName([int]$r, [int]$g, [int]$b) {
  $ref = @(
    [pscustomobject]@{ N = 'A-red';    R = 255; G = 0;   B = 0 },
    [pscustomobject]@{ N = 'B-green';  R = 0;   G = 255; B = 0 },
    [pscustomobject]@{ N = 'C-blue';   R = 0;   G = 0;   B = 255 },
    [pscustomobject]@{ N = 'D-yellow'; R = 255; G = 255; B = 0 })
  $best = ''; $bestD = [int]::MaxValue
  foreach ($p in $ref) {
    $d = [Math]::Abs($r - $p.R) + [Math]::Abs($g - $p.G) + [Math]::Abs($b - $p.B)
    if ($d -lt $bestD) { $bestD = $d; $best = $p.N }
  }
  return $best
}
function Get-PixelName([string]$path, [int]$x, [int]$y) {
  $raw = Get-PixelAt $path $x $y
  if ($raw -notmatch '^(\d+),(\d+),(\d+)$') { return $raw }
  return (Get-ColourName ([int]$Matches[1]) ([int]$Matches[2]) ([int]$Matches[3])) + "($raw)"
}

# A sample point outside the bitmap has to FAIL a check, not abort the whole run: with
# -ErrorActionPreference Stop a raw GetPixel throws, and a fault injection that makes an output
# smaller than expected would then end the suite with an exception instead of a red check
# (that is how this helper came to exist - the first rotated-source injection did exactly that).
function Get-PixelAt([string]$path, [int]$x, [int]$y) {
  try {
    $bmp = New-Object System.Drawing.Bitmap($path)
    if (($x -lt 0) -or ($y -lt 0) -or ($x -ge $bmp.Width) -or ($y -ge $bmp.Height)) {
      $s = "OUT-OF-RANGE(sampled $x,$y of $($bmp.Width)x$($bmp.Height))"
      $bmp.Dispose()
      return $s
    }
    $c = $bmp.GetPixel($x, $y)
    $s = "$($c.R),$($c.G),$($c.B)"
    $bmp.Dispose()
    return $s
  } catch { return 'UNREADABLE' }
}

# The EXIF orientation tag (274) of a file, 0 when absent, -1 when unreadable. "the tag must not be
# left saying 'rotate me again'" is only an assertion if a script can read the tag back.
function Get-ExifOrientation([string]$path) {
  try {
    $s = [System.IO.File]::OpenRead($path)
    $f = [System.Windows.Media.Imaging.BitmapFrame]::Create($s,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $v = 0
    if ($f.Metadata -is [System.Windows.Media.Imaging.BitmapMetadata]) {
      $m = [System.Windows.Media.Imaging.BitmapMetadata]$f.Metadata
      foreach ($q in @('/app1/ifd/{ushort=274}', '/ifd/{ushort=274}')) {
        if ($m.ContainsQuery($q)) { $o = $m.GetQuery($q); if ($null -ne $o) { $v = [int]$o; break } }
      }
    }
    $s.Dispose()
    return $v
  } catch { return -1 }
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
  # 'movedToExpected=True clampHeldInsideImage=True handlesResize=True' is the drag line the window
  # reports about the DRAG ITSELF: a moved rectangle at the expected pixel values, a move that stops
  # at the image edge while it is being dragged, and the three handle drags (right edge grows,
  # left edge shrinks, bottom-right corner grows). Before this existed the drag key was only
  # "the three mouse handlers) ok=True", which was green even when the rectangle never moved.
  $uiKeys = @('opened=True', 'windowPresent=True', 'ignored (single instance)',
              'settingsOpen=True cropWindowStillPresent=True', 'the three mouse handlers) ok=True',
              'movedToExpected=True clampHeldInsideImage=True handlesResize=True',
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

  # ================================================================ the user's own picture size
  # Every fixture above is small, and this suite was 19/19 green while the crop WINDOW selftest was
  # in fact RED on any image bigger than its fixture: the window's own assertion had 512x384
  # written into it ("the full-frame rectangle is 0,0,512,384"), so on a 3000x2250 image - where the
  # rectangle is correctly 0,0,3000,2250 and the confirm button correctly refuses it - that flag
  # went false and the whole selftest exited 1. A green suite that cannot see the user's own data
  # size is the failure mode these three checks exist to remove.
  #
  # Time: ONE big fixture, built by a single native fill (no per-pixel loop) and reused by all three
  # checks below; the three of them cost about 7 s in total, against about 50 s for the 19 small ones.
  $fBig = Join-Path $fdir 'img-3000x2250.png'
  New-BigGradientPng $fBig 3000 2250

  # ---------------------------------------------------------------- S18.18 the big crop
  # 3000x2250, box 0.05,0.05,0.9,0.9, asked for 1:1. Boundaries are rounded half-up, away from zero:
  #   left=round(0.05*3000)=150   right=round(0.95*3000)=2850  -> 2700 wide
  #   top=round(0.05*2250)=113    bottom=round(0.95*2250)=2138  -> 2025 tall   (112.5 and 2137.5)
  #   1:1 -> the box is already wider than tall, so the biggest centred square in it is 2025x2025
  #          at x=150+(2700-2025)/2=487, y=113.
  # A size alone cannot tell "the right region" from "a region of the right size" (see S18.12), so
  # both ends of the used box are compared with the source pixel by pixel. PNG output is lossless,
  # so an exact match is a meaningful assertion.
  $big = Invoke-Crop 'big-3000x2250' $fBig '0.05,0.05,0.9,0.9' '1:1'
  $bigCornerOk = $false
  $bigFarOk = $false
  $bigDetail = 'no output to sample'
  if (($null -ne $big.Out) -and (Test-Path -LiteralPath $big.Out)) {
    $s0 = Get-PixelAt $big.Src 487 113
    $o0 = Get-PixelAt $big.Out 0 0
    $bigCornerOk = ($s0 -eq $o0)
    $s1 = Get-PixelAt $big.Src 2511 2137
    $o1 = Get-PixelAt $big.Out 2024 2024
    $bigFarOk = ($s1 -eq $o1)
    $bigDetail = "out(0,0)=$o0 vs src(487,113)=$s0 | out(2024,2024)=$o1 vs src(2511,2137)=$s1"
  }
  Check 'S18.18 a 3000x2250 image crops to exactly 2025x2025 at (487,113), and both corner pixels match the source' `
    (($big.Exit -eq 0) -and ($big.OutSize -eq '2025x2025') -and $bigCornerOk -and $bigFarOk -and $big.Untouched) `
    "exit=$($big.Exit) measured=$($big.OutSize) $bigDetail line='$($big.OutputLine)'"

  # ---------------------------------------------------------------- S18.19 the big image, full frame
  # The engine-level sibling of the window flag that was red: "covers the whole image" must still
  # fire at a size the suite never used, and a no-op crop must still write nothing.
  $bigFull = Invoke-Crop 'big-full-frame' $fBig '0,0,1,1' 'free'
  Check 'S18.19 a full-frame rectangle on a 3000x2250 image is refused as well (no file written, named reason)' `
    (($bigFull.Exit -eq 3) -and ($bigFull.OutSize -eq 'MISSING') -and $bigFull.Untouched -and
     (Test-Log $bigFull.Lines 'CropNothingToDo')) `
    "exit=$($bigFull.Exit) output=$($bigFull.OutSize) line='$($bigFull.OutputLine)'"

  # ---------------------------------------------------------------- S18.20 the WINDOW on a big image
  # The regression guard for the defect itself. The keys are the ones the window reports about
  # ITSELF (its own image size, the pixel rectangle it produced, the refusal), never a literal
  # taken from a fixture - that is exactly what makes them true for every size.
  $fBigUi = Invoke-Confern 'ui-selftest-big' @('--crop-ui-selftest', '--crop-source', $fBig)
  $fBigUiKeys = @('opened=True', 'windowPresent=True', 'image=3000x2250',
                  'refused=True stillOpen=True', 'rect=0,0,3000,2250',
                  'movedToExpected=True clampHeldInsideImage=True handlesResize=True',
                  'clampedTo1px=True', 'matchesWhatWasDrawn=True',
                  'reopened=True windowPresent=True')
  $fBigUiMissing = @()
  foreach ($k in $fBigUiKeys) {
    if (-not (Test-Log $fBigUi.Lines ([regex]::Escape($k)))) { $fBigUiMissing += $k }
  }
  Check 'S18.20 the crop WINDOW selftest passes on a 3000x2250 image, full-frame refusal included' `
    (($fBigUi.Exit -eq 0) -and ($fBigUiMissing.Count -eq 0)) `
    ("exit=$($fBigUi.Exit) missing='" + ($fBigUiMissing -join ' | ') + "' summary='" +
     (Get-LogLine $fBigUi.Lines 'selftest done:') + "'")

  # ---------------------------------------------------------------- S18.21 a ROTATED source, cropped
  # Nothing above carried an EXIF orientation, so "the phone photo is rotated and then cropped" was
  # unverified. Orientation 6 means "rotate 90 CW to display", so a STORED 800x400 is SEEN as
  # 400x800:
  #   stored  : TL=red   TR=green        (800 wide, 400 tall)
  #   upright : TL=blue  TR=red          (400 wide, 800 tall)
  #             BL=yellow BR=green
  # (Same table as verify-orientation-matrix.ps1, which proves orientations 1-8 on the pixels.)
  # The window shows the upright picture, so the rectangle is drawn on it and the engine bakes the
  # orientation in BEFORE cropping. A crop of 0,0,0.5,0.5 must therefore be 200x400 and BLUE. If the
  # tag were ignored the frame would still be 800x400 and the same rectangle would give 400x200 of
  # stored-TL red - so the size and the colour each discriminate on their own.
  $fO6 = Join-Path $fdir 'exif6-800x400.jpg'
  New-QuadrantJpeg $fO6 800 400 6
  $o6Tl = Invoke-Crop 'exif6-tl-corner' $fO6 '0,0,0.5,0.5' 'free'
  $o6Right = Invoke-Crop 'exif6-right-half' $fO6 '0.5,0,0.5,1' 'free'
  $o6TlColour = 'MISSING'; $o6TopColour = 'MISSING'; $o6BottomColour = 'MISSING'
  if (($null -ne $o6Tl.Out) -and (Test-Path -LiteralPath $o6Tl.Out)) {
    $o6TlColour = Get-PixelName $o6Tl.Out 100 200
  }
  if (($null -ne $o6Right.Out) -and (Test-Path -LiteralPath $o6Right.Out)) {
    $o6TopColour = Get-PixelName $o6Right.Out 100 100
    $o6BottomColour = Get-PixelName $o6Right.Out 100 600
  }
  Check 'S18.21 EXIF orientation 6 + crop: the rectangle is taken on the UPRIGHT picture (200x400 blue, 200x800 red over green)' `
    (($o6Tl.Exit -eq 0) -and ($o6Tl.OutSize -eq '200x400') -and
     ($o6Right.Exit -eq 0) -and ($o6Right.OutSize -eq '200x800') -and
     ($o6TlColour -like 'C-blue*') -and ($o6TopColour -like 'A-red*') -and
     ($o6BottomColour -like 'B-green*') -and $o6Tl.Untouched -and $o6Right.Untouched) `
    ("tl=$($o6Tl.OutSize) $o6TlColour | right=$($o6Right.OutSize) top=$o6TopColour " +
     "bottom=$o6BottomColour")

  # ---------------------------------------------------------------- S18.22 ...and the tag is gone
  # The orientation was baked into the pixels, so a viewer that honours the tag must not rotate the
  # result a SECOND time. "JPG keeps its metadata" is the rule for --convert, but a crop always
  # moves pixels, so a stale tag here would be a lie.
  #
  # WHAT THIS DOES AND DOES NOT PROVE (measured, not assumed): the crop path never carries metadata
  # over at all today, because the frame it encodes is a CroppedBitmap and Encode only adopts
  # metadata when "frame is BitmapFrame". So the output has NO orientation tag (0, absent) rather
  # than a neutralised one, and this check cannot go red from "the tag was ignored while cropping"
  # - that failure is S18.21's job. What it pins is the property itself, so that a future change
  # which starts carrying metadata across a crop has to neutralise it to pass. The source half of
  # the assertion (tag=6 read back off the fixture) is what keeps this from being a reader that
  # always answers 0.
  $o6SrcTag = Get-ExifOrientation $fO6
  $o6OutTag = -1
  if (($null -ne $o6Right.Out) -and (Test-Path -LiteralPath $o6Right.Out)) {
    $o6OutTag = Get-ExifOrientation $o6Right.Out
  }
  Check 'S18.22 the cropped output carries no EXIF orientation left to rotate it again (source 6, output 0 or 1)' `
    (($o6SrcTag -eq 6) -and ($o6OutTag -ge 0) -and ($o6OutTag -le 1)) `
    "source orientation=$o6SrcTag output orientation=$o6OutTag"
}

# ------------------------------------------------------------------ main
$sections = @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
Write-Host "verify-m3d: exe=$Exe sections=$($sections -join ',') logdir=$LogDir fault='$FaultInjection'"
if ($FaultInjection -ne '') { Write-Host "verify-m3d: $(Get-FaultNote)" }

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

foreach ($s in $sections) {
  switch ($s) {
    'S18' { Expect 24; Invoke-S18 }
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
