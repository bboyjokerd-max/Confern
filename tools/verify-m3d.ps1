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
  $err = Join-Path $Work "$name.stderr.txt"
  if (Test-Path -LiteralPath $err) { Remove-Item -LiteralPath $err -Force }
  # stderr is redirected as well: an unredirected stream fails Start-Process -Wait -PassThru
  # outright ("cannot use -Wait with redirected output" style errors), and --help writes the usage
  # text to stdout while a warning would go to stderr.
  $p = Start-Process -FilePath $Exe -ArgumentList $argv -Wait -PassThru -WindowStyle Hidden `
                     -RedirectStandardOutput $out -RedirectStandardError $err
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
  # This is the same catalog the wheel draws (ToolCatalog.ToolsFor), so "crop is fourth" is a fact
  # that can be re-checked at any time - including while the user's own copy is running.
  #
  # !! 2026-10-07: the count moved from 4 to 5 because `read-qr` was really added to the catalog
  # (the FIFTH sector; its own checks are S19.07 onwards). The id list is asserted whole on
  # purpose: a tool appearing or disappearing changes this line, and that has to be a conscious
  # edit here and not a silent drift.
  $tl = Invoke-Confern 'tools-list' @('--tools-list')
  Check 'S18.03 the tool layer lists every sector in order, and crop is the fourth' `
    (($tl.Exit -eq 0) -and
     (Test-Log $tl.Lines 'tool list: count=5 tools=\[rotate90,strip-metadata,compress,crop,read-qr\]') -and
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

  # ---------------------------------------------------------------- S18.08 aspect presets
  # The canonical value set is the PRODUCT's list, not a literal in this script: the window draws
  # five BUTTONS, but eight ids are legal (9:16 / 3:4 / original-flipped are produced by the
  # window's direction toggle, and every one of them has to reach the engine over the worker pipe).
  # The list is parsed out of the product's own rejection message, so an id added to the product
  # cannot be forgotten here. (The parse lives in S18.14, which already runs that bad-ratio case.)
  # 800x600, box 0.05,0.05,0.9,0.9 -> left=40, right=760 (720 wide), top=30, bottom=570 (540 tall).
  #   free              -> 720x540 (untouched box)   ratio 1.3333
  #   1:1               -> 540x540 (height limited)  ratio 1.0000
  #   16:9              -> 720x405 (width limited)   ratio 1.7778
  #   9:16              -> 304x540 (height limited)  ratio 0.5625
  #   4:3               -> 720x540 (exactly fits)    ratio 1.3333
  #   3:4               -> 405x540 (height limited)  ratio 0.7500
  #   original          -> 720x540 (this image's own 4:3)   ratio 1.3333
  #   original-flipped  -> 720x540 box, 540x720 does NOT fit -> 405x540, ratio 0.7500
  # Tolerance: the constrained side is rounded to a whole pixel, so the ratio can be off by half
  # a pixel on that side; 540 px gives 0.5/540 = 0.09 %, and this check allows 1 %.
  $presets800 = @(
    @{ Id = 'free';             W = 720;   H = 540;   Target = 0.0 },
    @{ Id = '1:1';              W = 540;   H = 540;   Target = 1.0 },
    @{ Id = '16:9';             W = 720;   H = 405;   Target = 16.0 / 9.0 },
    @{ Id = '9:16';             W = 304;   H = 540;   Target = 9.0 / 16.0 },
    @{ Id = '4:3';              W = 720;   H = 540;   Target = 4.0 / 3.0 },
    @{ Id = '3:4';              W = 405;   H = 540;   Target = 3.0 / 4.0 },
    @{ Id = 'original';         W = 720;   H = 540;   Target = 800.0 / 600.0 },
    @{ Id = 'original-flipped'; W = 405;   H = 540;   Target = 600.0 / 800.0 }
  )
  $ratioLog = New-Object System.Collections.ArrayList
  $ratioOk = $true
  foreach ($p in $presets800) {
    $r = Invoke-Crop ("ratio-" + ($p.Id -replace ':', '-')) $f800 '0.05,0.05,0.9,0.9' $p.Id
    $w = -1; $h = -1
    if ($r.OutSize -match '^(\d+)x(\d+)$') { $w = [int]$Matches[1]; $h = [int]$Matches[2] }
    $wantW = $p.W; $wantH = $p.H
    if ($FaultInjection -eq 'ignore-ratio' -and $p.Id -eq '1:1') { $wantW = 720; $wantH = 540 }
    $sizeOk = (($w -eq $wantW) -and ($h -eq $wantH))
    $targetRatio = $p.Target
    $actualRatio = 0.0
    if ($h -gt 0) { $actualRatio = $w / $h }
    $ratioWithin = $true
    if ($targetRatio -gt 0) { $ratioWithin = ([math]::Abs($actualRatio - $targetRatio) / $targetRatio) -le 0.01 }
    [void]$ratioLog.Add("$($p.Id): $($r.OutSize) (want $($wantW)x$($wantH)) ratio=$([math]::Round($actualRatio,4)) target=$([math]::Round($targetRatio,4)) within1pct=$ratioWithin")
    if (-not ($sizeOk -and $ratioWithin -and $r.Untouched)) { $ratioOk = $false }
  }
  Check 'S18.08 every aspect preset (all eight ids, both orientations) reaches the engine and the output ratio is within 1 percent of the target' `
    $ratioOk `
    ($ratioLog -join ' | ')

  # ---------------------------------------------------------------- S18.08b direction is a fact
  # The crop rectangle has WIDTH and HEIGHT, so "this ratio is vertical" is a claim about which of
  # the two is bigger - and until S18.08b nothing in this suite tested it. A `9:16` whose constant
  # was written as 16.0/9.0 would keep `sizeOk` green in NO case above (the size check pins 304x540),
  # but a rotated SOURCE would have flipped it silently: the per-image check below is the one that
  # says "the same preset, two source orientations, one direction".
  $dirLog = New-Object System.Collections.ArrayList
  $dirOk = $true
  foreach ($p in $presets800) {
    $w = -1; $h = -1
    $r = Invoke-Crop ("dir-" + ($p.Id -replace ':', '-')) $f800 '0.05,0.05,0.9,0.9' $p.Id
    if ($r.OutSize -match '^(\d+)x(\d+)$') { $w = [int]$Matches[1]; $h = [int]$Matches[2] }
    $fact = 'unknown'
    if ($h -gt 0) { if ($w -gt $h) { $fact = 'wide' } elseif ($w -lt $h) { $fact = 'tall' } else { $fact = 'square' } }
    $want = 'square'
    switch ($p.Id) {
      'free' { $want = 'wide' }   # free keeps the 0.9x0.9 box: on a landscape image that box is wide
      '16:9' { $want = 'wide' }
      '4:3'  { $want = 'wide' }
      'original' { $want = 'wide' }
      '9:16' { $want = 'tall' }
      '3:4'  { $want = 'tall' }
      'original-flipped' { $want = 'tall' }
    }
    [void]$dirLog.Add("$($p.Id): $($r.OutSize) dir=$fact want=$want")
    if ($fact -ne $want) { $dirOk = $false }
  }
  Check 'S18.08b the measured output direction matches the preset that was asked for (9:16 and 3:4 really are taller than they are wide)' `
    $dirOk `
    ($dirLog -join ' | ')

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

  # ---------------------------------------------------------------- S18.13b a PORTRAIT source
  # Everything above runs on landscape fixtures. The new presets are the ones that make a portrait
  # source matter: a 9:16 crop of a landscape picture and a 9:16 crop of a portrait picture must
  # point the SAME way, and original/original-flipped must each follow their own source (that is
  # what "per image, its own size" means for the two per-image presets).
  # Fixture: 300x700 (portrait) - already built above, so this costs three crop runs.
  # 300x700 image, box 0.05..0.95 -> left=15, right=285 (270 wide), top=35, bottom=665 (630 tall).
  #   9:16             -> target 0.5625, tallest allowed is 630 -> 630*0.5625 = 354.375 -> 354x630 (tall)
  #   original         -> target 300/700 = 0.4286, height limited -> 270*7/3 = 630 -> 270x630 (tall)
  #   original-flipped -> target 700/300 = 2.3333 (WIDE on a portrait source!), height limited:
  #                       630 / 2.3333 = 270 -> 270x630 does fit the box -> 270x630? no: the box is
  #                       270 wide, so the width caps it at 270 and the height becomes 270/2.3333=115.7
  #                       -> 270x116 (wide). Measured in the detail line; the direction is asserted.
  $port = Invoke-Crop 'portrait-tall' $f300 '0.05,0.05,0.9,0.9' '9:16'
  $portFlip = Invoke-Crop 'portrait-original-flipped' $f300 '0.05,0.05,0.9,0.9' 'original-flipped'
  $portOrig = Invoke-Crop 'portrait-original' $f300 '0.05,0.05,0.9,0.9' 'original'
  # "wide" / "tall" / "square" from a measured "WxH" string. One implementation, used by every
  # direction check below (a second copy would be a second rule).
  function Get-DirectionOf([string]$size) {
    if ($size -notmatch '^(\d+)x(\d+)$') { return 'unknown' }
    $dw = [int]$Matches[1]; $dh = [int]$Matches[2]
    if ($dw -gt $dh) { return 'wide' } elseif ($dw -lt $dh) { return 'tall' } else { return 'square' }
  }
  $portTallDir = Get-DirectionOf $port.OutSize
  $portFlipDir = Get-DirectionOf $portFlip.OutSize
  $portOrigDir = Get-DirectionOf $portOrig.OutSize
  # The two per-image presets must be each other's opposite on the SAME source: that is the whole
  # content of "original-flipped is original rotated by 90 degrees".
  $portFlipIsReciprocal = $false
  if ($portFlipDir -ne 'unknown' -and $portOrigDir -ne 'unknown' -and
      $portFlipDir -ne 'square' -and $portOrigDir -ne 'square') {
    $portFlipIsReciprocal = ($portFlipDir -ne $portOrigDir)
  }
  Check 'S18.13b a PORTRAIT source: 9:16 is still tall, original is tall, original-flipped is wide, and the two per-image presets are each others reciprocal on this image' `
    (($port.OutSize -eq '270x480') -and ($portTallDir -eq 'tall') -and
     ($portOrig.OutSize -eq '270x630') -and ($portOrigDir -eq 'tall') -and
     ($portFlipDir -eq 'wide') -and $portFlipIsReciprocal -and
     $port.Untouched -and $portFlip.Untouched -and $portOrig.Untouched) `
    "9:16=$($port.OutSize) dir=$portTallDir original=$($portOrig.OutSize) dir=$portOrigDir original-flipped=$($portFlip.OutSize) dir=$portFlipDir reciprocal=$portFlipIsReciprocal untouched=$($port.Untouched -and $portFlip.Untouched -and $portOrig.Untouched)"

  # ---------------------------------------------------------------- S18.13c one batch, two orientations
  # "The same ratio for every image, each image converted with its own size" has to hold for the
  # per-image preset too: original-flipped on a LANDSCAPE image and on a PORTRAIT image in ONE job
  # must produce two outputs that KEEP their own orientation. Measured: 260x346 (wide, from 512x384)
  # and 116x270 (tall, from 300x700). If the ratio were computed from the first image and applied to
  # both, both would come out the same way - which is exactly the bug this pins.
  $md = Join-Path $Work 'mixed-orientation'
  New-Item -ItemType Directory -Force -Path $md | Out-Null
  $m1 = Join-Path $md 'a-512x384.png'
  $m2 = Join-Path $md 'b-300x700.png'
  Copy-Item -LiteralPath $f512 -Destination $m1 -Force
  Copy-Item -LiteralPath $f300 -Destination $m2 -Force
  $mSha1 = Get-Sha $m1; $mSha2 = Get-Sha $m2
  $mLog = Join-Path $Work 'mixed.log'
  if (Test-Path -LiteralPath $mLog) { Remove-Item -LiteralPath $mLog -Force }
  $mp = Start-Process -FilePath $Exe -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
    '--crop', "$m1;$m2", '--crop-rect', '0.05,0.05,0.9,0.9', '--crop-ratio', 'original-flipped',
    '--log', $mLog, '--quiet', '--no-autostart')
  $mLines = Get-LogLines $mLog
  $mo1 = Get-NewestOutput $md $m1
  $mo2 = Get-NewestOutput $md $m2
  $ms1 = 'MISSING'; $ms2 = 'MISSING'
  if ($null -ne $mo1) { $ms1 = Get-ImageSize $mo1 }
  if ($null -ne $mo2) { $ms2 = Get-ImageSize $mo2 }
  $ms1Dir = Get-DirectionOf $ms1
  $ms2Dir = Get-DirectionOf $ms2
  # !! The two sources are OPPOSITE orientations, so original-flipped has to turn them in OPPOSITE
  # directions: 512x384 (landscape box 460x345) -> 260x346 (tall), and 300x700 (portrait box 270x630)
  # -> 116x270 (wide). MEASURED, not derived: an earlier version of this check guessed 460x345 /
  # 630x270 from the box alone, which quietly assumed the ratio would be applied to the height.
  $ms1Wanted = '260x346'
  $ms2Wanted = '270x116'
  $mUntouched = ((Get-Sha $m1) -eq $mSha1) -and ((Get-Sha $m2) -eq $mSha2)
  Check 'S18.13c one job, two source orientations, original-flipped: each output is turned 90 degrees away from ITS OWN image (one comes out tall, the other wide)' `
    (($mp.ExitCode -eq 0) -and ($ms1Dir -ne $ms2Dir) -and
     ($ms1Dir -eq 'tall') -and ($ms2Dir -eq 'wide') -and
     ($ms1 -eq $ms1Wanted) -and ($ms2 -eq $ms2Wanted) -and
     $mUntouched -and (Test-Log $mLines 'ok=2 failed=0')) `
    "exit=$($mp.ExitCode) a(512x384)=$ms1 dir=$ms1Dir (want $ms1Wanted/tall) b(300x700)=$ms2 dir=$ms2Dir (want $ms2Wanted/wide) sourcesUntouched=$mUntouched out1='$(Split-Path -Leaf $mo1)' out2='$(Split-Path -Leaf $mo2)'"

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

  # The canonical set, read from the product's own rejection message. What this pins is the SPLIT
  # between "which values are accepted" and "how many buttons the window draws": the three ids the
  # direction toggle produces have no button, but every one of them must be accepted by the CLI,
  # must travel over the worker pipe, and must reach the engine with its own direction.
  $declaredSet = @()
  $declLine = Get-LogLine $badRatio.Lines 'expected one of '
  if ($declLine -match 'expected one of ([^)]+)\)') { $declaredSet = @($Matches[1] -split '/' | Where-Object { $_ -ne '' }) }
  Check 'S18.14b the product declares its whole canonical ratio set: eight ids (five buttons plus the three the direction toggle produces)' `
    (($declaredSet.Count -eq 8) -and ($declaredSet -contains '9:16') -and ($declaredSet -contains '3:4') -and
     ($declaredSet -contains 'original-flipped')) `
    "declared=($($declaredSet -join '/')) from '$declLine'"

  $acceptLog = New-Object System.Collections.ArrayList
  $acceptOk = $declaredSet.Count -eq 8
  foreach ($id in $declaredSet) {
    $r = Invoke-Crop ("accept-" + ($id -replace ':', '-')) $f512 '0.1,0.1,0.5,0.5' $id
    # !! The ratio id lives on the "worker: job N sent ..." line, NOT in JobLine's `crop=(...)`
    # segment: `crop=rect=(0.1,0.1,0.5,0.5) ratio=9:16` is appended AFTER the parenthesis, so a
    # pattern anchored to `crop=` would never see it (measured, after a first version of this check
    # reported onWorkerLine=False for all eight ids on a product that does send them).
    $job = Get-LogLine $r.Lines 'worker: job \d+ sent '
    # The id must be followed by a space or the end of the line, so an id that is a prefix of
    # another one cannot pass by accident.
    $onJob = [bool]($job -match ("ratio=" + [regex]::Escape($id) + '( |$)'))
    if (-not (($r.Exit -eq 0) -and $onJob -and $r.Untouched)) { $acceptOk = $false }
    [void]$acceptLog.Add("$id exit=$($r.Exit) out=$($r.OutSize) onWorkerLine=$onJob untouched=$($r.Untouched)")
  }
  Check 'S18.14c every declared ratio id is accepted by the CLI and really crosses the worker pipe (the id is on the worker job line)' `
    $acceptOk `
    ($acceptLog -join ' | ')

  # The usage text has to name every value it accepts: `--help` is the only documentation the tool
  # has, and a value that exists but is undocumented is a value nobody can use.
  # !! `--help` exits 2 (it is not "the run succeeded"), so the exit code is NOT part of this check -
  # only that the 118 usage lines were printed and every declared id appears in them.
  $helpRatio = Invoke-ConfernStdout 'help-ratio' @('--help')
  $missingHelp = @()
  foreach ($id in $declaredSet) {
    $found = @($helpRatio.Lines | Where-Object { ([string]$_).Contains($id) }).Count -gt 0
    if (-not $found) { $missingHelp += $id }
  }
  Check 'S18.14d --help names every declared ratio id (including the three that have no button)' `
    (($helpRatio.Lines.Count -gt 20) -and ($missingHelp.Count -eq 0)) `
    "exit=$($helpRatio.Exit) lines=$($helpRatio.Lines.Count) missing='$($missingHelp -join ',')' line='$(Get-LogLine $helpRatio.Lines '--crop-ratio')'"

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

  # The NEW-box drag on a FRESHLY OPENED window (the rectangle is the whole image then).
  # This is the first thing a user does, and it used to do NOTHING: BeginDrag picked Move, while
  # Move is a provable no-op at that moment (the position clamp is [0, imageW - rectW], and rectW
  # equals imageW). The window's subtitle promises "drag a rectangle on the picture", so this key
  # pins the promise. 'mode=New' alone would be a weak assertion, so the three lines below are
  # checked together with the summary flag: the box must really differ from the whole image, and
  # 'anchorIsStart=True oppositeIsEnd=True' is the window's OWN verdict on the pixel values it
  # reported (no fixture number is written into this script).
  $newBoxKeys = @('NEW box drag from inside the photo towards the bottom-right: mode=New',
                  'ok=True (pixels', 'anchorIsStart=True oppositeIsEnd=True differs=True notFullFrame=True',
                  'NEW box drag from inside the photo towards the top-left (reverse direction): mode=New',
                  'NEW box drag starting in the blank margin beside the photo: ok=True',
                  'newBoxDrag=True (down=True reverse=True margin=True)')
  $newBoxMissing = @()
  foreach ($k in $newBoxKeys) { if (-not (Test-Log $ui.Lines ([regex]::Escape($k)))) { $newBoxMissing += $k } }
  Check 'S18.15c dragging inside the freshly opened window (rectangle = whole image) really creates a NEW box, in both directions, anchored at the press point' `
    (($newBoxMissing.Count -eq 0) -and
     (Test-Log $ui.Lines 'newBoxDrag=True \(down=True reverse=True margin=True\)')) `
    "missing='" + ($newBoxMissing -join ' | ') + "' line='" + (Get-LogLine $ui.Lines 'NEW box drag from inside the photo towards the bottom-right') + "'"

  # ---------------------------------------------------------------- S18.15h a flush edge is movable
  # 2026-10-07 USER REPORT (real hardware): "I want to nudge the box and it wipes my selection and
  # makes me start over". Root cause: the drag classifier asked `roomOnX && roomOnY`, so a rectangle
  # flush with ONE image edge was classified as New. That flush state is the COMMON one, not an
  # edge case: a portrait photo with 16:9/4:3 gets a box whose width equals the image width, because
  # CropMath takes the largest same-ratio rectangle inside the box.
  # !! The pre-existing move check used a centred baseline (0.25,0.25,0.5,0.5), whose four edges are
  # all away from the image border - which is exactly why this defect survived every suite. The two
  # halves below pin BOTH sides of the condition: one axis flush must still be Move (the useful
  # behaviour), while a rectangle that IS the whole image must still be New (otherwise opening the
  # window and dragging on the picture does nothing - the defect fixed on 2026-10-06).
  # No fixture number is written here: the base rectangle, the displacement and the end rectangle are
  # read out of the product's own line, and the arithmetic is checked against them.
  $spanLine = Get-LogLine $ui.Lines 'spans the full WIDTH'
  $spanOk = $false
  $spanDetail = "line='" + $spanLine + "'"
  if ($spanLine -match 'base=(\d+),(\d+),(\d+),(\d+) dy=(\d+) end=(\d+),(\d+),(\d+),(\d+) mode=(\w+) moved=(\w+)') {
    $sBX = [int]$Matches[1]; $sBY = [int]$Matches[2]; $sBW = [int]$Matches[3]; $sBH = [int]$Matches[4]
    $sDy = [int]$Matches[5]
    $sEX = [int]$Matches[6]; $sEY = [int]$Matches[7]; $sEW = [int]$Matches[8]; $sEH = [int]$Matches[9]
    $sMode = $Matches[10]; $sMoved = $Matches[11]
    $spanOk = ($sMode -eq 'Move') -and ($sMoved -eq 'True') -and
              ($sEY -eq $sBY + $sDy) -and ($sEX -eq $sBX) -and
              ($sEW -eq $sBW) -and ($sEH -eq $sBH) -and
              ($sBW -gt 0) -and ($sBH -gt 0) -and ($sDy -gt 0) -and
              (Test-Log $ui.Lines 'startsNew=True ok=True')
    $spanDetail = "mode=$sMode moved=$sMoved base=$sBX,$sBY,$sBW,$sBH dy=$sDy end=$sEX,$sEY,$sEW,$sEH"
  }
  Check 'S18.15h a rectangle flush with ONE image edge is still MOVED by dragging inside it, while a rectangle that IS the whole image still starts a new selection' `
    $spanOk $spanDetail

  # ---------------------------------------------------------------- S18.15i the flip is reversible
  # 2026-10-07 USER REPORT (real hardware): "if I keep pressing the portrait/landscape toggle the crop
  # box gets smaller and smaller - can it stay the same size?".
  # Root cause: ToggleDirection went through SelectRatio, whose semantics are "the largest same-ratio
  # rectangle INSIDE the old box". Every flip re-clamped the previous result, so six flips turned
  # 600x338 into 19x11 (measured on the 600x900 fixture: 190x338 -> 190x107 -> 60x107 -> ...).
  # The behaviour now: a flip keeps the long side and the centre, and flipping BACK restores the
  # pre-flip rectangle exactly - which is what a landscape source needs, where a 900-wide box cannot
  # fit as a portrait box at all (measured: 900x506 -> 338x600 -> 900x506).
  # !! The product prints its own verdict flags, but reading a flag is a weak assertion, so the sizes
  # are parsed out of the product's line and re-derived here: every EVEN flip must return to the base
  # size exactly, all ODD flips must be identical (and different from the base), and the flipped area
  # must still be at least 95% of what fits.
  $flipLine = Get-LogLine $ui.Lines 'toggling the direction six times'
  $flipOk = $false
  $flipDetail = "line='" + $flipLine + "'"
  if ($flipLine -match 'base=(\d+)x(\d+) sizes=([0-9x >-]+) .*oddArea=(\d+) expectedArea=(\d+)') {
    $fBase = "$($Matches[1])x$($Matches[2])"
    $fSizes = @($Matches[3].Trim() -split '\s*->\s*')
    $fOddArea = [long]$Matches[4]; $fExpect = [long]$Matches[5]
    $fEven = ($fSizes.Count -eq 6) -and ($fSizes[1] -eq $fBase) -and ($fSizes[3] -eq $fBase) -and ($fSizes[5] -eq $fBase)
    $fOddSame = ($fSizes.Count -eq 6) -and ($fSizes[0] -eq $fSizes[2]) -and ($fSizes[2] -eq $fSizes[4]) -and ($fSizes[0] -ne $fBase)
    $fBig = ($fExpect -gt 0) -and ($fOddArea * 100 -ge $fExpect * 95)
    $flipOk = $fEven -and $fOddSame -and $fBig -and
              (Test-Log $ui.Lines 'backToBase=True oddStable=True oddIsTall=True') -and
              (Test-Log $ui.Lines 'oddBigEnough=True ok=True')
    $flipDetail = "base=$fBase sizes=$($fSizes -join ' -> ') oddArea=$fOddArea expectedArea=$fExpect backToBase=$fEven oddStable=$fOddSame bigEnough=$fBig"
  }
  Check 'S18.15i flipping the direction repeatedly does NOT shrink the box: every second flip returns to the exact base size, every flipped size is identical, and the flipped box is still as large as fits' `
    $flipOk $flipDetail

  # The ratio buttons: each one must be reachable AND must actually change the rectangle.
  # !! The `^` anchor is not decoration: the summary line "ratio states: free: ... ratio=free rect=..."
  # also contains the text "ratio 'free' ->" (inside the state it embeds), so an unanchored pattern
  # counted 10 states for 5 buttons and made a derived expectation look wrong.
  # The EXPECTED count comes from the product's own declared id set (8) minus the three ids that
  # have no button - i.e. it is derived, not written down. A hard-coded 5 would silently pass if a
  # button disappeared and another one appeared.
  $ratioStates = @($ui.Lines | Select-String -Pattern "^.*crop ui selftest: ratio '(free|1:1|16:9|9:16|4:3|3:4|original|original-flipped)' ->")
  # !! The count is divided by 2 because the window's selftest walks every button TWICE (once on the
  # source it was given, once on that source rotated 90 degrees - see S18.15d). Deriving this from
  # the passes keeps the expectation honest: "five buttons, two passes each".
  $ratioPasses = 2
  $ratioButtonCount = [int]($ratioStates.Count / $ratioPasses)
  $ratioStatesOk = ($ratioStates.Count -eq (($declaredSet.Count - 3) * $ratioPasses)) -and
                   ($ratioStates.Count % $ratioPasses -eq 0)
  foreach ($s in $ratioStates) {
    if ([string]$s.Line -notmatch 'ratio-ok=True') { $ratioStatesOk = $false }
  }
  Check 'S18.15b every preset BUTTON is reachable, and the ones with a target ratio report ratio-ok=True' `
    $ratioStatesOk `
    ("buttonCount=$ratioButtonCount passes=$ratioPasses states=$($ratioStates.Count) expected=$($declaredSet.Count - 3) || " + (($ratioStates | ForEach-Object { ([string]$_.Line).Trim() }) -join ' || '))

  # ---------------------------------------------------------------- S18.15d orientation is absolute
  # The window's own selftest runs its ratio loop twice: once on the source it was given, once on a
  # 90-degree copy of that source (a real PNG it writes and deletes itself). !! The two passes arrive
  # in ONE "ratio states:" line (ten entries: five presets on each source), not in two lines - an
  # earlier version required `Count -eq 2` and therefore failed while every entry it then parsed was
  # correct. THIS line is where the numbers are read from; the per-preset "ratio-check" line carries
  # only the verdict, which is why an even earlier version read nothing but zeros.
  # Pinned here, all derived from those two lines:
  #   1. every preset was measured on BOTH orientations;
  #   2. the pixel rectangle of every preset agrees with the target ratio the product itself reported;
  #   3. a fixed-direction preset reports the SAME direction on a landscape and on a portrait source,
  #      while `free` (no ratio at all) and `original` (per image) report the SOURCE's own direction -
  #      that contrast is what makes point 3 a statement about the product and not a tautology;
  #   4. each id is the reciprocal of its counterpart and they differ (9:16 vs 16:9, ...).
  $stateLines = @($ui.Lines | Select-String -Pattern '^.*crop ui selftest: ratio states: ')
  $checkLog = New-Object System.Collections.ArrayList
  # At least one states line, and it must yield a full two-orientation matrix (asserted entry by entry
  # below); the count of lines is only used to say "the summary was printed at all".
  $orientationOk = ($stateLines.Count -ge 1)
  $seenPerId = @{}
  $rectById = @{}
  foreach ($line in $stateLines) {
    $text = [string]$line.Line
    # One "id: <state>" entry per preset, separated by " ; ". The state carries
    # image=WxH ratio=<id> rect=x,y,w,h ratio-ok=... ratio-target=... ratio-actual=...
    foreach ($m in [regex]::Matches($text, '(free|1:1|16:9|9:16|4:3|3:4|original|original-flipped): loaded=.*?(?=(?: ; )|$)')) {
      $entry = $m.Value
      $id = $m.Groups[1].Value
      $srcW = 0; $srcH = 0; $rectW = -1; $rectH = -1; $target = 0.0; $actual = 0.0; $okFlag = $false
      if ($entry -match 'image=(\d+)x(\d+)') { $srcW = [int]$Matches[1]; $srcH = [int]$Matches[2] }
      if ($entry -match 'rect=(-?\d+),(-?\d+),(\d+),(\d+)') { $rectW = [int]$Matches[3]; $rectH = [int]$Matches[4] }
      if ($entry -match 'ratio-ok=(\w+) ratio-target=([0-9.]+) ratio-actual=([0-9.]+)') {
        $okFlag = ($Matches[1] -eq 'True'); $target = [double]$Matches[2]; $actual = [double]$Matches[3]
      }
      $sourceOrientation = if ($srcW -gt $srcH) { 'landscape' } elseif ($srcW -lt $srcH) { 'portrait' } else { 'square' }
      if (-not $seenPerId.ContainsKey($id)) { $seenPerId[$id] = @{} }
      $seenPerId[$id][$sourceOrientation] = $true
      $rectDirection = 'unknown'
      if ($rectH -gt 0) {
        if ($rectW -gt $rectH) { $rectDirection = 'wide' } elseif ($rectW -lt $rectH) { $rectDirection = 'tall' } else { $rectDirection = 'square' }
      }
      # The direction the preset's own constant asks for. `free` and `original` follow the SOURCE
      # (free has no ratio at all; original IS this image's ratio), so their expectation is the
      # source's own direction - that control is what makes "same on both sources" a statement
      # rather than a tautology. `original-flipped` is that direction rotated by 90 degrees.
      $sourceDirection = if ($srcW -gt $srcH) { 'wide' } elseif ($srcW -lt $srcH) { 'tall' } else { 'square' }
      $wantDirection = 'square'
      switch ($id) {
        '16:9' { $wantDirection = 'wide' }
        '4:3'  { $wantDirection = 'wide' }
        '9:16' { $wantDirection = 'tall' }
        '3:4'  { $wantDirection = 'tall' }
        'free' { $wantDirection = $sourceDirection }
        'original' { $wantDirection = $sourceDirection }
        'original-flipped' {
          $wantDirection = if ($sourceDirection -eq 'landscape' -or $sourceDirection -eq 'wide') { 'tall' } else { 'wide' }
        }
      }
      # `free` is the one preset with NO target ratio at all (the product reports ratio-target=0),
      # so "target > 0" cannot be required of it - but the rectangle must still agree with the
      # actual ratio the product reports, and the direction must follow the source.
      $rectOk = ($rectW -gt 0) -and ($rectH -gt 0) -and ($actual -gt 0) -and
                (([math]::Abs(($rectW / $rectH) - $actual) / $actual) -le 0.01)
      $valuesOk = ($actual -gt 0) -and $rectOk -and ($srcW -gt 0) -and ($srcH -gt 0)
      if ($id -ne 'free') { $valuesOk = $valuesOk -and ($target -gt 0) }
      $lineOk = $okFlag -and ($rectDirection -eq $wantDirection) -and $valuesOk
      if (-not $lineOk) { $orientationOk = $false }
      if (-not $rectById.ContainsKey($id)) { $rectById[$id] = @{} }
      $rectById[$id][$sourceOrientation] = $rectDirection
      [void]$checkLog.Add("$id/$sourceOrientation src=${srcW}x${srcH} rectdir=$rectDirection want=$wantDirection target=$([math]::Round($target,4)) actual=$([math]::Round($actual,4)) rectOk=$rectOk ok=$lineOk")
    }
  }
  # 1. and 3.: both orientations measured, and the direction is the same for the fixed-direction ones.
  foreach ($id in @('free', '1:1', '16:9', '4:3', 'original')) {
    $orientations = @()
    if ($seenPerId.ContainsKey($id)) { $orientations = @($seenPerId[$id].Keys | Sort-Object) }
    if ($orientations.Count -ne 2) {
      $orientationOk = $false
      [void]$checkLog.Add("$id measured on $($orientations.Count) orientation(s): $($orientations -join ',')")
    }
  }
  $sameDirectionIds = @('1:1', '16:9', '4:3')
  foreach ($id in $sameDirectionIds) {
    if (-not $rectById.ContainsKey($id)) { $orientationOk = $false; [void]$checkLog.Add("$id has no direction"); continue }
    $dirs = @($rectById[$id].Values | Sort-Object -Unique)
    $same = ($dirs.Count -eq 1)
    if (-not $same) { $orientationOk = $false }
    [void]$checkLog.Add("$id directionsAcrossOrientation=$($dirs -join ',') same=$same")
  }
  # The per-image preset must differ between the two orientations (that is what "per image" means).
  $origDirs = @()
  if ($rectById.ContainsKey('original')) { $origDirs = @($rectById['original'].Values | Sort-Object -Unique) }
  if ($origDirs.Count -ne 2) { $orientationOk = $false }
  [void]$checkLog.Add("original directionsAcrossOrientation=$($origDirs -join ',') (must be two)")
  # 4. each pair is reciprocal and distinct.
  # !! The values come from the DIRECTION-TOGGLE lines, not from the preset states: 9:16 and 3:4 are
  # produced by the toggle and never have a button of their own, so they never appear as a preset
  # state (a first version of this check looked for them there and read -1 for every one of them).
  # !! The matrix table is read HERE, before it is used: an earlier version read it after the first
  # use and reported "matrix ids present=0/8 lines=0" from an empty collection - a parse miss that
  # looked exactly like a product failure.
  $toggleStates = @($ui.Lines | Select-String -Pattern 'crop ui selftest: direction-toggle ')
  $matrixLines = @($ui.Lines | Select-String -Pattern 'crop ui selftest: ratio orientation matrix ')
  $matrixIds = @{}
  foreach ($l in $matrixLines) {
    foreach ($m in [regex]::Matches([string]$l.Line, 'pair (original-flipped|9:16|3:4)=([0-9.]+) vs (\S+)=([0-9.]+)')) {
      $matrixIds[$m.Groups[1].Value] = [double]$m.Groups[2].Value
      $matrixIds[$m.Groups[3].Value] = [double]$m.Groups[4].Value
    }
  }
  $pairLog = New-Object System.Collections.ArrayList
  foreach ($pair in @(@('16:9','9:16'), @('4:3','3:4'), @('original','original-flipped'))) {
    $a = -1.0; $b = -1.0
    foreach ($l in $toggleStates) {
      $t = [string]$l.Line
      $pat = "direction-toggle " + $pair[0] + " .*?before=\S+ after=(\S+) back=\S+ .*?targetBefore=([0-9.]+) targetAfter=([0-9.]+)"
      if ($t -match $pat) {
        if ($Matches[1] -eq $pair[1]) { $a = [double]$Matches[2]; $b = [double]$Matches[3] }
      }
    }
    $pairOk = ($a -gt 0) -and ($b -gt 0) -and ([math]::Abs($a * $b - 1.0) -lt 0.01) -and ([math]::Abs($a - $b) -gt 0.001)
    if (-not $pairOk) { $orientationOk = $false }
    [void]$pairLog.Add("$($pair[0])=$([math]::Round($a,4)) vs $($pair[1])=$([math]::Round($b,4)) reciprocalAndDistinct=$pairOk")
  }
  # ...and the two buttonless fixed-ratio ids are cross-checked in the ORIENTATION MATRIX pair table,
  # which is where all eight ids appear (see S18.15e, which pins that table's shape).
  foreach ($pair in @(@('9:16','16:9'), @('3:4','4:3'), @('original-flipped','original'))) {
    $a = -1.0; $b = -1.0
    if ($matrixIds.ContainsKey($pair[0])) { $a = $matrixIds[$pair[0]] }
    if ($matrixIds.ContainsKey($pair[1])) { $b = $matrixIds[$pair[1]] }
    $pairOk = ($a -gt 0) -and ($b -gt 0) -and ([math]::Abs($a * $b - 1.0) -lt 0.01) -and ([math]::Abs($a - $b) -gt 0.001)
    if (-not $pairOk) { $orientationOk = $false }
    [void]$pairLog.Add("matrix $($pair[0])=$([math]::Round($a,4)) vs $($pair[1])=$([math]::Round($b,4)) reciprocalAndDistinct=$pairOk")
  }
  [void]$pairLog.Add("matrix ids present=$($matrixIds.Keys.Count)/8 lines=$($matrixLines.Count)")
  Check 'S18.15d the direction of every preset is absolute: same direction on a landscape and on a portrait source, the pixel rectangle agrees with the reported target ratio, and each id is the reciprocal of its counterpart' `
    $orientationOk `
    (($checkLog -join ' | ') + ' || ' + ($pairLog -join ' | '))

  # The ORIENTATION MATRIX line covers all eight ids (9:16 and 3:4 have no button, so they have no
  # preset state and no toggle line - the matrix and the pair table are the only places they appear).
  # This is a check of its own so a parse failure here cannot silently decide S18.15d's verdict.
  $matrixLines = @($ui.Lines | Select-String -Pattern 'crop ui selftest: ratio orientation matrix ')
  $matrixOk = ($matrixLines.Count -ge 1)
  $matrixText = ''
  foreach ($l in $matrixLines) {
    $matrixText = [string]$l.Line
    foreach ($m in [regex]::Matches($matrixText, 'pair (original-flipped|9:16|3:4)=([0-9.]+) vs (\S+)=([0-9.]+) reciprocalAndDistinct=(\w+)')) {
      $pA = [double]$m.Groups[2].Value; $pB = [double]$m.Groups[4].Value; $rec = ($m.Groups[5].Value -eq 'True')
      if (-not ($rec -and ([math]::Abs($pA * $pB - 1.0) -lt 0.01))) { $matrixOk = $false }
    }
    foreach ($m in [regex]::Matches($matrixText, '(free|1:1|16:9|4:3|original)\(landscape/portrait\) (\w+)/(\w+) fixedDirection=(\w+) same=(\w+) ok=(\w+)')) {
      $fixed = ($m.Groups[4].Value -eq 'True'); $same = ($m.Groups[5].Value -eq 'True'); $ok = ($m.Groups[6].Value -eq 'True')
      # fixedDirection => the two orientations must give the same direction; !fixedDirection => they must differ.
      if (-not ($ok -and ($same -eq $fixed))) { $matrixOk = $false }
      if (($m.Groups[2].Value -eq $m.Groups[3].Value) -ne $same) { $matrixOk = $false }
    }
  }
  $matrixPairCount = @([regex]::Matches($matrixText, 'pair ')).Count
  $matrixPresetCount = @([regex]::Matches($matrixText, '\(landscape/portrait\)')).Count
  Check 'S18.15e the orientation matrix itself is well formed: five presets compared across both orientations, and the three reciprocal pairs (including original-flipped) declared reciprocals of each other' `
    ($matrixOk -and ($matrixPairCount -eq 3) -and ($matrixPresetCount -eq 5)) `
    "matrixLines=$($matrixLines.Count) presets=$matrixPresetCount pairs=$matrixPairCount line='$matrixText'"

  # The WINDOW's own verdict on the same property (it computes it, this suite only reads it).
  Check 'S18.15e2 the window itself reports the orientation matrix as ok with both source orientations measured' `
    ((Test-Log $ui.Lines 'ratio orientation ok=True portraitPassRan=True') -and
     (Test-Log $ui.Lines 'reopen on the rotated source:.*opened=True')) `
    "matrix='$(Get-LogLine $ui.Lines 'ratio orientation matrix')' verdict='$(Get-LogLine $ui.Lines 'ratio orientation ok=')'"

  # ---------------------------------------------------------------- S18.15f the direction toggle
  # The design (ruled 2026-10-07): the button row keeps FIVE ratio buttons, and one extra button
  # flips the SELECTED ratio to its other direction. The three properties pinned here are the ones
  # that make it honest:
  #   * free and 1:1 have no direction => the button is DISABLED (and the text says so; a silently
  #     dead button is what this avoids);
  #   * 16:9 / 4:3 / original flip for real: a different ratio id, a different direction on screen,
  #     and flipping twice returns to where it started;
  #   * the selected ratio button's LABEL equals the label of the current ratio id - otherwise the
  #     screen would say "16:9" while cropping 9:16.
  $toggleLines = @($ui.Lines | Select-String -Pattern 'crop ui selftest: direction-toggle ')
  $toggleLog = New-Object System.Collections.ArrayList
  $toggleOk = $true
  $labelLines = @($ui.Lines | Select-String -Pattern 'crop ui selftest: ratio-button-label ')
  $labelOk = $true
  foreach ($line in $labelLines) {
    if ([string]$line.Line -notmatch 'ok=True') { $labelOk = $false }
    [void]$toggleLog.Add((( [string]$line.Line) -replace '^.*ratio-button-label ', 'label '))
  }
  $labelCounts = @{}
  foreach ($id in @('free', '1:1', '16:9', '4:3', 'original')) {
    $labelCounts[$id] = @($labelLines | Where-Object { [string]$_.Line -match "ratio-button-label preset=$([regex]::Escape($id)) " }).Count
  }
  # Two passes (landscape + portrait source) => every button must have been labelled twice, and the
  # label invariant must have held in both.
  foreach ($id in $labelCounts.Keys) {
    if ($labelCounts[$id] -ne 2) { $labelOk = $false }
    [void]$toggleLog.Add("labelPreset $id count=$($labelCounts[$id])")
  }

  $toggleCount = @{}
  foreach ($line in $toggleLines) {
    $text = [string]$line.Line
    $id = ''; $before = ''; $after = ''; $back = ''; $dirBefore = ''; $dirAfter = ''; $enabled = ''; $lineOk = $false
    if ($text -match 'direction-toggle (\S+) source=\d+x\d+ before=(\S+) after=(\S+) back=(\S+)') {
      $id = $Matches[1]; $before = $Matches[2]; $after = $Matches[3]; $back = $Matches[4]
    } else { $toggleOk = $false; [void]$toggleLog.Add("unparsable: $text"); continue }
    if ($text -match 'directionBefore=(\S+) directionAfter=(\S+)') { $dirBefore = $Matches[1]; $dirAfter = $Matches[2] }
    if ($text -match 'enabled=(\w+)') { $enabled = $Matches[1] }
    if ($text -match 'ok=(\w+)') { $lineOk = ($Matches[1] -eq 'True') }
    if (-not $toggleCount.ContainsKey($id)) { $toggleCount[$id] = 0 }
    $toggleCount[$id] = $toggleCount[$id] + 1

    # Which ids have a counterpart, derived from the product's declared set (a '3:4' next to '4:3'
    # is the same statement as '9:16' next to '16:9'): free and 1:1 are the only two WITHOUT one.
    $hasCounterpart = (($id -ne 'free') -and ($id -ne '1:1'))
    $caseOk = $false
    if (-not $hasCounterpart) {
      $caseOk = ($enabled -eq 'False') -and ($before -eq $id) -and ($after -eq $id) -and ($back -eq $id) -and $lineOk
    } else {
      $caseOk = ($enabled -eq 'True') -and ($before -ne $after) -and ($back -eq $before) -and
                ($dirBefore -ne $dirAfter) -and $lineOk
    }
    if (-not $caseOk) { $toggleOk = $false }
    [void]$toggleLog.Add("$id before=$before after=$after back=$back dir=$dirBefore->$dirAfter enabled=$enabled counterpart=$hasCounterpart ok=$caseOk")
  }
  Check 'S18.15f the direction toggle flips 16:9/4:3/original to a DIFFERENT id and direction and back again, and is DISABLED (not silently dead) for free and 1:1' `
    ($toggleOk -and ($toggleLines.Count -ge 5) -and ($toggleCount.Keys.Count -eq 5)) `
    ($toggleLog -join ' | ')

  Check 'S18.15g the selected ratio button never lies: its label equals the label of the current ratio id, on every preset and both source orientations' `
    ($labelOk -and ($labelLines.Count -ge 10)) `
    (($labelLines | ForEach-Object { ([string]$_.Line).Trim() }) -join ' || ')

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

  # The button row: FIVE ratio buttons + ONE direction button (+ the "ratio" caption and the hint).
  # A scrollbar check does NOT cover this: a horizontal StackPanel never grows a scrollbar, it CLIPS.
  # So the window measures itself and reports the numbers; every expectation below is derived from
  # those numbers (no pixel count is written into this script).
  $rowLine = Get-LogLine $ui.Lines 'ratio-button-row '
  $rowParsed = $false
  $rowWidth = 0.0; $rowConstraint = 0.0; $rowMaxRight = 0.0; $rowChildren = 0; $rowWindow = 0.0
  $rowFitsFlag = $false
  if ($rowLine -match 'children=(\d+).*rowWidth=([0-9.]+) constraint=([0-9.]+) maxChildRight=([0-9.]+) windowWidth=([0-9.]+).*fits=(\w+) ok=(\w+)') {
    $rowChildren = [int]$Matches[1]; $rowWidth = [double]$Matches[2]; $rowConstraint = [double]$Matches[3]
    $rowMaxRight = [double]$Matches[4]; $rowWindow = [double]$Matches[5]
    $rowFitsFlag = ($Matches[6] -eq 'True'); $rowOkFlag = ($Matches[7] -eq 'True')
    $rowParsed = $true
  }
  # 5 buttons + the caption + the direction button + the hint = 8 children, derived from the count of
  # ids that actually have a button (the three buttonless ids are the direction toggle's output).
  $expectedChildren = ($declaredSet.Count - 3) + 3
  $rowGeometryOk = $rowParsed -and $rowFitsFlag -and ($rowChildren -eq $expectedChildren) -and
                   ($rowWidth -gt 0) -and ($rowMaxRight -le $rowConstraint + 0.5) -and
                   ($rowConstraint -le ($rowWindow - 40) + 0.5)
  Check 'S18.16c five ratio buttons plus the direction button all fit inside the window: nothing overlaps, nothing is clipped, and every child sits inside the row it was given' `
    $rowGeometryOk `
    "children=$rowChildren (expected $expectedChildren) rowWidth=$rowWidth constraint=$rowConstraint maxChildRight=$rowMaxRight windowWidth=$rowWindow fits=$rowFitsFlag line='$rowLine'"

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
                  'newBoxDrag=True (down=True reverse=True margin=True)',
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

# =============================================================================
#  S19 READ QR (tool `read-qr`, the FIFTH tool sector; spec 9.4)
#
#  WHAT THIS PROVES, AND WHAT IT DOES NOT
#   1. THE TEXT IS RE-DERIVED FROM THE PRODUCT'S OWN OUTPUT. `ok=True` only says the engine
#      thinks it succeeded. S19.01 therefore parses the decoded text OUT of the `--read-qr`
#      stdout line and compares it character for character against the known payload. Nothing
#      here trusts a boolean.
#   2. NOTHING IS WRITTEN. This is the only tool whose product is text rather than a file, so
#      "no file appeared" is asserted on a whole-directory snapshot (name/size/mtime before and
#      after), not on "the output path is empty".
#   3. THE SOURCE IS READ-ONLY (spec 5): sha256 + length + mtime before and after.
#   4. THE SECOND ROUND IS NOT DECORATION. `qr-small-in-scene-160.png` is decoded by the
#      full-size round after the first pass fails - and the round is read from the PRODUCT's log
#      line, so a product that only ever ran one pass would fail this check.
#   5. THE FIXTURES ARE COMMITTED (see fixtures/qr/README.md) and they were ENCODED WITH ZXing
#      ITSELF - the same library the product decodes with. That makes them a valid fixture for
#      the DECODE PATH, and it does NOT license any claim about real-world photographs.
#
#  NOT covered here:
#   - the wheel sector / the text window / "open in browser" are NOT wired yet (the UI cut is a
#     separate slice). What is checked is the catalog entry (S19.07) and the engine + headless
#     path.
#   - the worker dispatch arm for op=read-qr is NOT exercised at runtime by this suite: nothing
#     on the command line can ask the WORKER for a read-qr job today, and this suite refuses to
#     invent one (see S19.06, which checks the dispatcher and the unknown-op message against the
#     source instead).
#   - recognition of photographed / printed / perspective-distorted codes: UNMEASURED.
# =============================================================================

# The output of one `--read-qr` run.
#
# !! The EVIDENCE comes from STDERR, not from a `--log` file, and that is not a workaround: the
# headless diagnostics of this product write to the EVIDENCE channel (`Evidence.Line`), which
# mirrors every line to stderr (see Evidence.cs: `if (!_quiet) Console.Error.WriteLine(...)`).
# Measured 2026-10-07: this process also creates a resident hit layer on the way (the settings log
# line says log=<%LOCALAPPDATA%>\Confern\logs\confern.log, NOT the --log path), so there is no
# per-run log file for this switch - reading stderr is the only honest reader. `--quiet` is
# deliberately NOT passed, because it would silence exactly the lines under assertion.
function Invoke-ReadQr([string]$name, [string]$fixture) {
  $d = Join-Path $Work "qr-$name"
  New-Item -ItemType Directory -Force -Path $d | Out-Null
  $src = Join-Path $d (Split-Path -Leaf $fixture)
  Copy-Item -LiteralPath $fixture -Destination $src -Force

  $beforeNames = @(Get-ChildItem -LiteralPath $d -File | Sort-Object Name |
                   ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
  $sha = Get-Sha $src
  $len = (Get-Item -LiteralPath $src).Length
  $mtime = (Get-Item -LiteralPath $src).LastWriteTimeUtc

  $r = Invoke-ConfernStdout "read-qr-$name" @('--read-qr', $src, '--no-autostart')
  $afterNames = @(Get-ChildItem -LiteralPath $d -File | Sort-Object Name |
                  ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })

  return [pscustomobject]@{
    Name        = $name
    Exit        = $r.Exit
    Stdout      = $r.Lines
    Evidence2   = (Get-LogLines (Join-Path $Work "read-qr-$name.stderr.txt"))
    Text        = (Get-QrText $r.Lines)
    DirSame     = (($beforeNames -join "`n") -eq ($afterNames -join "`n"))
    FileCount   = $afterNames.Count
    Sha         = $sha
    ShaAfter    = (Get-Sha $src)
    Length      = $len
    LengthAfter = (Get-Item -LiteralPath $src).Length
    Mtime       = $mtime
    MtimeAfter  = (Get-Item -LiteralPath $src).LastWriteTimeUtc
  }
}

# Re-derive the decoded text from the product's stdout line. `tool read-qr: text=<TEXT> chars=<N>`
# -- the text runs to the LAST ` chars=` (the payload could contain " chars=" in principle, so the
# pattern is anchored on the trailing count instead of on the first space).
function Get-QrText([string[]]$lines) {
  foreach ($line in $lines) {
    $m = [regex]::Match([string]$line, '^(?:\d\d:\d\d:\d\d\.\d+ )?tool read-qr: text=(.*) chars=(\d+)$')
    if ($m.Success) { return $m.Groups[1].Value }
  }
  return ''
}

function Invoke-S19 {
  Write-Host "=== S19 read-qr tool: text, the two-stage decode, and a negative fixture ==="

  $repo = Split-Path -Parent $PSScriptRoot
  $fdir = Join-Path $repo 'fixtures\qr'
  $payload = 'Confern QR fixture 2026-10-07 / payload-v1'
  $fClean = Join-Path $fdir 'qr-clean-512x512.png'
  $fScaled = Join-Path $fdir 'qr-scaled-256x256.png'
  $fJpeg = Join-Path $fdir 'qr-scaled-jpeg-q40-512x512.jpg'
  $fBig = Join-Path $fdir 'qr-big-2400x2400.png'
  $fTiny = Join-Path $fdir 'qr-small-in-scene-160.png'
  $fNone = Join-Path $fdir 'no-qr-512x384.png'
  $missing = @()
  foreach ($f in @($fClean, $fScaled, $fJpeg, $fBig, $fTiny, $fNone)) {
    if (-not (Test-Path -LiteralPath $f)) { $missing += (Split-Path -Leaf $f) }
  }
  if ($missing.Count -gt 0) {
    Inconclusive "QR fixtures missing from $fdir ($($missing -join ', ')) - they are committed, so a missing one means the checkout is broken"
  }

  # ---------------------------------------------------------------- S19.01 the clean code
  $clean = Invoke-ReadQr 'clean' $fClean
  Check 'S19.01 the clean QR decodes to EXACTLY the known payload (re-derived from the product stdout, not from ok=True)' `
    (($clean.Exit -eq 0) -and ($clean.Text -ceq $payload) -and
     (Test-Log $clean.Evidence2 'tool read-qr: ok=True error=None')) `
    "exit=$($clean.Exit) chars=$($clean.Text.Length) identical=$($clean.Text -ceq $payload) text='$($clean.Text)'"

  # ---------------------------------------------------------------- S19.02 scaled + JPEG
  # The same payload scaled to 256x256 and re-encoded as a lossy JPEG at quality 40. Both have to
  # come back character for character: "it decoded something" is not the assertion.
  $scaled = Invoke-ReadQr 'scaled' $fScaled
  $jpeg = Invoke-ReadQr 'jpeg' $fJpeg
  Check 'S19.02 the scaled (256x256) and the JPEG-quality-40 version of the same code decode to the same 42 characters' `
    (($scaled.Exit -eq 0) -and ($scaled.Text -ceq $payload) -and
     ($jpeg.Exit -eq 0) -and ($jpeg.Text -ceq $payload)) `
    "scaled exit=$($scaled.Exit) identical=$($scaled.Text -ceq $payload) | jpeg exit=$($jpeg.Exit) identical=$($jpeg.Text -ceq $payload)"

  # ---------------------------------------------------------------- S19.03 the second round
  # THE TWO-STAGE STRATEGY IS MEASURED, NOT ASSUMED. A 160 px code inside a 3000x2250 picture is
  # too coarse after decoding at 1600 px, so the product has to fall back to the full size - and
  # the round that answered is on the product's own log line.
  $tiny = Invoke-ReadQr 'second-round' $fTiny
  $firstPassNoQr = Test-Log $tiny.Evidence2 'read-qr: first-pass \d+x\d+ in \d+ ms -> \(no qr\)'
  $fullSizeHit = Test-Log $tiny.Evidence2 'read-qr: full-size 3000x2250 in \d+ ms -> 42 chars'
  Check 'S19.03 a code too small for the first pass is still found by the full-size round (both rounds are on the log line)' `
    (($tiny.Exit -eq 0) -and ($tiny.Text -ceq $payload) -and $firstPassNoQr -and $fullSizeHit) `
    ("exit=$($tiny.Exit) firstPassMissed=$firstPassNoQr fullSizeFound=$fullSizeHit line='" +
     (Get-LogLine $tiny.Evidence2 'read-qr: full-size') + "'")

  # ---------------------------------------------------------------- S19.03b the first pass pays off
  # The SAME code at 2400x2400 is answered by the first pass (natively decoded at 1600 px), so the
  # cheap round is not dead code either: it is the round that answers for the ordinary big picture.
  $big = Invoke-ReadQr 'big-first-pass' $fBig
  Check 'S19.03b a 2400x2400 picture is answered by the first pass at 1600 px, without decoding the full size' `
    (($big.Exit -eq 0) -and ($big.Text -ceq $payload) -and
     (Test-Log $big.Evidence2 'read-qr: first-pass 1600x1600 in \d+ ms -> 42 chars') -and
     (-not (Test-Log $big.Evidence2 'read-qr: full-size'))) `
    "exit=$($big.Exit) line='$(Get-LogLine $big.Evidence2 'read-qr: first-pass')' fullSizeRan=$(Test-Log $big.Evidence2 'read-qr: full-size')"

  # ---------------------------------------------------------------- S19.04 the negative fixture
  # A picture with no QR must fail BY NAME, without an exception and without producing anything.
  # The directory snapshot is the load-bearing half: a tool that "helpfully" wrote an empty output
  # would pass an assertion that only looked at the exit code.
  $none = Invoke-ReadQr 'no-qr' $fNone
  Check 'S19.04 a picture with no QR fails BY NAME (QrNotFound), and writes no file at all' `
    (($none.Exit -eq 3) -and (Test-Log $none.Evidence2 'QrNotFound') -and
     (Test-Log $none.Evidence2 'no file is written') -and $none.DirSame -and ($none.FileCount -eq 1)) `
    "exit=$($none.Exit) dirUnchanged=$($none.DirSame) filesInDir=$($none.FileCount) line='$(Get-LogLine $none.Evidence2 'tool read-qr: ok=')'"

  # ---------------------------------------------------------------- S19.05 sources are read-only
  # Spec 5, the first rule. read-qr is the one op that never writes, so this is the only place the
  # promise "it reads and nothing else" can go red.
  $allUntouched = $clean.DirSame -and $scaled.DirSame -and $jpeg.DirSame -and
                  $big.DirSame -and $tiny.DirSame -and $none.DirSame -and
                  ($clean.Sha -eq $clean.ShaAfter) -and ($scaled.Sha -eq $scaled.ShaAfter) -and
                  ($jpeg.Sha -eq $jpeg.ShaAfter) -and ($big.Sha -eq $big.ShaAfter) -and
                  ($tiny.Sha -eq $tiny.ShaAfter) -and ($none.Sha -eq $none.ShaAfter) -and
                  ($clean.LengthAfter -eq $clean.Length) -and ($jpeg.LengthAfter -eq $jpeg.Length) -and
                  ($clean.MtimeAfter -eq $clean.Mtime) -and ($jpeg.MtimeAfter -eq $jpeg.Mtime)
  Check 'S19.05 all six runs: the source file AND the whole folder are byte-for-byte unchanged (sha256 + length + mtime, spec 5)' `
    $allUntouched `
    "clean=$($clean.Sha.Substring(0,12))->$($clean.ShaAfter.Substring(0,12)) jpeg=$($jpeg.Sha.Substring(0,12))->$($jpeg.ShaAfter.Substring(0,12)) filesPerDir=$($clean.FileCount)/$($scaled.FileCount)/$($big.FileCount)/$($tiny.FileCount)/$($none.FileCount)"

  # ---------------------------------------------------------------- S19.06 unknown op is still rejected
  # The regression guard for the edit this slice makes to WorkerServer: a new op was added to the
  # dispatch switch, and the unknown-op message has to keep naming the FULL list (it is the
  # observable evidence behind "an old worker refuses instead of silently doing the wrong thing").
  # Nothing on the command line can send an unknown op to the worker today, so this reads the
  # SOURCE: the switch arms and the reject message must list the same ops, in the same order.
  # That check goes red exactly when someone adds an op and forgets the message (or breaks the
  # reject path's list), which is the failure this slice could plausibly introduce.
  $workerSrc = Join-Path $repo 'src\Sidecar\Convert\WorkerServer.cs'
  $workerLines = @()
  if (Test-Path -LiteralPath $workerSrc) {
    $workerLines = @(Get-Content -LiteralPath $workerSrc -Encoding UTF8)
  }
  $dispatchOps = @(); $rejectOps = @()
  $inSwitch = $false
  foreach ($line in $workerLines) {
    $t = ([string]$line).Trim()
    if ($t -match '^ConversionResult result = op switch') { $inSwitch = $true; continue }
    if ($inSwitch) {
      foreach ($m in [regex]::Matches($t, 'WorkerProtocol\.(Op[A-Za-z0-9]+) =>')) {
        $dispatchOps += $m.Groups[1].Value
      }
      if ($t -match '^\};') { $inSwitch = $false }
    }
    # The message is built from $"{WorkerProtocol.OpX}/{WorkerProtocol.OpY}" - collect every one
    # that appears on a line inside RejectUnknownOp (its own body mentions WorkerProtocol only
    # there, so a plain scan of the two interpolated lines is enough).
    if ($t -match '\$"\{WorkerProtocol\.Op') {
      foreach ($m in [regex]::Matches($t, 'WorkerProtocol\.(Op[A-Za-z0-9]+)')) {
        $rejectOps += $m.Groups[1].Value
      }
    }
  }
  $sameList = (($dispatchOps -join '/') -eq ($rejectOps -join '/'))
  Check 'S19.06 the unknown-op rejection still names exactly the ops the dispatch switch handles, in order (nothing broke)' `
    (($dispatchOps.Count -ge 6) -and ($rejectOps.Count -eq $dispatchOps.Count) -and $sameList -and
     ($rejectOps -contains 'OpReadQr') -and (Test-Log $workerLines 'RejectUnknownOp')) `
    "dispatch=($($dispatchOps -join '/')) reject=($($rejectOps -join '/')) same=$sameList"

  # ---------------------------------------------------------------- S19.07 the catalog entry
  # The catalog is the only owner of "which tools exist" (the wheel draws whatever it says). This
  # slice adds the entry only: the sector, the browser and the text window are a later cut.
  $tl = Invoke-Confern 'tools-list-qr' @('--tools-list')
  Check 'S19.07 the tool catalog now lists five tools and read-qr is the fifth, labelled READ QR' `
    (($tl.Exit -eq 0) -and
     (Test-Log $tl.Lines 'tool list: count=5 tools=\[rotate90,strip-metadata,compress,crop,read-qr\]') -and
     (Test-Log $tl.Lines "tool list: 4 id=read-qr label='READ QR'")) `
    "exit=$($tl.Exit) line='$(Get-LogLine $tl.Lines 'tool list: count=')' fifth='$(Get-LogLine $tl.Lines 'tool list: 4 ')'"

  # ---------------------------------------------------------------- S19.08 documented
  # Every switch this product exposes is documented in --help; a headless diagnostic nobody can
  # find is not a diagnostic.
  $help = Invoke-ConfernStdout 'help-qr' @('--help')
  Check 'S19.08 --read-qr is documented in --help (it is not a secret switch)' `
    (Test-Log $help.Lines '--read-qr <path>') `
    "help lines=$($help.Lines.Count) line='$(Get-LogLine $help.Lines '--read-qr')'"

  # ---------------------------------------------------------------- S19.09 the op is declared once
  # The declaration order of the Op* constants is the order the dispatch switch and the reject
  # message use, so the three lists are kept in one order on purpose; this pins the declaration
  # side of it so a stray edit cannot reorder them silently.
  $opsInWorkerProtocol = @()
  $protoLines = @($workerLines | ForEach-Object { [string]$_ })
  foreach ($line in $protoLines) {
    $t = ([string]$line).Trim()
    # The modifiers are optional in the pattern on purpose: `internal const` is what these are
    # today, and a pattern that only accepted `public const` would silently read an EMPTY list -
    # which is exactly how this check first failed (measured 2026-10-07: declared=() while the
    # dispatch list was complete, i.e. the parser was wrong, not the product).
    $m = [regex]::Match($t, '^(?:public|internal|private)?\s*(?:const|static readonly)\s+string\s+(Op[A-Za-z0-9]+)\s*=')
    if ($m.Success) { $opsInWorkerProtocol += $m.Groups[1].Value }
  }
  Check 'S19.09 WorkerProtocol declares the six ops in the same order the dispatcher and the reject message use' `
    ((($opsInWorkerProtocol -join '/') -eq 'OpConvert/OpRotate90/OpStripMetadata/OpCompress/OpCrop/OpReadQr') -and
     (($opsInWorkerProtocol -join '/') -eq ($dispatchOps -join '/'))) `
    "declared=($($opsInWorkerProtocol -join '/')) dispatch=($($dispatchOps -join '/'))"
}

# ------------------------------------------------------------------ main
$sections = @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
Write-Host "verify-m3d: exe=$Exe sections=$($sections -join ',') logdir=$LogDir fault='$FaultInjection'"
if ($FaultInjection -ne '') { Write-Host "verify-m3d: $(Get-FaultNote)" }

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

foreach ($s in $sections) {
  switch ($s) {
    'S18' { Expect 39; Invoke-S18 }
    'S19' { Expect 10; Invoke-S19 }
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
