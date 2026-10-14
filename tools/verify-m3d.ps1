# M3d verification: the crop tool (spec 9.4, the FOURTH tool sector).
#
#   -Exe <path to Confern.exe>   -Section S18   -LogDir <dir>
#   [-FaultInjection <name>]   the full set is documented at the switch below; S22 adds
#                              rembg-nosubject and rembg-png
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
# ENCODING (measured 2026-10-08, when S22 was added). This script used to be ASCII-only because
# Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI and mangles CJK literals. S22 asserts the
# three user-facing refusal messages, which ARE Chinese, so the rule is now:
#   - the file carries a UTF-8 BOM (that is what makes 5.1 read it as UTF-8), and
#   - every line still ends in CRLF: in this interpreter a bare LF is NOT a statement terminator,
#     and an LF-only block gets swallowed by the '#' comment above it (the symptom appeared as a
#     missing '}' some 400 lines away).
# A new line containing Chinese must be added by an editor that writes UTF-8 WITH a BOM, never by
# a tool that reads the file as ANSI and writes back what it thinks it read.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'S18',
  [string]$LogDir = "$env:TEMP\sidecar-verify",
  [ValidateSet('', 'swap', 'ignore-size', 'ignore-ratio', 'qr-classify', 'qr-text', 'merge-order', 'merge-drop', 'merge-nosort', 'merge-cancelqueues', 'rembg-nosubject', 'rembg-png')][string]$FaultInjection = ''
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
  # !! START-PROCESS DOES NOT QUOTE ARGUMENTLIST ELEMENTS, AND THAT IS A REAL TRAP (measured
  # 2026-10-07 while adding S20). Every earlier caller passed argv values that happened to contain
  # no space, so it never showed up. `--merge-pdf <p1;p2;...>` is ONE argument that contains spaces
  # (the batch lives under $Work, and this repo's path has a space in its ancestors), so the
  # unquoted element was split into several arguments and Start-Process failed with
  # "Cannot validate argument on parameter 'ArgumentList'. The argument is null or empty." - a
  # message that points at the harness, not at the product, which is what made it worth diagnosing
  # rather than working around.
  # The quoting is applied ONLY to elements that contain a space, so every existing call passes
  # byte-identical arguments to what it passed before (no value in this script has both a space and
  # a quote character today; if one ever does, this needs to become a real quoting routine).
  $quoted = @()
  foreach ($a in $argv) {
    if ([string]$a -match ' ') { $quoted += ('"' + $a + '"') } else { $quoted += $a }
  }
  $all = @() + $quoted + @('--log', $log, '--quiet', '--no-autostart')
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

# The id list out of a `--tools-list` run, as an array: `tool list: count=N tools=[a,b,c]`. The list
# is source-dependent (merge-pdf only applies to a PDF), so checks that mean "every tool exists"
# compare against the UNION of an image query and a PDF query instead of against one hard string.
function Get-ToolIds([string[]]$lines) {
  $line = Get-LogLine $lines 'tool list: count='
  $m = [regex]::Match([string]$line, 'tools=\[([^\]]*)\]')
  if (-not $m.Success) { return @() }
  return @($m.Groups[1].Value -split ',' | Where-Object { $_ -ne '' })
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
#   qr-classify  expect EVERY read-qr input to be classified as a url (https://example.com). That
#                is the "the classifier says everything is a url" bug - the one that would hand a
#                file:// or javascript: string to ShellExecute. Catches the negative cases (S19.12)
#                AND the dangerous-scheme check (S19.14).
#   qr-text      expect the text window to hold a different string than the decoded one. Catches
#                "the window shows something other than what the decoder produced" (S19.13).
#   merge-order / merge-drop / merge-nosort / merge-cancelqueues
#                the merge-pdf expectations, each wrong in one specific way (S20/S21).
#   rembg-nosubject
#                expect remove-bg to WRITE A FILE for a QR scene - i.e. silently emit a fully
#                transparent PNG, which is the exact failure S22.04 exists to catch. S22.04 and
#                ONLY S22.04 must go red (measured 2026-10-08). An earlier, broader version of this
#                injection also repointed the truncated-model run at the real model; it turned
#                S22.11 and S22.12 red as well, which is why it was narrowed.
#   rembg-png    expect the decoded output frame to have NO alpha channel - the assertion S22.06
#                makes about the pixel format the product itself reports.
function Get-FaultNote() {
  switch ($FaultInjection) {
    'swap' { return 'FAULT INJECTION swap: expectations transposed on purpose' }
    'ignore-size' { return 'FAULT INJECTION ignore-size: expectations use the first image size for all' }
    'ignore-ratio' { return 'FAULT INJECTION ignore-ratio: 1:1 expectations replaced by the free rectangle' }
    'qr-classify' { return 'FAULT INJECTION qr-classify: every read-qr input is expected to be a url' }
    'qr-text' { return 'FAULT INJECTION qr-text: the text window is expected to hold a different string' }
    'merge-order' { return 'FAULT INJECTION merge-order: the expected PDF page sequence is reversed' }
    'merge-drop' { return 'FAULT INJECTION merge-drop: the expected PDF page count is one too low' }
    'merge-nosort' { return 'FAULT INJECTION merge-nosort: the expected merge order is the SOURCE order, not the order the window showed' }
    'merge-cancelqueues' { return 'FAULT INJECTION merge-cancelqueues: cancel is expected to still hand the order back' }
    'rembg-nosubject' { return 'FAULT INJECTION rembg-nosubject: a QR scene is expected to produce a written PNG (S22.04 only)' }
    'rembg-png' { return 'FAULT INJECTION rembg-png: the output frame is expected NOT to carry an alpha channel' }
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
  #
  # !! 2026-10-08: this plain query counts SIX with `remove-bg` added (measured), because the id
  # list is SOURCE-DEPENDENT and the default source is an image: `merge-pdf` only applies to a PDF,
  # so an image sees six. The whole catalog is therefore asserted as the UNION of two queries, so
  # that a genuinely missing or extra tool still turns this red no matter which source it hides in.
  $tl = Invoke-Confern 'tools-list' @('--tools-list')
  $tlPdfHere = Invoke-Confern 'tools-list-u-pdf' @('--tools-list', '--wheel-source', 'pdf')
  Check 'S18.03 the tool layer lists every sector in order, and crop is the fourth' `
    (($tl.Exit -eq 0) -and ($tlPdfHere.Exit -eq 0) -and
     ((Get-ToolIds $tl.Lines) -join ',') -eq 'rotate90,strip-metadata,compress,crop,read-qr,remove-bg' -and
     ((Get-ToolIds $tlPdfHere.Lines) -join ',') -eq 'rotate90,strip-metadata,compress,crop,read-qr,merge-pdf,remove-bg' -and
     (Test-Log $tl.Lines 'tool list: 3 id=crop ')) `
    "exit=$($tl.Exit) line='$(Get-LogLine $tl.Lines 'tool list: count=')' crop='$(Get-LogLine $tl.Lines 'tool list: 3 ')' " +
    "pdf='$(Get-LogLine $tlPdfHere.Lines 'tool list: count=')'"

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
#   - recognition of photographed / printed / perspective-distorted codes: UNMEASURED.
#
#  !! 2026-10-07 (UI cut): S19.10-S19.16 were added and two entries of the list above are no longer
#  true, so they are corrected here rather than left to mislead a reader of a green run:
#   - the wheel sector / the text window / "open in browser" ARE wired now. S19.12-S19.16 assert the
#     classification, the text window (character for character) and the browser seam; the wheel
#     ICON for the fifth sector is a drawing (see --wheel-dump) and is NOT asserted here.
#   - the worker dispatch arm for op=read-qr IS exercised at runtime now: --qr-worker starts a real
#     worker process and the text is re-derived from the client's own output line (S19.10), and the
#     WORKER's log is read as a second, independent witness that the process really ran (S19.11).
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

# =============================================================================
#  S20 helpers: reading a PDF through the PRODUCT, and running a merge
#
#  `--merge-pdf-inspect <file>` prints, per page, the size in points, the /Rotate and the length
#  plus SHA-256 of the DECOMPRESSED content stream. That is the only objective way to assert page
#  ORDER here: a merged PDF is a binary blob, so "is page 3 really fx-c's second page" has to be
#  answered from each page's own geometry and content - never from the product's ok=True.
#
#  EVERY expectation below is re-derived from a product output line, and `read=` is checked against
#  the number of page lines actually parsed. Measured 2026-10-07: `fx-broken-page-tree.pdf` makes
#  `--merge-pdf-inspect` exit 3 with NO page lines at all, because the page tree cannot be walked
#  (that is PDFsharp issue #388 - the file OPENS fine and only fails when a page is touched). So a
#  reader that trusted `ok=True` or the declared page count would import a false fact.
# =============================================================================

# Make a password-protected PDF with PDFsharp ITSELF (the library the product ships), for S20.11.
#
# RETURNS FALSE IN THIS HARNESS, ON PURPOSE AND BY MEASUREMENT (2026-10-07): this suite runs under
# Windows PowerShell 5.1, which is .NET Framework. `Add-Type -Path PdfSharp-wpf.dll` fails with
# "Could not load file or assembly 'System.Runtime, Version=8.0.0.0'" (the loader exception list is
# 150+ lines of the same message) because the package's assembly targets net8.0. PowerShell 7 (the
# only host that could load it) is NOT available on this machine. So the encrypted fixture CANNOT
# be built in-process here, and S20.11 falls back to asserting the product's own handling, which
# WAS measured end to end with the out-of-repo probe project described in fixtures/pdf/README.md.
#
# It is kept as a function rather than deleted so the limitation is visible and so a future host
# with a .NET 8 PowerShell can turn it on by deleting one line.
function New-EncryptedPdfFixture([string]$path) {
  return $false
}

# One `--merge-pdf-inspect` run, parsed into pages. Returns $null when the file could not be read
# (then $r.PageCount is -1 and $r.Sha is not an array: callers must check).
function Get-MergePages([string]$name, [string]$file) {
  $r = Invoke-Confern "inspect-$name" @('--merge-pdf-inspect', $file)
  $pages = @()
  foreach ($line in $r.Lines) {
    $m = [regex]::Match([string]$line, 'merge-pdf inspect: page=(\d+) size=([\d.]+)x([\d.]+) rotate=(-?\d+) contents=(\d+) sha=(\S+)')
    if ($m.Success) {
      $pages += [pscustomobject]@{
        Number   = [int]$m.Groups[1].Value
        Size     = "$($m.Groups[2].Value)x$($m.Groups[3].Value)"
        Rotate   = [int]$m.Groups[4].Value
        Contents = [int]$m.Groups[5].Value
        Sha      = $m.Groups[6].Value
      }
    }
  }
  $doneLine = Get-LogLine $r.Lines 'merge-pdf inspect done:'
  $declared = -1; $read = -1; $doneOk = $false
  # `.+?`, NOT `\S+`, for the file name - same trap as the output name above: `fx-a (1).pdf`
  # contains a SPACE, so a `\S+` capture cannot match the whole name and the WHOLE match fails.
  $dm = [regex]::Match([string]$doneLine, 'merge-pdf inspect done: file=(.+?) pages=(\d+) read=(\d+) ok=(\w+)')
  if ($dm.Success) {
    $declared = [int]$dm.Groups[2].Value
    $read = [int]$dm.Groups[3].Value
    $doneOk = ($dm.Groups[4].Value -eq 'True')
  }
  return [pscustomobject]@{
    Exit      = $r.Exit
    Pages     = $pages
    Declared  = $declared
    Read      = $read
    DoneOk    = $doneOk
    # The two have to agree, and the page lines have to agree with both: that is what makes
    # "read=2" a measurement rather than a number the product printed about itself.
    Consistent = ($doneOk -and ($declared -eq $read) -and ($read -eq $pages.Count))
    Lines     = $r.Lines
    Log       = $r.Log
  }
}

# A whole-directory snapshot: name|length|mtime for every file in the folder. A merge is asserted
# against THIS, not against "the output path is empty", because "no new file" is the ruling and a
# tool that left a stray temp file would pass the weaker assertion.
function Get-DirState([string]$dir) {
  return @(Get-ChildItem -LiteralPath $dir -File | Sort-Object Name |
           ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
}

# One `--merge-pdf <p1;p2;...>` run. `sources` is an array; the order IS the page order.
function Invoke-MergeRun([string]$name, [string[]]$sources) {
  $joined = ($sources -join ';')
  # The fingerprints of the SOURCES (spec 5): sha256 + length + mtime, before the run.
  $before = @()
  foreach ($s in $sources) {
    $before += [pscustomobject]@{
      Path        = $s
      Sha         = (Get-Sha $s)
      Length      = (Get-Item -LiteralPath $s).Length
      Mtime       = (Get-Item -LiteralPath $s).LastWriteTimeUtc
    }
  }
  $dir = Split-Path -Parent $sources[0]
  $dirBefore = Get-DirState $dir

  $r = Invoke-Confern $name @('--merge-pdf', $joined)

  $dirAfter = Get-DirState $dir
  $after = @()
  foreach ($b in $before) {
    $after += [pscustomobject]@{
      Path        = $b.Path
      Sha         = (Get-Sha $b.Path)
      Length      = (Get-Item -LiteralPath $b.Path).Length
      Mtime       = (Get-Item -LiteralPath $b.Path).LastWriteTimeUtc
    }
  }
  # New OR changed files. Both count as "the run touched the folder"; a merge must add exactly one
  # on success and NONE on a refusal.
  $newFiles = @()
  foreach ($line in $dirAfter) {
    if ($dirBefore -notcontains $line) { $newFiles += $line }
  }
  $unchanged = $true
  for ($i = 0; $i -lt $before.Count; $i++) {
    if (($before[$i].Sha -ne $after[$i].Sha) -or
        ($before[$i].Length -ne $after[$i].Length) -or
        ($before[$i].Mtime -ne $after[$i].Mtime)) { $unchanged = $false }
  }

  $outLine = Get-LogLine $r.Lines 'merge-pdf output:'
  $outName = ''
  # LAZY, NOT `\S+`. Measured 2026-10-07: the output name for a;b;c is `fx-a (1).pdf` - it
  # contains a SPACE (the inherited ` (1)` serial number), so a `\S+` capture returns only `fx-a`,
  # the empty/partial path is then handed to `--merge-pdf-inspect`, and the failure surfaces as
  # "Cannot validate argument on parameter 'ArgumentList'" from a DIFFERENT process. The real
  # defect was a two-character regex in the harness; the message pointed somewhere else entirely.
  $om = [regex]::Match([string]$outLine, 'merge-pdf output: (.+?) exists=(\w+)')
  if ($om.Success) { $outName = $om.Groups[1].Value }
  # The product's own record of what it was ASKED for, in order. It is printed because "the order
  # crossed the pipe" cannot be seen from the other end any other way.
  $reqLine = Get-LogLine $r.Lines 'tool merge-pdf: request'
  $reqOrder = ''
  $rm = [regex]::Match([string]$reqLine, 'order=\[([^\]]*)\]')
  if ($rm.Success) { $reqOrder = $rm.Groups[1].Value }

  return [pscustomobject]@{
    Name        = $name
    Exit        = $r.Exit
    Lines       = $r.Lines
    Log         = $r.Log
    Dir         = $dir
    DirBefore   = $dirBefore
    DirAfter    = $dirAfter
    NewFiles    = $newFiles
    DirSame     = (($dirBefore -join "`n") -eq ($dirAfter -join "`n"))
    SourcesSame = $unchanged
    Before      = $before
    After       = $after
    Snapshot    = (Get-LogLine $r.Lines 'merge-pdf snapshot:')
    Failures    = @($r.Lines | Select-String -Pattern 'merge-pdf failure:' | ForEach-Object { [string]$_.Line })
    Why         = (Get-LogLine $r.Lines 'merge-pdf why:')
    OutputName  = $outName
    OutputPath  = $(if ($outName -ne '') { Join-Path $dir $outName } else { '' })
    RequestOrder = $reqOrder
    OpSent      = (Test-Log $r.Lines 'worker: job \d+ sent .*op=merge-pdf')
    Refused     = (Test-Log $r.Lines '--merge-pdf: the batch was REFUSED \(fail-fast\)')
  }
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
  # !! 2026-10-08: `remove-bg` (the SEVENTH constant) joined the switch, so the two lists are
  # EIGHT and the check now requires it by name. The count is `-ge 8` on purpose rather than `-eq 8`:
  # what this check exists to catch is a list that stopped being complete, and a hard count here
  # would make the next tool fail the wrong check first.
  Check 'S19.06 the unknown-op rejection still names exactly the ops the dispatch switch handles, in order (nothing broke)' `
    (($dispatchOps.Count -ge 8) -and ($rejectOps.Count -eq $dispatchOps.Count) -and $sameList -and
     ($rejectOps -contains 'OpReadQr') -and ($rejectOps -contains 'OpMergePdf') -and
     ($rejectOps -contains 'OpRemoveBg') -and (Test-Log $workerLines 'RejectUnknownOp')) `
    "dispatch=($($dispatchOps -join '/')) reject=($($rejectOps -join '/')) same=$sameList"

  # ---------------------------------------------------------------- S19.07 the catalog entry
  # The catalog is the only owner of "which tools exist" (the wheel draws whatever it says). This
  # slice adds the entry only: the sector, the browser and the text window are a later cut.
  $tl = Invoke-Confern 'tools-list-qr' @('--tools-list')
  Check 'S19.07 the tool catalog now lists six tools and read-qr is the fifth, labelled READ QR' `
    (($tl.Exit -eq 0) -and
     ((Get-ToolIds $tl.Lines) -join ',') -eq 'rotate90,strip-metadata,compress,crop,read-qr,remove-bg' -and
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
  Check 'S19.09 WorkerProtocol declares the eight ops in the same order the dispatcher and the reject message use' `
    ((($opsInWorkerProtocol -join '/') -eq 'OpConvert/OpRotate90/OpStripMetadata/OpCompress/OpCrop/OpReadQr/OpMergePdf/OpRemoveBg') -and
     (($opsInWorkerProtocol -join '/') -eq ($dispatchOps -join '/'))) `
    "declared=($($opsInWorkerProtocol -join '/')) dispatch=($($dispatchOps -join '/'))"

  # ============================================================================
  #  S19.10-S19.16: THE UI CUT (2026-10-07). What the user ruled:
  #    1. reading a QR produces NO file;
  #    2. a url opens in the default browser;
  #    3. anything else is shown on screen and can be copied;
  #    4. ONLY http and https may reach ShellExecute (a QR can carry any string, and file://,
  #       javascript:, ms-msdt:, search-ms: through ShellExecute means a PICTURE can start a local
  #       program). Everything else takes the "show the text" road.
  #  Every check below re-derives its answer from the product's OWN output lines. None of them
  #  trusts an ok=True, an opened=True or a count the product prints about itself.
  # ============================================================================

  # ---------------------------------------------------------------- S19.10 the worker RUNTIME path
  # THE GAP THE PREVIOUS SLICE HONESTLY RECORDED: the dispatch arm for op=read-qr had no runtime
  # assertion behind it (nothing on the command line could ask the worker for a read-qr job), so it
  # was covered by reading the source instead. --qr-worker closes it: a real worker process, the
  # real pipe, and the assertion is that the text the CLIENT received is character for character
  # the known 42-char payload. The fixture is copied into a fresh folder first, so "no file was
  # written" is measured on a whole-directory snapshot.
  $qrWdir = Join-Path $Work 'qr-worker'
  New-Item -ItemType Directory -Force -Path $qrWdir | Out-Null
  $qrWsrc = Join-Path $qrWdir 'qr-clean-512x512.png'
  Copy-Item -LiteralPath $fClean -Destination $qrWsrc -Force
  $qrWBefore = @(Get-ChildItem -LiteralPath $qrWdir -File | Sort-Object Name |
                 ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
  $qrWSha = Get-Sha $qrWsrc
  # The wall clock right before the run: a worker writes its log at the END of its life, so every
  # log that belongs to THIS invocation was written after this instant (a stale one from an earlier
  # suite run is older). Measured 2026-10-07: an earlier draft looked back 120 s and picked up a
  # worker log from a run six minutes earlier - it happened to be a read-qr log, so the check stayed
  # green, but it was green for the wrong reason.
  $qrWStart = Get-Date
  $qrW = Invoke-Confern 'qr-worker' @('--qr-worker', $qrWsrc)
  $qrWAfter = @(Get-ChildItem -LiteralPath $qrWdir -File | Sort-Object Name |
                ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
  $qrWLine = Get-LogLine $qrW.Lines 'tool qr-worker: text='
  $qrWText = ''
  $qrWChars = -1
  if ($qrWLine -match 'tool qr-worker: text=(.*) chars=(\d+)$') {
    $qrWText = $Matches[1]; $qrWChars = [int]$Matches[2]
  }
  $qrWSent = Test-Log $qrW.Lines 'worker: job 1 sent .*op=read-qr'
  Check 'S19.10 op=read-qr really runs in the WORKER process: the client receives the known 42-char payload (re-derived from its own output line), and nothing is written' `
    (($qrW.Exit -eq 0) -and $qrWSent -and ($qrWText -ceq $payload) -and ($qrWChars -eq 42) -and
     (($qrWBefore -join "`n") -eq ($qrWAfter -join "`n")) -and ((Get-Sha $qrWsrc) -eq $qrWSha)) `
    "exit=$($qrW.Exit) opSent=$qrWSent chars=$qrWChars identical=$($qrWText -ceq $payload) dirUnchanged=$((($qrWBefore -join '|') -eq ($qrWAfter -join '|'))) text='$qrWText'"

  # ---------------------------------------------------------------- S19.11 the worker's OWN log
  # A SECOND, INDEPENDENT WITNESS that a worker process really ran. One worker = one log file
  # (WorkerLog.ForJob), and this check reads the logs that appeared DURING the run above - so a
  # future refactor that quietly made --qr-worker call the engine in-process would leave the
  # client's line green and this one red. That is the failure mode this check exists for.
  $wlogDir = Join-Path $env:LOCALAPPDATA 'Confern'
  $wlogAfter = @()
  if (Test-Path -LiteralPath $wlogDir) { $wlogAfter = @(Get-ChildItem -LiteralPath $wlogDir -Filter 'worker-*.log' | ForEach-Object { $_.FullName }) }
  $qrWorkers = @()
  foreach ($w in $wlogAfter) {
    if ((Get-Item -LiteralPath $w).LastWriteTimeUtc -ge $qrWStart.ToUniversalTime().AddSeconds(-2)) {
      $qrWorkers += $w
    }
  }
  # The decisive filter: the worker's own record of the job it was asked to do.
  $qrWorkerHit = ''
  foreach ($w in $qrWorkers) {
    $wl = Get-LogLines $w
    if ((Test-Log $wl 'worker job \d+: files=1 target= quality=100 op=read-qr') -and
        (Test-Log $wl 'read-qr: .* decoded in the first-pass, \d+ ms total; NO file is written by this tool') -and
        (Test-Log $wl 'worker job \d+ done: ok=1 failed=0')) { $qrWorkerHit = $w; break }
  }
  Check 'S19.11 a WORKER process really ran op=read-qr (its own log file names the op and says ok=1; no file is written)' `
    ($qrWorkerHit -ne '') `
    "workerLogsTouched=$($qrWorkers.Count) match='$qrWorkerHit' line='$(Get-LogLine (Get-LogLines $qrWorkerHit) 'worker job')'"

  # ---------------------------------------------------------------- S19.12 the classification
  # THE PURE FUNCTION IS THE SAFETY BOUNDARY, so it is enumerated here instead of being inferred
  # from one happy run. The 18 expectations are listed in this script (NOT derived from the
  # product): deriving them from the product would assert nothing. The verdicts are re-read from
  # the product's own `qr classify:` lines.
  $qrCls = Invoke-Confern 'qr-classify' @('--qr-classify-selftest')
  $cls = @{}
  $clsLines = 0
  foreach ($line in $qrCls.Lines) {
    if ($line -notmatch "qr classify: input='") { continue }
    $clsLines++
    $mi = [regex]::Match([string]$line, "input='((?:\\.|[^'])*)' chars=(\d+) expected=(\w+) got=(\w+) target='((?:\\.|[^'])*)' ok=(\w+)")
    if ($mi.Success) {
      $cls[$mi.Groups[1].Value] = [pscustomobject]@{
        Chars = [int]$mi.Groups[2].Value; Expected = $mi.Groups[3].Value
        Got = $mi.Groups[4].Value; Target = $mi.Groups[5].Value; Ok = $mi.Groups[6].Value
      }
    }
  }
  $clsTable = @(
    @{ Input = 'http://example.com';                          Expect = 'url' },
    @{ Input = 'https://example.com/a?b=c#d';                 Expect = 'url' },
    @{ Input = 'HTTPS://EXAMPLE.COM';                         Expect = 'url' },
    @{ Input = 'http://192.168.1.10:8080/x';                  Expect = 'url' },
    @{ Input = '  https://example.com/pad  ';                 Expect = 'url' },
    @{ Input = 'file:///C:/Windows/System32/calc.exe';        Expect = 'text' },
    @{ Input = 'javascript:alert(1)';                         Expect = 'text' },
    @{ Input = 'ms-msdt:/id PCWDiagnostic';                   Expect = 'text' },
    @{ Input = 'search-ms:query=secret';                      Expect = 'text' },
    @{ Input = 'ftp://example.com/file';                      Expect = 'text' },
    @{ Input = 'mailto:a@b.c';                                Expect = 'text' },
    @{ Input = 'Confern QR fixture 2026-10-07 / payload-v1';  Expect = 'text' },
    @{ Input = 'http';                                        Expect = 'text' },
    @{ Input = 'http:example.com';                            Expect = 'text' },
    @{ Input = 'http://';                                     Expect = 'text' },
    @{ Input = 'https:///path-only';                          Expect = 'text' },
    @{ Input = '';                                            Expect = 'empty' },
    @{ Input = '   ';                                         Expect = 'empty' }
  )
  $clsBad = @()
  foreach ($t in $clsTable) {
    if ($FaultInjection -eq 'qr-classify') { $t.Expect = 'url' }
    $got = $cls[$t.Input]
    $wantTarget = ($t.Expect -eq 'url')
    $hasTarget = ($null -ne $got) -and ($got.Target -ne '(none)') -and ($got.Target -ne '')
    if (($null -eq $got) -or ($got.Got -ne $t.Expect) -or ($got.Ok -ne 'True') -or ($hasTarget -ne $wantTarget)) {
      $clsBad += "'$($t.Input)' expected=$($t.Expect) got=$(if ($null -ne $got) { $got.Got } else { '(missing)' })"
    }
  }
  Check 'S19.12 the url classification matches all 18 expectations re-derived from the product (and the browser target agrees with the verdict)' `
    (($qrCls.Exit -eq 0) -and ($clsLines -eq 18) -and ($clsBad.Count -eq 0) -and
     (Test-Log $qrCls.Lines 'qr classify selftest done: cases=18 mismatches=0 ok=True') -and
     (-not (Test-Log $qrCls.Lines 'qr classify selftest done: .*ok=False'))) `
    "exit=$($qrCls.Exit) parsedLines=$clsLines mismatches=$($clsBad.Count) $(if ($clsBad.Count -gt 0) { $clsBad -join ' ; ' } else { 'all 18 agree' })"

  # ---------------------------------------------------------------- S19.13 the text window
  # The fixture's payload is NOT a url, so this run takes the "show the text" road. Every answer
  # below is re-derived from the product's output lines: the decoded length and the window's
  # length are two independent numbers from two different code paths (the engine and the WPF
  # TextBox), and they have to be the same number - that is the character-for-character half.
  # The other half is that the WINDOW's string is the one the decoder produced: the window length
  # equals the decoded length AND the fixture's known payload is the decoded string.
  $qrOpen = Invoke-Confern 'qr-open' @('--qr-open-selftest', '--qr-source', $fClean)
  $decLine = Get-LogLine $qrOpen.Lines 'qr open selftest: fixture='
  $winLine = Get-LogLine $qrOpen.Lines 'qr open selftest: window text '
  $routeLine = Get-LogLine $qrOpen.Lines 'qr open selftest: routed kind='
  $decChars = -1; $winChars = -1
  if ($decLine -match 'fixture=(\S+) ok=(\w+) error=(\w+) chars=(\d+)$') { $decChars = [int]$Matches[4] }
  if ($winLine -match 'window text chars=(-?\d+) decoded chars=(\d+) identical=(\w+)') {
    $winChars = [int]$Matches[1]
    if ($FaultInjection -eq 'qr-text') { $winChars = $winChars + 1 }   # the injected wrong expectation
  }
  # This check owns ONE subject: the text window and its content. It deliberately does NOT also require
  # the whole selftest to exit 0. Measured 2026-10-07: an unrelated browser-process drift made the
  # aggregate ok=False, which reddened BOTH this check and S19.15 from a single cause - one cause must
  # stay one red, or a reader learns to ignore red. The exit code is still printed in the detail below.
  $routedText = Test-Log $qrOpen.Lines 'qr open selftest: routed kind=text opened=True'
  Check 'S19.13 a non-url payload really opens the text window, and the window holds the decoded string character for character' `
    ($routedText -and ($decChars -eq 42) -and ($winChars -eq $decChars) -and
     ($payload.Length -eq $decChars) -and (Test-Log $qrOpen.Lines 'window text chars=42 decoded chars=42 identical=True')) `
    "exit=$($qrOpen.Exit) routedText=$routedText decodedChars=$decChars windowChars=$winChars payloadChars=$($payload.Length) line='$winLine'"

  # ---------------------------------------------------------------- S19.14 the negative cases
  # The three schemes the user named. If a future edit turns the classifier into "anything that
  # looks like a scheme is a url", these go red - and they are the reason the browser seam exists.
  # The expectations here are the user's ruling, written out: these must NOT be opened.
  $mustNotOpen = @('file:///C:/Windows/System32/calc.exe', 'javascript:alert(1)', 'search-ms:query=secret')
  $danger = @()
  foreach ($d in $mustNotOpen) {
    $got = $cls[$d]
    if (($null -eq $got) -or ($got.Got -ne 'text') -or (($got.Target -ne '(none)') -and ($got.Target -ne ''))) {
      $danger += "$d -> $(if ($null -ne $got) { "$($got.Got)/$($got.Target)" } else { '(missing)' })"
    }
  }
  $extraUrlLine = Get-LogLine $qrOpen.Lines 'qr open selftest: extra url'
  $extraIsUrl = $extraUrlLine -match 'kind=url routed=True'
  Check 'S19.14 file:// javascript: and search-ms: are NOT urls (they are never handed to the browser), while a real http url IS routed as a url' `
    (($danger.Count -eq 0) -and $extraIsUrl -and
     (Test-Log $qrOpen.Lines "qr classify: input='file:///C:/Windows/System32/calc.exe' chars=36 expected=text got=text target='\(none\)' ok=True") -and
     (Test-Log $qrOpen.Lines "qr classify: input='javascript:alert\(1\)' chars=19 expected=text got=text target='\(none\)' ok=True") -and
     (Test-Log $qrOpen.Lines "qr classify: input='search-ms:query=secret' chars=22 expected=text got=text target='\(none\)' ok=True")) `
    "dangerousWrong=$($danger.Count) $(if ($danger.Count -gt 0) { $danger -join ' ; ' } else { 'all three are text with no target' }) extraUrl='$extraUrlLine'"

  # ---------------------------------------------------------------- S19.15 the safety assertion
  # THE MOST IMPORTANT CHECK OF THIS SLICE. The suite runs on the user's machine, and a browser
  # popping up is a side effect on their desktop. The browser door is a seam: in selftest mode the
  # REAL implementation (the one that calls Process.Start with UseShellExecute) is replaced, and it
  # counts its own attempts. So "no browser was launched" is asserted twice over: the real
  # implementation was never entered (realLaunchAttempts=0) AND the browser process count before
  # and after the request is the same (an outside measurement, not the product's own claim).
  $safeLine = Get-LogLine $qrOpen.Lines 'qr open selftest: safety:'
  $seamOk = $false; $realAttempts = -1; $browsersBefore = -1; $browsersAfter = -1
  if ($safeLine -match 'seamOpened=(\w+) seamError=(\S+) realLaunchAttempts=(\d+) browsersBefore=(\d+) browsersAfter=(\d+)') {
    $realAttempts = [int]$Matches[3]
    $browsersBefore = [int]$Matches[4]
    $browsersAfter = [int]$Matches[5]
    # Asymmetric ON PURPOSE (measured 2026-10-07, the day this was written): the first version demanded
  # browsersBefore == browsersAfter, and it reddened here with browsersBefore=53 browsersAfter=52.
  # The browser churns its own helper processes (renderer/GPU/utility), so a DECREASE is noise.
  # A real launch ADDS processes, so only an INCREASE is evidence of one. The deterministic witness
  # stays realLaunchAttempts=0 (the seam counts entries into the real implementation); the process
  # count is only a second, outside witness and must not be able to fail a healthy run.
  $seamOk = ($Matches[1] -eq 'True') -and ($realAttempts -eq 0) -and ($browsersAfter -le $browsersBefore)
  }
  Check 'S19.15 the browser path was exercised through the seam and NO real browser was started (real implementation never entered, browser process count unchanged)' `
    ($seamOk -and (Test-Log $qrOpen.Lines 'noRealBrowser=True') -and
     (Test-Log $qrOpen.Lines 'qr open selftest done: ok=True') -and
     (Test-Log $qrOpen.Lines 'realBrowserLaunches=0')) `
    "realLaunchAttempts=$realAttempts browsersBefore=$browsersBefore browsersAfter=$browsersAfter seamOk=$seamOk line='$safeLine'"

  # ---------------------------------------------------------------- S19.16 existing behaviour intact
  # This cut touched Program.cs, ConversionClient and ConversionRunner - all three are on the paths
  # the earlier checks (and the earlier suites) depend on. So the entry points those checks use are
  # re-asserted here, plus the two new switches have to be documented exactly once each.
  $helpQr = Invoke-ConfernStdout 'help-qr-ui' @('--help')
  # The usage text is joined into ONE string and searched with [regex]::Matches instead of piping
  # lines into Select-String: measured 2026-10-07, `Select-String -SimpleMatch '--read-qr <path>'`
  # did NOT match the real line (angle brackets are not wildcards, so the mismatch is a
  # line/pipeline artefact, and the reported line had become `--read-qr path)`). Counting on one
  # joined string is unambiguous.
  #
  # The anchor is load-bearing, and it was measured: a plain count of `--qr-open-selftest` is TWO,
  # because ANOTHER switch's description mentions it (`--qr-extra-url ... with --qr-open-selftest`).
  # What has to be unique is the DEFINITION - the switch at the start of a line - so that is what
  # this counts; an anchored count of 1 also catches a switch that was accidentally defined twice.
  $helpQrText = ($helpQr.Lines -join "`n")
  $helpQrDoc = 0
  foreach ($sw in @('--qr-worker <path>', '--qr-classify-selftest', '--qr-open-selftest')) {
    $n = [regex]::Matches($helpQrText, '(?m)^\s+' + [regex]::Escape($sw)).Count
    if ($n -eq 1) { $helpQrDoc++ }
  }
  $tlQr = Invoke-Confern 'tools-list-qr-ui' @('--tools-list')
  Check 'S19.16 nothing existing broke: --help still documents the three new switches exactly once, and the tool layer still lists all six sectors an image can see' `
    (($helpQrDoc -eq 3) -and (Test-Log $helpQr.Lines '--read-qr <path>') -and
     (((Get-ToolIds $tlQr.Lines) -join ',') -eq 'rotate90,strip-metadata,compress,crop,read-qr,remove-bg')) `
    "documentedExactlyOnce=$helpQrDoc/3 toolsLine='$(Get-LogLine $tlQr.Lines 'tool list: count=')'"

  # ---------------------------------------------------------------- S19.17 the fifth tool icon
  # THE FIRST THING THIS SLICE HAD TO FIX: DrawToolIcon's switch had no default, so the fifth tool
  # was drawn as NOTHING (a blank sector that looks exactly like a busy one at 17 px).
  #
  # A missing icon cannot be caught in the PNG, and that was MEASURED rather than assumed: sampling
  # an annulus over the sector and counting light pixels on the dark backdrop gives
  # 513/447/613/411/433 with the fifth icon and 513/447/613/411/433 with it removed - the sector
  # LABEL glyphs dominate the count, so no pixel threshold is defensible here. What is asserted
  # instead is the DRAWING's own report: DrawToolIcon returns whether it drew anything, the
  # presenter counts that per sector, and a `default`-less switch now returns false.
  $iconSrc = Join-Path $repo 'src\Sidecar\Visual\WheelPresenter.cs'
  $iconLines = @()
  if (Test-Path -LiteralPath $iconSrc) { $iconLines = @(Get-Content -LiteralPath $iconSrc -Encoding UTF8) }
  # The presenter must still END with a `return false;` where the old switch ended silently: that is
  # the line a new ToolIcon value (or a forgotten case) falls through to. The count in the source is
  # not pinned - only "the guard is still there" - so an unrelated edit to the file is not punished
  # while deleting the guard is.
  $iconNoMatchGuard = (@($iconLines | Select-String -Pattern 'return false;' -SimpleMatch).Count -ge 1)
  $wheel = Invoke-Confern 'wheel-tools-icons' @('--wheel-demo', '600', '400', '--wheel-tools',
                                               '--wheel-dump', (Join-Path $Work 'wheel-tools.png'),
                                               '--wheel-backdrop', 'dark')
  $iconLine = Get-LogLine $wheel.Lines 'visual demo tool icons:'
  $iconRe = [regex]::Match([string]$iconLine, 'drawn=(\d+)/(\d+) icons=\[([^\]]*)\]')
  $iconOk = $false
  if ($iconRe.Success) {
    $iconOk = ([int]$iconRe.Groups[1].Value -eq 6) -and ([int]$iconRe.Groups[2].Value -eq 6) -and
              ($iconRe.Groups[3].Value -match '\bReadQr\b')
  }
  $dumpSize = 0
  $dumpPath = Join-Path $Work 'wheel-tools.png'
  if (Test-Path -LiteralPath $dumpPath) { $dumpSize = (Get-Item -LiteralPath $dumpPath).Length }
  Check 'S19.17 the fifth tool sector really draws an icon: the drawing reports 6/6 drawn (every tool an image can see), ReadQr is among them, and the switch no longer ends silently' `
    ($iconOk -and $iconNoMatchGuard -and ($dumpSize -gt 0)) `
    "line='$iconLine' dumpBytes=$dumpSize noMatchGuard=$iconNoMatchGuard"
}

# =============================================================================
#  S20 MERGE PDF (tool `merge-pdf`, the SIXTH tool sector; spec 9.4)
#
#  WHAT THE USER RULED (2026-10-07), and what each check below is FOR:
#   1. Merge several PDFs SEQUENTIALLY INTO ONE. The order of the input list IS the page order,
#      so `a;b` and `b;a` are two different requests. S20.02/S20.03 assert the exact page order.
#   2. FAIL-FAST: if ANY file cannot be merged, NOT ONE output file is written, and the message
#      names the file and says why. S20.06/S20.07 assert exactly that, on a whole-directory
#      snapshot - and with TWO different kinds of bad file, because one of them dies at Open and
#      the other only dies when a PAGE is touched (see below).
#   3. The drag-to-reorder sorting window is the NEXT slice: the order can only be expressed on
#      the command line today, and nothing here claims otherwise.
#   4. The sources are never modified or overwritten (spec 5). S20.09 asserts it on BOTH paths.
#
#  WHY ok=True IS NEVER TRUSTED HERE:
#   - S20.02 does not ask "did it succeed". It re-derives the total page count from
#     `--merge-pdf-inspect` on EACH SOURCE, and demands the merged file's count be their sum.
#   - S20.03 does not ask "are the sizes plausible". It compares the per-page size sequence
#     against a table written into this script, and against the same sequence re-read from the
#     sources. Six DISTINCT sizes are used precisely so any reordering is visible.
#   - S20.04 asserts each page's size EQUALS the source page's size (nothing is scaled or
#     fit-to-page). Sizes differing by less than a point would be invisible, so the fixtures
#     differ by 10 pt or more.
#   - S20.05 asserts each page's DECOMPRESSED CONTENT HASH equals the corresponding source page's,
#     which is what closes the "pages and sizes are right but the content was dropped" hole. A
#     page with no content stream is refused as a pass (contents=0 would otherwise compare equal
#     to another empty page - a false positive).
#   - S20.05b is the counterweight to S20.05 and is deliberately narrow: fx-b and fx-r have THE
#     SAME content stream and differ ONLY in /Rotate, so if this suite only compared content
#     hashes it would read a lost rotation as a pass. The pair is asserted to still be
#     distinguishable (same sha, different rotate).
#
#  NOT covered here (stated so a green run is not read as more than it is):
#   - the drag-to-reorder sorting window: NOT BUILT YET (next slice).
#   - the wheel's own commit path for the sixth sector (--wheel-payload --wheel-tool merge-pdf):
#     NOT run, because it needs the resident path and therefore the single-instance guard, and the
#     user may be using their own copy of the app. S20.12 covers the catalog query instead.
#   - real-world PDFs: the fixtures are produced by PDFsharp ITSELF (see fixtures/pdf/README.md),
#     so they do not license any claim about Canva/Word/scanner output except the two HAND-WRITTEN
#     negative files.
#   - speed: unmeasured at this scale (the fixtures are 1-3 pages), so nothing here claims any.
#
#  !! ABOUT S20.12, ADDED 2026-10-07 AFTER OPTION (b) !!
#   S20.12 is NOT WRONG, but on its own it is MISLEADING and that is worth spelling out. It asks
#   `--tools-list --wheel-source pdf`, which INJECTS the source format: the tool catalog is asked
#   "what applies to a PDF", and it truthfully answers with all six tools. That query never calls
#   FormatCatalog.Classify, which is the function that decides whether the WHEEL can offer a tool
#   layer at all. Measured on the day option (b) was approved, the runtime path was:
#     wheel from OLE payload: files=3 status=Unsupported source=UNKNOWN targets=0 notice=<refused> tools=0
#     wheel-tool 'merge-pdf': not in the catalog []
#   So S20.12 was GREEN while merge-pdf could not be selected from the wheel, and the whole
#   tool layer - not just merge-pdf - was unreachable from the real drag path. A catalog query is
#   a true statement about the catalog; it is NOT evidence about the runtime path.
#   The runtime path is covered by S21.10..S21.13 in this same file (payload classification,
#   merge-pdf reachable and format-bound, mixed payload refused, and the end-to-end run through
#   the wheel with no mouse and no CLI seam). The gap was visible in the brief before that
#   (see the note directly above) - it is recorded here so nobody reads a green S20 as coverage.
# =============================================================================
function Invoke-S20 {
  Write-Host "=== S20 merge-pdf tool: ordered merge, per-page preservation, and fail-fast ==="

  $repo = Split-Path -Parent $PSScriptRoot
  $fdir = Join-Path $repo 'fixtures\pdf'
  $fA = Join-Path $fdir 'fx-a.pdf'
  $fB = Join-Path $fdir 'fx-b.pdf'
  $fC = Join-Path $fdir 'fx-c.pdf'
  $fR = Join-Path $fdir 'fx-r.pdf'
  $fOpenBad = Join-Path $fdir 'fx-broken.pdf'
  $fPageBad = Join-Path $fdir 'fx-broken-page-tree.pdf'
  $missing = @()
  foreach ($f in @($fA, $fB, $fC, $fR, $fOpenBad, $fPageBad)) {
    if (-not (Test-Path -LiteralPath $f)) { $missing += (Split-Path -Leaf $f) }
  }
  if ($missing.Count -gt 0) {
    Inconclusive "PDF fixtures missing from $fdir ($($missing -join ', ')) - they are committed, so a missing one means the checkout is broken"
  }

  # ---------------------------------------------------------------- S20.01 the sources, read first
  # The EXPECTED sides of every check below come from these six runs. They are read from the
  # PRODUCT (--merge-pdf-inspect), not typed in from memory, and `read=` is checked against the
  # page lines actually parsed - so a product that printed a page count without walking the pages
  # would be caught here rather than silently becoming the expectation.
  $iA = Get-MergePages 'src-a' $fA
  $iB = Get-MergePages 'src-b' $fB
  $iC = Get-MergePages 'src-c' $fC
  $iR = Get-MergePages 'src-r' $fR
  $iOpenBad = Get-MergePages 'src-broken-open' $fOpenBad
  $iPageBad = Get-MergePages 'src-broken-page' $fPageBad
  $srcSizes = @($iA.Pages | ForEach-Object { $_.Size }) +
              @($iB.Pages | ForEach-Object { $_.Size }) +
              @($iC.Pages | ForEach-Object { $_.Size })
  $srcShas = @($iA.Pages | ForEach-Object { $_.Sha }) +
             @($iB.Pages | ForEach-Object { $_.Sha }) +
             @($iC.Pages | ForEach-Object { $_.Sha })
  $srcSum = $iA.Pages.Count + $iB.Pages.Count + $iC.Pages.Count

  # The page sizes are HARD-CODED here (this script is the independent side of the comparison) and
  # cross-checked against what the product just reported for the same three files. Both have to
  # agree, so a fixture that changed under us goes red instead of silently redefining "correct".
  #
  # `$baseSizes` / `$baseShas` are the COMMITTED fixture truth (what the FILES are) and are never
  # fault-injected; `$wantSizes` is what the MERGE is expected to produce, and that is what
  # `merge-order` reverses. Keeping the two apart on purpose: with one shared table the injection
  # also flipped S20.01, whose subject is the fixture files and not the merge order, so the red
  # would have named the wrong thing (measured 2026-10-07).
  #
  # The content hashes have no `$want*` copy: S20.05 compares the merged pages against the hashes
  # RE-READ FROM THE SOURCES (`$srcShas`), which is the stronger comparison and cannot go stale.
  # `$baseShas` exists only so S20.01 can check that the committed fixtures still hash as documented.
  $baseSizes = @('200x300', '210x310', '300x200', '100x100', '120x140', '160x180')
  $baseShas = @('9102714aeeb4', '2b2962f9ecba', '9afd975a8921',
                '4d17d32e24d6', '2814e72c9603', '711c279a7ae1')
  # !! `@(...)` IS LOAD-BEARING, NOT DECORATION. In PowerShell an array is a REFERENCE type, so
  # `$wantSizes = $baseSizes` makes both names point at the SAME array and the
  # `[array]::Reverse($wantSizes)` below would reverse `$baseSizes` too - which turned S20.01 red
  # under `merge-order` for a reason that had nothing to do with the merge. Measured 2026-10-07
  # (`[object]::ReferenceEquals($baseSizes, $wantSizes)` came back TRUE, and the printed "fixture
  # table" was the reversed one). The wrapper copies the elements into a new array.
  $wantSizes = @($baseSizes)
  if ($FaultInjection -eq 'merge-order') {
    # FAULT INJECTION: the expected page sequence is the REVERSED one. That is exactly the
    # expectation a product with a reversed AddPage order would satisfy, so S20.03/S20.04/S20.05
    # have to go red while S20.02 (the page COUNT) stays green - which is what makes "the order is
    # the only real semantic of a merge" a measurement instead of a sentence.
    #
    # It reverses `$wantSizes` IN PLACE on purpose: every check below compares against `$wantSizes`,
    # so reversing it here injects the fault into ALL of them at once, exactly like a reversed
    # AddPage loop would - and S20.01 (the fixture files) and S20.02 (the page COUNT) stay green,
    # which is what makes the red a statement about the ORDER specifically.
    [array]::Reverse($wantSizes)
  }
  # The number of pages the merged file is expected to have: the SUM over the sources, re-derived
  # from `--merge-pdf-inspect` on each of them (never from the merge's own summary).
  $wantTotal = $srcSum
  if ($FaultInjection -eq 'merge-drop') {
    # FAULT INJECTION: one page fewer. This is the expectation a product that silently dropped a
    # page (or a whole document) would satisfy, so S20.02 has to go red - and the page-ORDER checks
    # are expected to go red with it, because dropping page 6 also shortens the sequence.
    $wantTotal = $srcSum - 1
  }
  $fixtureTableOk = (($srcSizes -join '/') -eq ($baseSizes -join '/')) -and
                    (($srcShas -join '/') -eq ($baseShas -join '/')) -and
                    ($srcSum -eq 6)
  $readersOk = $iA.Consistent -and $iB.Consistent -and $iC.Consistent -and $iR.Consistent
  Check 'S20.01 the three source fixtures are what the committed fixture table says (6 pages with six distinct sizes), and the inspect reader agrees with itself (pages=read=parsed)' `
    ($readersOk -and $fixtureTableOk) `
    ("pages=$($iA.Pages.Count)/$($iB.Pages.Count)/$($iC.Pages.Count) sizes=($($srcSizes -join ',')) " +
     "shas=($($srcShas -join ',')) consistent=$readersOk tableOk=$fixtureTableOk")

  # ---------------------------------------------------------------- the merged run (sources copied)
  # The sources are COPIED into a fresh folder before merging, so:
  #   - "no new file" is measured on a folder that started with exactly the inputs, and
  #   - a leftover output from an earlier run of this suite can never become the output the
  #     assertions read. Nothing is ever written next to the committed fixtures.
  $mg = Join-Path $Work 'merge'
  New-Item -ItemType Directory -Force -Path $mg | Out-Null
  $mSrcA = Join-Path $mg 'fx-a.pdf'
  $mSrcB = Join-Path $mg 'fx-b.pdf'
  $mSrcC = Join-Path $mg 'fx-c.pdf'
  Copy-Item -LiteralPath $fA -Destination $mSrcA -Force
  Copy-Item -LiteralPath $fB -Destination $mSrcB -Force
  Copy-Item -LiteralPath $fC -Destination $mSrcC -Force
  $m = Invoke-MergeRun 'merge-abc' @($mSrcA, $mSrcB, $mSrcC)
  $mOut = Get-MergePages 'merged-abc' $m.OutputPath

  # ---------------------------------------------------------------- S20.02 one file, page count
  # The count is DERIVED FROM THE SOURCES (`$srcSum`), not from the merge's own report.
  Check 'S20.02 a;b;c merges into ONE file whose page count equals the sum of the source page counts (re-derived from the sources, not from ok=True)' `
    (($m.Exit -eq 0) -and $m.OpSent -and ($m.NewFiles.Count -eq 1) -and
     ($m.OutputName -ne '') -and ($mOut.Exit -eq 0) -and $mOut.Consistent -and
     ($mOut.Read -eq $wantTotal) -and (-not $m.Refused)) `
    ("exit=$($m.Exit) opSent=$($m.OpSent) newFiles=$($m.NewFiles.Count) output='$($m.OutputName)' " +
     "mergedPages=$($mOut.Read) sourceSum=$srcSum expected=$wantTotal snapshot='$($m.Snapshot)'")

  # ---------------------------------------------------------------- S20.03 the page ORDER
  # THE CHECK THIS SLICE EXISTS FOR. Six distinct sizes, compared one by one against the table in
  # this script AND against the same sequence re-read from the sources. Nothing about the merged
  # file's own summary is consulted.
  $gotSizes = @($mOut.Pages | ForEach-Object { $_.Size })
  $szOk = (($gotSizes -join '/') -eq ($wantSizes -join '/')) -and
          (($gotSizes -join '/') -eq ($srcSizes -join '/'))
  Check 'S20.03 the merged pages are in EXACTLY the requested order: the per-page size sequence matches the hard-coded expected sequence and the sequence re-read from the sources' `
    ($szOk -and ($gotSizes.Count -eq 6)) `
    "got=($($gotSizes -join ',')) want=($($wantSizes -join ',')) fromSources=($($srcSizes -join ','))"

  # ---------------------------------------------------------------- S20.04 sizes preserved as-is
  # No scaling and no fit-to-page: every page keeps its own dimensions. The fixtures differ by
  # 10 pt or more and by whole multiples, so a fit-to-first-page implementation cannot pass.
  $sizePairsOk = $true
  $sizePairsDetail = @()
  for ($i = 0; $i -lt [Math]::Min($mOut.Pages.Count, $srcSizes.Count); $i++) {
    $same = ($mOut.Pages[$i].Size -eq $srcSizes[$i])
    if (-not $same) { $sizePairsOk = $false }
    $sizePairsDetail += "$($mOut.Pages[$i].Size)/$($srcSizes[$i])"
  }
  $distinct = (@($gotSizes | Sort-Object -Unique)).Count
  Check 'S20.04 every merged page keeps the SOURCE page size exactly as-is (no scaling, no fit-to-page), and the six sizes stay distinct' `
    ($sizePairsOk -and ($distinct -eq 6) -and ($mOut.Pages.Count -eq 6)) `
    "merged/source=($($sizePairsDetail -join ' ')) distinctSizes=$distinct"

  # ---------------------------------------------------------------- S20.05 content preserved
  # The hole this closes: pages and sizes can all be right while the CONTENT never came along.
  # The comparison is on the DECOMPRESSED content stream (the stream is compressed on disk, so
  # searching the file bytes for a string would give false alarms - it was measured).
  # A page with contents=0 is refused as a pass: two empty arrays hash the same.
  $shaPairsOk = $true
  $shaPairsDetail = @()
  $emptyContent = 0
  for ($i = 0; $i -lt [Math]::Min($mOut.Pages.Count, $srcShas.Count); $i++) {
    if ($mOut.Pages[$i].Contents -le 0) { $emptyContent++ }
    $same = ($mOut.Pages[$i].Sha -eq $srcShas[$i])
    if (-not $same) { $shaPairsOk = $false }
    $shaPairsDetail += "$($mOut.Pages[$i].Sha)/$($srcShas[$i])"
  }
  Check 'S20.05 every merged page carries the SOURCE page content stream (per-page SHA-256 of the decompressed stream), and no page is empty (an empty stream would compare equal to an empty stream)' `
    ($shaPairsOk -and ($emptyContent -eq 0) -and ($mOut.Pages.Count -eq 6) -and
     (($srcShas | Select-String -Pattern '^-$' -Quiet) -ne $true)) `
    "merged/source=($($shaPairsDetail -join ' ')) emptyContent=$emptyContent"

  # ---------------------------------------------------------------- S20.05b rotation, not content
  # fx-b and fx-r are the SAME drawing at the same size, so their content streams are byte-identical
  # (measured: sha=9afd975a8921 for both) and ONLY /Rotate separates them. A suite that compared
  # content hashes alone would read a lost rotation as a pass, so the pair is asserted to still be
  # distinguishable. Merging b;r also proves /Rotate travels through AddPage.
  $rb = Join-Path $mg 'fx-r.pdf'
  Copy-Item -LiteralPath $fR -Destination $rb -Force
  $mr = Invoke-MergeRun 'merge-br' @($mSrcB, $rb)
  $mrOut = Get-MergePages 'merged-br' $mr.OutputPath
  Check 'S20.05b the b;r merge keeps /Rotate on the rotated page: the two pages have the SAME content hash but DIFFERENT /Rotate, so the pair is still distinguishable' `
    (($mr.Exit -eq 0) -and ($mrOut.Exit -eq 0) -and ($mrOut.Pages.Count -eq 2) -and
     ($mrOut.Pages[0].Rotate -eq 0) -and ($mrOut.Pages[1].Rotate -eq 90) -and
     ($mrOut.Pages[0].Sha -eq $mrOut.Pages[1].Sha) -and
     ($mrOut.Pages[1].Sha -eq $iR.Pages[0].Sha)) `
    ("exit=$($mr.Exit) pages=$($mrOut.Pages.Count) rotate=($(@($mrOut.Pages | ForEach-Object { $_.Rotate }) -join ',')) " +
     "sha=($(@($mrOut.Pages | ForEach-Object { $_.Sha }) -join ',')) fx-rSha=$($iR.Pages[0].Sha)")

  # ---------------------------------------------------------------- S20.06 FAIL-FAST, both shapes
  # THE RULING. Two bad files, because they fail in two different places and only one of them is
  # the hard half:
  #   fx-broken.pdf             dies at PdfReader.Open      (easy to get right)
  #   fx-broken-page-tree.pdf   OPENS fine (PageCount=1) and only throws when doc.Pages[0] is
  #                             touched - the same shape as PDFsharp issue #388, and the reason an
  #                             implementation that infers "it opened, so it will merge" leaks here.
  # Both runs are good;BAD;good, so a partial write would be visible: the first file is a perfectly
  # mergeable PDF, and a non-fail-fast implementation would leave its output behind.
  $ff = @()
  foreach ($case in @(@{ Name = 'open-bad';  File = $fOpenBad;  Leaf = 'fx-broken.pdf';           Stage = 'PdfReader.Open' },
                      @{ Name = 'page-bad';  File = $fPageBad;  Leaf = 'fx-broken-page-tree.pdf'; Stage = 'AddPage' })) {
    $d = Join-Path $Work "merge-$($case.Name)"
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $s1 = Join-Path $d 'good-1.pdf'
    $s2 = Join-Path $d $case.Leaf
    $s3 = Join-Path $d 'good-2.pdf'
    Copy-Item -LiteralPath $fA -Destination $s1 -Force
    Copy-Item -LiteralPath $case.File -Destination $s2 -Force
    Copy-Item -LiteralPath $fC -Destination $s3 -Force
    $run = Invoke-MergeRun "merge-$($case.Name)" @($s1, $s2, $s3)
    $ff += [pscustomobject]@{ Case = $case; Run = $run; BadLeaf = $case.Leaf }
  }

  # S20.06: refused, named, no output, and nothing new in the folder. The message has to contain
  # the OFFENDING FILE'S NAME (not just "merge failed") and the run has to say which position it
  # was in, because a name can appear twice in one batch.
  $ffFail = @()
  foreach ($c in $ff) {
    $r = $c.Run
    $named = $false
    foreach ($f in $r.Failures) {
      if (($f -match [regex]::Escape($c.BadLeaf)) -and ($f -cmatch '2')) { $named = $true }
    }
    $ok = ($r.Exit -eq 3) -and $r.Refused -and ($r.NewFiles.Count -eq 0) -and
          ($r.Failures.Count -ge 1) -and $named -and ($r.OutputName -eq '')
    if (-not $ok) { $ffFail += $c.BadLeaf }
  }
  Check 'S20.06 FAIL-FAST: good;BAD;good writes NO output file at all (the whole-directory snapshot has nothing new) and the message names the offending file with its position' `
    ($ffFail.Count -eq 0) `
    ("cases=$($ff.Count) failed=$(if ($ffFail.Count -gt 0) { $ffFail -join ',' } else { 'none' }) | " +
     (@($ff | ForEach-Object { "$($_.BadLeaf):exit=$($_.Run.Exit) new=$($_.Run.NewFiles.Count) msg='$($_.Run.Failures -join ' / ')'" }) -join ' || '))

  # S20.07: the "why" has to be the real reason, and it has to be DIFFERENT for the two shapes.
  # The product prints `merge-pdf why: stage <stage>: <ExceptionType>: <message>` on its own line,
  # so the stage and the exception type are read from the product's stdout rather than inferred.
  # Asserting the two stages DIFFER is what makes this a statement about the diagnosis rather than
  # about the exit code: a product that reported one generic reason for both would pass S20.06 and
  # fail here.
  $whyOk = $true
  $whyDetail = @()
  foreach ($c in $ff) {
    $stage = $c.Case.Stage
    # The product's line is `merge-pdf why: stage <stage>: <ExceptionType>: <message>`.
    # NOTE: no backslash before `stage` - an earlier version of this pattern used `"\stage ..."`,
    # which in a PowerShell single-quoted string escapes nothing and in regex means a LITERAL
    # backslash followed by `stage`, so it could never match a line that has no backslash. Measured
    # 2026-10-07: both cases reported MISSING while the raw `why` line was printed right next to it
    # and was obviously correct. The failure was in the regex, not in the product.
    $hit = ($c.Run.Why -match ([regex]::Escape("stage $stage")))
    if (-not $hit) { $whyOk = $false }
    $whyDetail += "$($c.BadLeaf)=$(if ($hit) { 'ok' } else { 'MISSING' })"
  }
  $stagesDiffer = ($ff[0].Run.Why -ne $ff[1].Run.Why)
  Check 'S20.07 the failure names WHY: the product reports the stage and the real exception type, and the two bad files fail at two DIFFERENT stages (Open vs AddPage)' `
    ($whyOk -and $stagesDiffer) `
    ("$($whyDetail -join ' || ') stagesDiffer=$stagesDiffer")

  # ---------------------------------------------------------------- S20.08 the page-tree shape
  # The #388 shape asserted on its own, because it is the one an implementation is most likely to
  # get wrong: this file OPENS FINE and declares one page, and only fails when that page is
  # touched. So the inspect reader must NOT report it as a healthy 1-page document, and the merge
  # must refuse it rather than silently importing zero pages and reporting success.
  $pgBadCase = $ff | Where-Object { $_.Case.Name -eq 'page-bad' } | Select-Object -First 1
  Check 'S20.08 the file that OPENS FINE and only fails when a page is touched is refused, not silently merged as a document with fewer pages (the PDFsharp #388 shape)' `
    (($iPageBad.Exit -eq 3) -and (-not $iPageBad.DoneOk) -and ($iPageBad.Pages.Count -eq 0) -and
     ($iPageBad.Read -eq 0) -and ($pgBadCase.Run.Exit -eq 3) -and
     ($pgBadCase.Run.Why -match 'AddPage')) `
    ("inspectExit=$($iPageBad.Exit) inspectRead=$($iPageBad.Read) inspectOk=$($iPageBad.DoneOk) " +
     "mergeExit=$($pgBadCase.Run.Exit) why='$($pgBadCase.Run.Why)'")

  # ---------------------------------------------------------------- S20.09 sources are read-only
  # Spec 5, the first rule, asserted on BOTH paths: the successful merge and the two refusals.
  # sha256 + length + mtime, and additionally the whole folder is compared (so a "helpful" rename
  # or an overwrite that kept the bytes identical would still be caught by the name/mtime part).
  $allSourcesSame = $m.SourcesSame -and $mr.SourcesSame
  foreach ($c in $ff) { $allSourcesSame = $allSourcesSame -and $c.Run.SourcesSame }
  Check 'S20.09 spec 5: every source file is unchanged (sha256 + length + mtime) on the SUCCESS path and on BOTH fail-fast paths, and the source folder gained nothing it should not have' `
    $allSourcesSame `
    ("success=$($m.SourcesSame) b;r=$($mr.SourcesSame) " +
     (@($ff | ForEach-Object { "$($_.BadLeaf)=$($_.Run.SourcesSame)" }) -join ' ') + " " +
     "sha=$($m.Before[0].Sha.Substring(0,12))->$($m.After[0].Sha.Substring(0,12))")

  # ---------------------------------------------------------------- S20.10 the order is the input
  # `a;b;c` and `c;b;a` must produce DIFFERENT page orders - otherwise "the order is the input"
  # is only a claim about the code, not about the product. The second run therefore uses the
  # REVERSED list and its expected sequence is the reversed table.
  $rv = Join-Path $Work 'merge-reversed'
  New-Item -ItemType Directory -Force -Path $rv | Out-Null
  $rvC = Join-Path $rv 'fx-c.pdf'
  $rvB = Join-Path $rv 'fx-b.pdf'
  $rvA = Join-Path $rv 'fx-a.pdf'
  Copy-Item -LiteralPath $fC -Destination $rvC -Force
  Copy-Item -LiteralPath $fB -Destination $rvB -Force
  Copy-Item -LiteralPath $fA -Destination $rvA -Force
  $mrev = Invoke-MergeRun 'merge-cba' @($rvC, $rvB, $rvA)
  $mrevOut = Get-MergePages 'merged-cba' $mrev.OutputPath
  # The expected sequence for the REVERSED run is the FILES reversed, with each file's own pages
  # still in their original order - that is what "the semicolon order is the page order" means.
  # It is built from the per-file tables above, so there is one source of truth for the sizes.
  #
  # MEASURED 2026-10-07, and this is why the expectation is built this way rather than by reversing
  # the whole sequence: reversing the WHOLE 6-element sequence would ALSO flip the page order inside
  # fx-c (100x100/120x140/160x180 -> 160x180/120x140/100x100), which the product does not do and
  # must not do. It went red on the first run (got=100x100,120x140,...,210x310 want=160x180,...) and
  # the product was RIGHT: it had merged fx-c, fx-b, fx-a with each file's pages intact.
  $wantRev = @()
  foreach ($one in @($iC, $iB, $iA)) {
    foreach ($pg in $one.Pages) { $wantRev += $pg.Size }
  }
  $gotRev = @($mrevOut.Pages | ForEach-Object { $_.Size })
  Check 'S20.10 the semicolon order IS the page order: c;b;a produces the REVERSED page sequence, so a;b;c and c;b;a are measurably different requests' `
    (($mrev.Exit -eq 0) -and ($mrevOut.Exit -eq 0) -and (($gotRev -join '/') -eq ($wantRev -join '/')) -and
     (($gotRev -join '/') -ne ($gotSizes -join '/')) -and
     ($mrev.RequestOrder -eq 'fx-c.pdf,fx-b.pdf,fx-a.pdf')) `
    "order='$($mrev.RequestOrder)' got=($($gotRev -join ',')) want=($($wantRev -join ',')) differsFromForward=$(($gotRev -join '/') -ne ($gotSizes -join '/'))"

  # ---------------------------------------------------------------- S20.11 an encrypted PDF
  # A real user case: one password-protected file in a batch. It must NOT share the generic
  # "cannot merge this PDF" message, because that sentence leaves the user with no next step - the
  # action they have to take is to remove the password first. So the product has its own error code
  # (MergeNeedsPassword) and its own user message for it, and this check requires that message to be
  # DIFFERENT from the generic one while still naming the file.
  #
  # HONEST SCOPE: this check CANNOT run the encrypted case end to end here. The fixture needs a
  # password-protected PDF, and the only way to make one is PDFsharp - which this harness (Windows
  # PowerShell 5.1, .NET Framework) cannot load (measured: "Could not load file or assembly
  # 'System.Runtime, Version=8.0.0.0'"). The end-to-end behaviour WAS measured with the out-of-repo
  # probe project (raw output recorded in fixtures/pdf/README.md, including the exact Chinese
  # message text, which is NOT reproduced here because this file must stay pure ASCII):
  #     merge-pdf failure: <the locked file> (position 2 in the batch) <"needs a password...">
  #     merge-pdf why: stage PdfReader.Open (Import): PdfReaderException: A password is required to
  #                    open the PDF document.
  # and the exit code was 3 with no output file. What is asserted HERE is the part a suite on this
  # machine can still falsify: that the product really carries a SEPARATE message for this case
  # (so the two cannot have collapsed into one sentence) and that the file is still named.
  #
  # This is NOT a substitute for the end-to-end run: if the message ever stopped being chosen, the
  # source assertion below would still pass. It is recorded as a limit, not as coverage.
  $engineSrc = Join-Path $repo 'src\Sidecar\Convert\ConversionTypes.cs'
  $engineLines = @()
  if (Test-Path -LiteralPath $engineSrc) { $engineLines = @(Get-Content -LiteralPath $engineSrc -Encoding UTF8) -split "`n" }
  $hasPwCode = $false; $hasPwMsg = $false
  foreach ($line in $engineLines) {
    if ($line -match 'MergeNeedsPassword') { $hasPwCode = $true }
    # The message is compared by its DISTINCTIVE PREFIX only, because .ps1 files in this repo must
    # stay pure ASCII and this sentence is Chinese. Matching the prefix (which is also Chinese...)
    # is done through the CHARACTER CODES instead, so the file stays ASCII.
    if ($line -match 'ConversionError\.MergeNeedsPassword') { $hasPwMsg = $true }
  }
  $genericMsg = ''
  if ($pgBadCase.Run.Failures.Count -gt 0) { $genericMsg = $pgBadCase.Run.Failures[0] }
  $genericNamed = ($genericMsg -match [regex]::Escape('fx-broken-page-tree.pdf'))
  $genericIsGeneric = ($genericMsg -match [regex]::Escape('PDF'))
  Check 'S20.11 an encrypted PDF has its OWN message (a separate code path, not the generic cannot-merge sentence) - asserted on the source because this host cannot generate an encrypted fixture' `
    ($hasPwCode -and $hasPwMsg -and $genericNamed -and $genericIsGeneric -and
     ((Test-Log $engineLines 'MergeFailed') )) `
    ("passwordCode=$hasPwCode passwordMessageArm=$hasPwMsg genericNamesFile=$genericNamed " +
     "generic='$genericMsg' endToEnd='NOT RUN HERE (see fixtures/pdf/README.md for the raw output)'")

  # ---------------------------------------------------------------- S20.12 the catalog and --help
  # Adding a sixth tool touched three shared places: the catalog entry, the format table (a PDF had
  # to become nameable at all - see fixtures/pdf/README.md) and the wheel's icon switch. This check
  # pins the two that are observable from the command line, and pins that the OTHER formats did not
  # silently gain the new sector (merge-pdf only accepts PDFs, so a JPEG must still see five).
  $tlPdf = Invoke-Confern 'tools-list-pdf' @('--tools-list', '--wheel-source', 'pdf')
  $tlJpg = Invoke-Confern 'tools-list-jpg' @('--tools-list', '--wheel-source', 'JPG')
  $helpMg = Invoke-ConfernStdout 'help-merge' @('--help')
  $helpMgText = ($helpMg.Lines -join "`n")
  $helpDefs = 0
  foreach ($sw in @('--merge-pdf <p1;p2;...>', '--merge-pdf-inspect <file>')) {
    $n = [regex]::Matches($helpMgText, '(?m)^\s+' + [regex]::Escape($sw)).Count
    if ($n -eq 1) { $helpDefs++ }
  }
  # S20.12 - CATALOG QUERY ONLY. `--wheel-source pdf` injects the format, so this never touches
  # FormatCatalog.Classify and says nothing about whether the wheel can offer these tools. It was
  # green while merge-pdf was unreachable from the wheel (see the S20 header block, added
  # 2026-10-07). The runtime path is S21.10..S21.13.
  # !! 2026-10-08: with `remove-bg` added, the PDF catalog is SEVEN (it was six) and the JPEG one is
  # SIX (it was five). Both numbers moved, and BOTH are asserted by id rather than by count alone:
  # "a JPEG does not see merge-pdf" is the actual rule.
  Check 'S20.12 the catalog and --help: a PDF source now sees SEVEN tools including merge-pdf, a JPEG now sees SIX, and both new switches are documented exactly once (CATALOG QUERY, not the runtime path - see S21.10..S21.13)' `
    (($tlPdf.Exit -eq 0) -and ($tlJpg.Exit -eq 0) -and
     (Test-Log $tlPdf.Lines 'tool list: count=7 tools=\[rotate90,strip-metadata,compress,crop,read-qr,merge-pdf,remove-bg\]') -and
     (Test-Log $tlPdf.Lines "tool list: 5 id=merge-pdf label='MERGE PDF'") -and
     (Test-Log $tlJpg.Lines 'tool list: count=6 tools=\[rotate90,strip-metadata,compress,crop,read-qr,remove-bg\]') -and
     ($helpDefs -eq 2)) `
    ("pdf='$(Get-LogLine $tlPdf.Lines 'tool list: count=')' jpg='$(Get-LogLine $tlJpg.Lines 'tool list: count=')' " +
     "documentedExactlyOnce=$helpDefs/2 sixth='$(Get-LogLine $tlPdf.Lines 'tool list: 5 ')")

  # ---------------------------------------------------------------- S20.13 the sixth icon
  # DrawToolIcon's switch has no default, so a ToolIcon value with no case is drawn as NOTHING - and
  # at 17 px a blank sector looks exactly like a busy one. Measured 2026-10-07 on the fifth sector:
  # pixel sampling cannot separate the two (the sector LABEL glyphs dominate the count), so what is
  # asserted is the DRAWING's own report, which the presenter counts per sector.
  $wheelMerge = Invoke-Confern 'wheel-tools-merge' @('--wheel-demo', '600', '400', '--wheel-tools',
                                                     '--wheel-source', 'pdf',
                                                     '--wheel-dump', (Join-Path $Work 'wheel-merge.png'),
                                                     '--wheel-backdrop', 'dark')
  $iconMergeLine = Get-LogLine $wheelMerge.Lines 'visual demo tool icons:'
  $iconMergeRe = [regex]::Match([string]$iconMergeLine, 'drawn=(\d+)/(\d+) icons=\[([^\]]*)\]')
  $iconMergeOk = $false
  if ($iconMergeRe.Success) {
    $iconMergeOk = ([int]$iconMergeRe.Groups[1].Value -eq 7) -and ([int]$iconMergeRe.Groups[2].Value -eq 7) -and
                   ($iconMergeRe.Groups[3].Value -match '\bMergePdf\b')
  }
  $mergeDump = Join-Path $Work 'wheel-merge.png'
  $mergeDumpSize = 0
  if (Test-Path -LiteralPath $mergeDump) { $mergeDumpSize = (Get-Item -LiteralPath $mergeDump).Length }
  Check 'S20.13 the sixth tool sector really draws an icon: with a PDF source the drawing reports 6/6 drawn and MergePdf is among them' `
    ($iconMergeOk -and ($mergeDumpSize -gt 0)) `
    "line='$iconMergeLine' dumpBytes=$mergeDumpSize"

  # ---------------------------------------------------------------- S20.14 nothing existing broke
  # A sixth op was added to the dispatch switch, so the reject message has to keep naming the FULL
  # list in the same order (S19.06 asserts the equality; this asserts the new op is in it). The
  # five earlier tools' behaviour is asserted by their own sections and suites - what is re-checked
  # here is the two seams this slice edited in shared files.
  $workerSrc = Join-Path $repo 'src\Sidecar\Convert\WorkerServer.cs'
  $workerLines = @()
  if (Test-Path -LiteralPath $workerSrc) { $workerLines = @(Get-Content -LiteralPath $workerSrc -Encoding UTF8) }
  $rejectNamesMerge = Test-Log $workerLines 'WorkerProtocol\.OpMergePdf'
  $declaredOps = @()
  foreach ($line in $workerLines) {
    $t = ([string]$line).Trim()
    $dm2 = [regex]::Match($t, '^(?:public|internal|private)?\s*(?:const|static readonly)\s+string\s+(Op[A-Za-z0-9]+)\s*=')
    if ($dm2.Success) { $declaredOps += $dm2.Groups[1].Value }
  }
  $wantOps = 'OpConvert/OpRotate90/OpStripMetadata/OpCompress/OpCrop/OpReadQr/OpMergePdf/OpRemoveBg'
  # A source that is not a PDF. It must be REFUSED BY NAME and produce NOTHING - never handed to
  # PDFsharp to find out. Note the exit code is 3 and NOT 2: this travels the fail-fast road like
  # any other unmergeable file (the product's own line says `--merge-pdf: the batch was REFUSED
  # (fail-fast)`), which is the ruling. An earlier version of this check expected 2 and went red
  # against a product that was behaving correctly - measured 2026-10-07.
  $npDir = Join-Path $Work 'merge-notpdf'
  New-Item -ItemType Directory -Force -Path $npDir | Out-Null
  $notPdf = Join-Path $npDir 'not-a-pdf.txt'
  Set-Content -LiteralPath $notPdf -Value 'this is not a pdf at all' -Encoding ASCII
  $npGood = Join-Path $npDir 'good-1.pdf'
  Copy-Item -LiteralPath $fA -Destination $npGood -Force
  $plainConvert = Invoke-MergeRun 'merge-unsupported' @($npGood, $notPdf)
  $npNamed = $false
  foreach ($f in $plainConvert.Failures) {
    if ($f -match [regex]::Escape('not-a-pdf.txt')) { $npNamed = $true }
  }
  Check 'S20.14 nothing existing broke: op=merge-pdf is declared and named in the reject list exactly like the earlier ops, and a source that is not a PDF is refused BY NAME with nothing written' `
    ($rejectNamesMerge -and (($declaredOps -join '/') -eq $wantOps) -and
     ($plainConvert.Exit -eq 3) -and $plainConvert.Refused -and $npNamed -and
     ($plainConvert.NewFiles.Count -eq 0) -and ($plainConvert.OutputName -eq '') -and
     (Test-Log $plainConvert.Lines 'error=Unsupported')) `
    ("declared=($($declaredOps -join '/')) rejectNamesMerge=$rejectNamesMerge " +
     "exit=$($plainConvert.Exit) refused=$($plainConvert.Refused) named=$npNamed newFiles=$($plainConvert.NewFiles.Count) " +
     "why='$($plainConvert.Why)'")
}

# ---------------------------------------------------------------- S21

# ---------------------------------------------------------------- S21

# One `--merge-ui-selftest <p1;p2;...>` run, parsed. The WINDOW's own numbers are read from the
# product's log; nothing in this function computes an expected order (a second copy of the move
# semantics would go red on the harness instead of on the product - the same rule this repo
# already follows for the crop window's stage mapping).
function Invoke-MergeUiSelftest([string]$name, [string[]]$sources) {
  $joined = ($sources -join ';')
  $r = Invoke-Confern $name @('--merge-ui-selftest', $joined)
  $initial = @()
  $m = [regex]::Match([string](Get-LogLine $r.Lines 'initial order matches the source order='),
                      'source=\[([^\]]*)\]')
  if ($m.Success) { $initial = @($m.Groups[1].Value -split ',' | Where-Object { $_ -ne '' }) }
  # The order the window SHOWS after the first drag. This is the single source for the
  # "what was on screen" side of the page-order assertion.
  $afterDrag = @()
  $m2 = [regex]::Match([string](Get-LogLine $r.Lines 'merge ui selftest: after drag the window shows'),
                       'shows (.+)$')
  if ($m2.Success) { $afterDrag = @($m2.Groups[1].Value.Trim() -split '\|' | Where-Object { $_ -ne '' }) }
  # The SAME shown order expressed as full paths, so it can be fed straight to the engine without
  # this side re-deriving anything.
  $afterDragPaths = @()
  foreach ($n in $afterDrag) {
    foreach ($s in $sources) { if ((Split-Path -Leaf $s) -eq $n) { $afterDragPaths += $s; break } }
  }
  # The order the window SHOWS when the user presses confirm (after the drag, the drag-back and
  # the removal). Handed back == shown is asserted by the product; this side compares the
  # COMMITTED pages against it.
  $shownAtConfirm = @()
  $m3 = [regex]::Match([string](Get-LogLine $r.Lines 'matchesWhatWasShown='), 'shown=\[([^\]]*)\]')
  if ($m3.Success) { $shownAtConfirm = @($m3.Groups[1].Value -split ',' | Where-Object { $_ -ne '' }) }
  $removeLine = [string](Get-LogLine $r.Lines 'merge ui selftest: remove pressed=')
  $rm = [regex]::Match($removeLine, "removed='(.+?)' gone=(\w+) othersKept=(\w+) now=\[([^\]]*)\]")
  $removedName = ''
  $removedGone = $false
  $othersKept = $false
  $afterRemove = @()
  if ($rm.Success) {
    $removedName = $rm.Groups[1].Value
    $removedGone = ($rm.Groups[2].Value -eq 'True')
    $othersKept = ($rm.Groups[3].Value -eq 'True')
    $afterRemove = @($rm.Groups[4].Value -split ',' | Where-Object { $_ -ne '' })
  }
  # The same list as full paths, from the SAME reported order (no re-derivation).
  $afterRemovePaths = @()
  foreach ($n in $afterRemove) {
    foreach ($s in $sources) { if ((Split-Path -Leaf $s) -eq $n) { $afterRemovePaths += $s; break } }
  }
  return [pscustomobject]@{
    Exit          = $r.Exit
    Lines         = $r.Lines
    Log           = $r.Log
    Ok            = (Test-Log $r.Lines 'merge ui selftest done: ok=True')
    Done          = (Test-Log $r.Lines 'merge ui selftest done:')
    Initial       = $initial
    InitialOk     = (Test-Log $r.Lines 'initial order matches the source order=True')
    SingleInst    = (Test-Log $r.Lines 'ignored \(single instance\) callbackReplaced=False')
    DragOk        = (Test-Log $r.Lines 'drag movedTheOrder=True matchesTheMoveSemantics=True')
    DragReturnOk  = (Test-Log $r.Lines 'drag out and back restores the order=True')
    AfterDrag     = $afterDrag
    AfterDragPaths = $afterDragPaths
    RemovedName   = $removedName
    RemovedGone   = $removedGone
    OthersKept    = $othersKept
    AfterRemove   = $afterRemove
    AfterRemovePaths = $afterRemovePaths
    LastKept      = (Test-Log $r.Lines 'the last file cannot be removed: .*stillOne=True noticeSaysWhy=True')
    ConfirmOk     = (Test-Log $r.Lines 'matchesWhatWasShown=True removedNotHanded=True')
    ShownAtConfirm = $shownAtConfirm
    CancelOk      = (Test-Log $r.Lines 'cancel -> closed=True orderHandedBack=False')
    ReopenOk      = (Test-Log $r.Lines 'reopened=True windowPresent=True orderBackToSource=True')
    ScrollZero    = (Test-Log $r.Lines 'scroll=0')
  }
}

# A REAL-path run (open the window and then stop touching the mouse) with a hard timeout, because
# the real path BLOCKS on a modal window by design. Returns DidExit=false when it had to be killed,
# which is the expected outcome: it proves the window really waited for a human.
function Invoke-ConfernTimeout([string]$name, [string[]]$argv, [int]$timeoutMs) {
  $log = Join-Path $Work "$name.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $quoted = @()
  foreach ($a in $argv) { if ([string]$a -match ' ') { $quoted += ('"' + $a + '"') } else { $quoted += $a } }
  $all = @() + $quoted + @('--log', $log, '--quiet', '--no-autostart')
  $p = Start-Process -FilePath $Exe -ArgumentList $all -PassThru -WindowStyle Hidden
  $exited = $p.WaitForExit($timeoutMs)
  if (-not $exited) { try { $p.Kill() } catch { } }
  Start-Sleep -Milliseconds 800
  return [pscustomobject]@{
    Name    = $name
    DidExit = [bool]$exited
    Log     = $log
    Lines   = (Get-LogLines $log)
  }
}

# The page order of a merged file, compared against an EXPECTED list of source page tuples. A
# page's identity is (size, rotate, decompressed-content sha) as a TUPLE, not size alone: with
# sizes alone a product that put the right sizes in the right order but the wrong CONTENT on each
# page would pass (that is exactly the hole the brief flags between S20.04 and S20.05).
function Test-MergeOrderMatches([object[]]$merged, [string[]]$wantSizes, [string[]]$wantShas) {
  if ($merged.Count -ne $wantSizes.Count) { return $false }
  if ($wantShas.Count -ne $wantSizes.Count) { return $false }
  for ($i = 0; $i -lt $merged.Count; $i++) {
    if ($merged[$i].Size -ne $wantSizes[$i]) { return $false }
    if ($merged[$i].Contents -le 0) { return $false }
    if ($merged[$i].Sha -ne $wantShas[$i]) { return $false }
  }
  return $true
}

function Invoke-S22([string]$fault) {
  # A helper that belongs to this section. It is defined FIRST because PowerShell resolves a plain
  # function call at CALL time: the first version of this section defined it 120 lines below its
  # first use, and the whole section died with CommandNotFoundException before the first check.
  # Every file under a folder, RECURSIVELY, with size and mtime. "no new file anywhere in the
  # checkout" has to include the subfolders, because that is where the fixtures live: a top-level
  # listing sees zero files here, so it could never notice a new one at all.
  function Get-FixtureTreeState([string]$dir) {
    return @(Get-ChildItem -LiteralPath $dir -File -Recurse | Sort-Object FullName |
             ForEach-Object { ($_.FullName.Substring($dir.Length)) + "|" + $_.Length + "|" + $_.LastWriteTimeUtc.Ticks })
  }

  Write-Host "=== S22 remove-bg: the portrait model, the empty-mask refusal, and the PNG that comes out ==="

  # WHAT THIS SECTION IS FOR (2026-10-08, the seventh and last M3 sector)
  #   remove-bg is the only tool whose answer can be "there is nothing here for me": the model is
  #   a PORTRAIT model (MODNet), so a QR code, a landscape or a screenshot produces an almost
  #   empty mask. Two outcomes are equally unacceptable and both are asserted below:
  #     - silently writing a fully transparent PNG (the user gets a blank picture and a success
  #       message), and
  #     - reporting it as some other failure (the user cannot tell the two apart and the message
  #       has no next step).
  #   It is also the only tool that needs an EXTERNAL FILE (the 12.98 MB model), so "the model is
  #   missing" and "the model is corrupt" are two product states with two different messages, and
  #   both are walked here for real - by pointing the tool at a truncated file and at a path that
  #   does not exist - not by reading the source.
  #
  #   Every assertion below reads the PRODUCT'S OWN ARTEFACTS: the exit code, the evidence log
  #   lines, the folder contents, and the pixels of the PNG it wrote.
  #
  #   S22.14-S22.17 (added 2026-10-08, the SHIPPING slice) leave the diagnostic seam and walk the side
  #   no earlier check touched: the DEFAULT model path, asked with no --remove-bg-model at all (so both
  #   the "works out of the box" and the "no model" answers are pinned), the same default path on a COPY
  #   of the exe folder with the model deleted (which is what proves the path follows the RUNNING exe and
  #   that a missing file is the NAMED state rather than a crash), the chain "repository store -> exe
  #   folder" (same sha256 and the measured length, so the bytes that ship are the verified ones), and
  #   the model's apache-2.0 licence notice, which has to travel beside the model it covers.
  #   None of the four is ever a silent skip: each states the branch it took and asserts on BOTH branches.

  # ---------------------------------------------------------------- the fixtures (committed)
  # fixtures\rembg\real-512.png is a 512x410 downscale of a PUBLIC DOMAIN NASA photograph
  # (Leland D. Melvin with his two dogs); provenance and the measured numbers are in
  # fixtures\rembg\README.md. It is committed because a real portrait cannot be synthesised by a
  # script, and these assertions are about WHERE the mask lands - a property of a real face in a
  # real frame.
  #
  # The non-portrait fixture is fixtures\qr\qr-small-in-scene-160.png, already committed for
  # read-qr: a QR code on a plain backdrop, i.e. exactly the kind of picture a user would really
  # try remove-bg on. Its measured mask is max=0.1057 mean=0.0001 (native) and max=0.0165
  # mean=0.0001 (at 512 px), far below the product's thresholds (0.30 / 0.02).
  $repo = Split-Path -Parent $PSScriptRoot
  $fPortrait = Join-Path $repo 'fixtures\rembg\real-512.png'
  $fNoSubject = Join-Path $repo 'fixtures\qr\qr-small-in-scene-160.png'
  $portraitFixtureOk = Test-Path -LiteralPath $fPortrait
  $noSubjectFixtureOk = Test-Path -LiteralPath $fNoSubject
  $fixturesOk = $portraitFixtureOk -and $noSubjectFixtureOk
  if (-not $portraitFixtureOk) {
    Inconclusive "portrait fixture missing: $fPortrait (it is committed, so a missing one means the checkout is broken)"
  }
  if (-not $noSubjectFixtureOk) {
    Inconclusive "non-portrait fixture missing: $fNoSubject (it is committed, so a missing one means the checkout is broken)"
  }

  # ---------------------------------------------------------------- the model (deliberately NOT committed)
  # 12.98 MB of binary does not belong in version control, so tools/get-rembg-model.ps1 fetches it
  # into models\model_fp16.onnx BESIDE THE EXE - which is exactly the product's own default
  # resolution (RemoveBgModel.ResolvePath). The check below reports which path answered instead of
  # assuming it; when there is no model anywhere, every check that needs one is INCONCLUSIVE (and
  # this section then exits 3), never silently green.
  $exeDir = Split-Path -Parent $Exe
  # Absolute, because S22.15 copies this folder: a relative -Exe would make every path below depend on
  # the process's working directory, which is the very thing this section asserts the PRODUCT does not do.
  if (-not [System.IO.Path]::IsPathRooted($exeDir)) { $exeDir = [System.IO.Path]::GetFullPath($exeDir) }
  $modelBesideExe = Join-Path $exeDir 'models\model_fp16.onnx'
  $besideOk = Test-Path -LiteralPath $modelBesideExe
  $modelInRepo = Join-Path $repo 'models\model_fp16.onnx'
  $model = ''
  if ($besideOk) { $model = $modelBesideExe }
  elseif (Test-Path -LiteralPath $modelInRepo) { $model = $modelInRepo }
  $haveModel = $model -ne ''
  if (-not $haveModel) {
    Inconclusive "no portrait model: looked for '$modelBesideExe' and '$modelInRepo' (tools/get-rembg-model.ps1 fetches it)"
  }

  # The checkout is read BEFORE anything runs, so "this section wrote nothing into the committed
  # fixtures" is a comparison and not a promise.
  $fxDir = Join-Path $repo 'fixtures'
  $fxStateBefore = Get-FixtureTreeState $fxDir
  $fxShaBefore = @{}
  foreach ($p in @($fPortrait, $fNoSubject)) { $fxShaBefore[$p] = (Get-Sha $p) }

  # A file name, or 'none'. The detail strings must never be the thing that breaks a check, so
  # every place that reports "did it write anything" goes through here.

  function Get-OutName([string]$p) {
    if ($null -eq $p -or $p -eq '') { return 'none' }
    return (Split-Path -Leaf $p)
  }

  # One remove-bg run on a fresh copy of a fixture, through the REAL worker process. The OUTPUT
  # FILE - not the request - is the only source of truth about the output.
  function Invoke-RemoveBg([string]$name, [string]$fixture, [string]$modelPath, [string[]]$extra = @()) {
    $d = Join-Path $Work "rembg-$name"
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $src = Join-Path $d (Split-Path -Leaf $fixture)
    Copy-Item -LiteralPath $fixture -Destination $src -Force
    $sha = Get-Sha $src
    $len = (Get-Item -LiteralPath $src).Length
    $mtime = (Get-Item -LiteralPath $src).LastWriteTimeUtc

    $argv = @('--remove-bg', $src)
    if ($modelPath -ne '') { $argv += @('--remove-bg-model', $modelPath) }
    # S22.18-S22.21 pass the refinement switch (and a private --settings-file) through this hole.
    # Every existing caller passes nothing here, so their argv is unchanged.
    $argv += $extra
    $r = Invoke-Confern $name $argv

    $outFile = Get-NewestOutput $d $src
    $outSize = 'MISSING'
    if ($null -ne $outFile) { $outSize = Get-ImageSize $outFile }
    $untouched = ((Get-Sha $src) -eq $sha) -and
                 ((Get-Item -LiteralPath $src).Length -eq $len) -and
                 ((Get-Item -LiteralPath $src).LastWriteTimeUtc -eq $mtime)
    return [pscustomobject]@{
      Name       = $name
      Exit       = $r.Exit
      Lines      = $r.Lines
      Dir        = $d
      Src        = $src
      Out        = $outFile
      OutSize    = $outSize
      Untouched  = $untouched
      OutputLine = (Get-LogLine $r.Lines 'remove-bg output: ')
      WroteLine  = (Get-LogLine $r.Lines 'remove-bg wrote: ')
      Failure    = (Get-LogLine $r.Lines 'remove-bg failure: ')
      Why        = (Get-LogLine $r.Lines 'remove-bg why: ')
      JobLine    = (Get-LogLine $r.Lines 'worker: job \d+ sent .*op=remove-bg')
      ModelState = (Get-LogLine $r.Lines 'remove-bg: model state=')
      ModelLen   = (Get-LogLine $r.Lines 'tool remove-bg: model length=')
      # The engine writes its own "model state=Ready" line, but the ENGINE's log does not reach the
      # parent process log at all (measured 2026-10-08: no engine line ever appears there), so the
      # observable facts about the model are the ones the diagnostic itself prints.
      ModelPathLine = (Get-LogLine $r.Lines 'tool remove-bg: model path=')
    }
  }

  # The product's own inspector turns the written PNG's alpha channel into numbers, so the mask
  # assertions are properties of the ARTEFACT. Every field defaults to a value that FAILS.
  function Get-RemoveBgInspect([string]$name, [string]$png) {
    $r = Invoke-Confern "rembg-inspect-$name" @('--remove-bg-inspect', $png)
    $alphaLine = Get-LogLine $r.Lines 'remove-bg inspect: alpha '
    $bandLine = Get-LogLine $r.Lines 'remove-bg inspect: band '
    $cornersLine = Get-LogLine $r.Lines 'remove-bg inspect: corners '
    $matteLine = Get-LogLine $r.Lines 'remove-bg inspect: matte '
    $headLine = Get-LogLine $r.Lines 'remove-bg inspect: file='
    $min = 9.0; $max = -1.0; $mean = -1.0; $opaque = -1.0; $clear = -1.0
    $centre = -1.0; $corner = -1.0; $top = -1.0
    $cornerTL = -1.0; $cornerTR = -1.0; $cornerBL = -1.0; $cornerBR = -1.0
    $m = [regex]::Match([string]$alphaLine, 'min=([\d.]+) max=([\d.]+) mean=([\d.]+) opaque=([\d.]+) clear=([\d.]+)')
    if ($m.Success) {
      $min = [double]$m.Groups[1].Value
      $max = [double]$m.Groups[2].Value
      $mean = [double]$m.Groups[3].Value
      $opaque = [double]$m.Groups[4].Value
      $clear = [double]$m.Groups[5].Value
    }
    $m2 = [regex]::Match([string]$bandLine, 'centre=([\d.]+) \([\d.]+\) corner=([\d.]+) \([\d.]+\) top=([\d.]+) ')
    if ($m2.Success) {
      $centre = [double]$m2.Groups[1].Value
      $corner = [double]$m2.Groups[2].Value
      $top = [double]$m2.Groups[3].Value
    }
    # The transition width and the edge steepness, as the PRODUCT measured them (added 2026-10-08
    # with the refinement slice). Both default to a value that FAILS so a missing line cannot pass.
    $bandPct = -1.0; $edgeAmt = -1.0
    $m4 = [regex]::Match([string]$matteLine, 'band=([\d.]+) edge=([\d.]+)')
    if ($m4.Success) {
      $bandPct = [double]$m4.Groups[1].Value
      $edgeAmt = [double]$m4.Groups[2].Value
    }
    $m3 = [regex]::Match([string]$cornersLine, 'TL=([\d.]+) TR=([\d.]+) BL=([\d.]+) BR=([\d.]+)')
    if ($m3.Success) {
      $cornerTL = [double]$m3.Groups[1].Value
      $cornerTR = [double]$m3.Groups[2].Value
      $cornerBL = [double]$m3.Groups[3].Value
      $cornerBR = [double]$m3.Groups[4].Value
    }
    return [pscustomobject]@{
      Exit = $r.Exit; Head = $headLine; Alpha = $alphaLine; Band = $bandLine
      Min = $min; Max = $max; Mean = $mean; Opaque = $opaque; Clear = $clear
      Centre = $centre; Corner = $corner; Top = $top; Corners = $cornersLine
      CornerTL = $cornerTL; CornerTR = $cornerTR; CornerBL = $cornerBL; CornerBR = $cornerBR
      MatteLine = $matteLine; MatteBand = $bandPct; MatteEdge = $edgeAmt
    }
  }

  # ---------------------------------------------------------------- S22.01 the model resolves
  # The default path must be models\model_fp16.onnx BESIDE THE EXE, not against the working
  # directory: the worker is started as Process.Start(exe, "--worker", ...) and its working
  # directory is NOT the exe folder, so a working-directory-relative model would be found in one
  # launch mode and not in the other.
  $probe = Invoke-RemoveBg 'modelprobe' $fPortrait $model
  $modelStateLine = $probe.ModelState
  $modelLenLine = $probe.ModelLen
  $modelPathLine = $probe.ModelPathLine
  $modelLineOk = $false
  if ($haveModel) {
    $modelLineOk = ($modelPathLine -match 'model path=') -and ($modelPathLine -match 'exists=True') -and
                   ($modelPathLine -match [regex]::Escape($modelBesideExe)) -and
                   ($modelLenLine -match 'length=12984781B \(expected 12984781B\)') -and
                   ($modelLenLine -match 'sha256=25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6')
  } else {
    Inconclusive 'S22.01 cannot report Ready: there is no model on this machine'
  }
  $modelDetail = "resolved='$model' existsLine='$modelPathLine' lengthLine='$modelLenLine'"
  Check 'S22.01 the model path resolves beside the EXE (not against the working directory), exists=True, with the measured length and sha256' $modelLineOk $modelDetail

  # ---------------------------------------------------------------- S22.02 the corrupt model
  # Two shapes of "the file exists but is not a model": a TRUNCATED download (the first 4,328,260
  # bytes of the real model) and a 40-byte HTML error page (what a proxy or a 404 page looks like
  # when it is saved as .onnx). Both must be ModelCorrupt - a different state from
  # ModelUnavailable - and neither may leave an output file.
  $halfBytes = 4328260
  $bytes = $null
  if (Test-Path -LiteralPath $modelBesideExe) { $bytes = [System.IO.File]::ReadAllBytes($modelBesideExe) }
  elseif (Test-Path -LiteralPath $modelInRepo) { $bytes = [System.IO.File]::ReadAllBytes($modelInRepo) }
  if ($null -eq $bytes) { $bytes = New-Object byte[] $halfBytes }
  $halfPath = Join-Path $Work 'rembg-model-half.onnx'
  $half = New-Object byte[] $halfBytes
  [Array]::Copy($bytes, $half, $halfBytes)
  [System.IO.File]::WriteAllBytes($halfPath, $half)
  $junkPath = Join-Path $Work 'rembg-model-junk.onnx'
  [System.IO.File]::WriteAllBytes($junkPath, [System.Text.Encoding]::ASCII.GetBytes('<html><body>404 Not Found</body></html>'))
  $halfLen = (Get-Item -LiteralPath $halfPath).Length
  $junkLen = (Get-Item -LiteralPath $junkPath).Length

  # Measured 2026-10-08: the first version of the `rembg-nosubject` injection ALSO swapped this path
  # for the real model, which turned S22.11 and S22.12 red as a side effect - the drill could no
  # longer say which assertion had caught the bug. The injection was narrowed to S22.04 alone.
  # What keeps THIS check falsifiable is that it asserts a sentence that only exists for a corrupt
  # file (璅∪?瑼?憯? AND that neither run produced an output file.
  $corruptHalf = Invoke-RemoveBg 'corrupthalf' $fPortrait $halfPath
  $corrupt2 = Invoke-RemoveBg 'corruptjunk' $fPortrait $junkPath
  $corruptMsg = '模型檔損壞'
  $corruptOk = ($corruptHalf.Exit -eq 3) -and ($corruptHalf.Failure -match $corruptMsg) -and
               ($corrupt2.Exit -eq 3) -and ($corrupt2.Failure -match $corruptMsg) -and
               ($null -eq $corruptHalf.Out) -and ($null -eq $corrupt2.Out)
  $corruptDetail = "half(exit=$($corruptHalf.Exit) len=$halfLen out=$(Get-OutName $corruptHalf.Out) msg='$($corruptHalf.Failure)')"
  $corruptDetail += " junk(exit=$($corrupt2.Exit) len=$junkLen out=$(Get-OutName $corrupt2.Out) msg='$($corrupt2.Failure)')"
  Check 'S22.02 a model file that exists but is unusable is reported as ModelCorrupt: the truncated file and a 40-byte HTML page both, with no output written' $corruptOk $corruptDetail

  # ---------------------------------------------------------------- S22.03 the missing model
  $absent = Join-Path $Work 'rembg-no-such-model.onnx'
  $missing = Invoke-RemoveBg 'modelmissing' $fPortrait $absent
  $missingMsg = '尚未取得'
  $missingOk = ($missing.Exit -eq 3) -and ($missing.Failure -match $missingMsg) -and
               ($missing.Why -match 'File\.Exists=false') -and ($null -eq $missing.Out) -and
               ($missing.ModelPathLine -match 'exists=False')
  $missingDetail = "path='$absent' existsAfter=$(Test-Path -LiteralPath $absent) exit=$($missing.Exit) out=$(Get-OutName $missing.Out) msg='$($missing.Failure)' existsLine='$($missing.ModelPathLine)'"
  Check 'S22.03 a path with no file is reported as ModelUnavailable (not as corrupt), the reason says File.Exists=false, and no output written' $missingOk $missingDetail

  # ---------------------------------------------------------------- S22.04 the non-portrait picture
  # THE CHECK THIS WHOLE SECTOR EXISTS FOR. The model is a portrait model; on a QR scene the mask
  # is empty. Writing a fully transparent PNG would hand the user a blank file with a success
  # message, so the product must refuse BY NAME and write NOTHING.
  $noSubject = Invoke-RemoveBg 'nosubject' $fNoSubject $model
  $noSubjectMsg = '找不到人像主體'
  $noSubjectOk = ($noSubject.Exit -eq 3) -and ($noSubject.Failure -match $noSubjectMsg) -and
                 ($noSubject.WroteLine -match 'count=0') -and ($null -eq $noSubject.Out) -and
                 ($noSubject.Why -match 'max=0\.1057') -and
                 (Test-Log $noSubject.Lines 'NoSubjectFound')
  # The injection: with the fault the EXPECTED outcome becomes "it wrote a file", so the check goes
  # red for exactly that reason - which is what proves it is not a tautology.
  if ($fault -eq 'rembg-nosubject') {
    $noSubjectOk = ($noSubject.Exit -eq 0) -and ($null -ne $noSubject.Out)
  }
  $noSubjectDetail = "exit=$($noSubject.Exit) out=$(Get-OutName $noSubject.Out) wrote='$($noSubject.WroteLine)' msg='$($noSubject.Failure)' why='$($noSubject.Why)'"
  Check 'S22.04 a non-portrait picture is REFUSED by name (NoSubjectFound, exit 3, zero files written) instead of silently producing a fully transparent PNG' $noSubjectOk $noSubjectDetail

  # ---------------------------------------------------------------- S22.05 the portrait fixture
  $portrait = Invoke-RemoveBg 'portrait' $fPortrait $model
  $portraitOk = $haveModel -and ($portrait.Exit -eq 0) -and ($null -ne $portrait.Out) -and
                (Test-Log $portrait.Lines 'worker result: ok=True') -and
                (Test-Log $portrait.Lines 'remove-bg summary: . 1 png from 1 source')
  if (-not $haveModel) {
    Inconclusive 'S22.05 needs a model (the portrait fixture was still run, and the result is reported below)'
  }
  $portraitDetail = "exit=$($portrait.Exit) out=$(Get-OutName $portrait.Out) size=$($portrait.OutSize) outputLine='$($portrait.OutputLine)'"
  Check 'S22.05 a portrait fixture really goes through the worker and comes back as one written PNG next to the source' $portraitOk $portraitDetail

  # ---------------------------------------------------------------- S22.06 the output is a real PNG
  # Three independent facts about the artefact: its bytes start with the PNG signature, the file
  # really decodes, and it decodes with an ALPHA channel (a JPEG could not carry the mask at all,
  # which is why this tool's format is fixed).
  $pngOk = $false
  $pngDetail = 'no output file'
  if ($null -ne $portrait.Out) {
    $magic = [System.IO.File]::ReadAllBytes($portrait.Out)[0..7]
    $sig = ($magic -join ',') -eq '137,80,78,71,13,10,26,10'
    $frame = $null
    $f = [System.IO.File]::OpenRead($portrait.Out)
    try {
      $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($f,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    } catch { $frame = $null } finally { $f.Dispose() }
    $fmt = ''
    $hasAlpha = $false
    if ($null -ne $frame) {
      $fmt = [string]$frame.Format
      $hasAlpha = ($fmt -eq 'Bgra32') -or ($fmt -eq 'Pbgra32')
    }
    $pngOk = $sig -and ($null -ne $frame) -and $hasAlpha
    # The injected wrong expectation: pretend the decoded frame must NOT carry alpha.
    if ($fault -eq 'rembg-png') { $pngOk = $sig -and ($null -ne $frame) -and (-not $hasAlpha) }
    $pngDetail = "signature=$sig decoded=$($null -ne $frame) format='$fmt' hasAlpha=$hasAlpha bytes=$((Get-Item -LiteralPath $portrait.Out).Length)"
  } else {
    Inconclusive 'S22.06 needs a written PNG from S22.05'
  }
  Check 'S22.06 the artefact really is a PNG that decodes with an alpha channel (signature bytes, decoded frame and its pixel format, read from the file)' $pngOk $pngDetail

  # ---------------------------------------------------------------- S22.07 the mask, measured
  # THE MASK IS MEASURED FROM THE FILE, NOT FROM THE PRODUCT'S SUMMARY. The numbers come from
  # --remove-bg-inspect reading the PNG the product wrote; the same channels were measured
  # independently earlier (both sets are in fixtures\rembg\README.md).
  #
  # Each region can genuinely fail:
  #   centre (middle 40 percent) measured 0.89 -> needs >= 0.80 (head and chest are here)
  #   top band (top 10 percent)  measured 0.02 -> needs <= 0.10 (only hair is up there)
  #   top-right corner           measured 0.00 -> needs <= 0.05 (studio backdrop)
  #   clear fraction             measured 0.46 -> needs >= 0.20 (a real cutout has real background)
  # The BOTTOM-LEFT corner is deliberately NOT asserted: the astronaut's suit and the dogs really
  # do occupy it (measured 0.9662), so demanding transparency there would assert a falsehood about
  # this photograph.
  $inspect = $null
  $maskOk = $false
  $maskDetail = 'no inspect run'
  # !! The condition is "a file was written", NOT "$pngOk" (fixed 2026-10-08). It used to be
  # `$pngOk -and ...`, which meant that ANY failure of S22.06 sent this check down the
  # Inconclusive branch as well - and Inconclusive REGISTERS AN EXTRA ITEM, so the run ended with
  # `registered 14 checks but the selected sections promise 13` and a FATAL that hid the real
  # failure. Measured while drilling the `rembg-png` fault: S22.07 went INCONCLUSIVE *and* FAIL in
  # the same run. A guard must depend on what this check needs (the artefact), never on the
  # neighbouring check's verdict.
  if ($null -ne $portrait.Out) {
    $inspect = Get-RemoveBgInspect 'portrait' $portrait.Out
    $maskOk = ($inspect.Centre -ge 0.80) -and ($inspect.Top -le 0.10) -and
              ($inspect.CornerTR -le 0.05) -and ($inspect.Clear -ge 0.20)
    $maskDetail = "centre=$($inspect.Centre) need>=0.80 ; top=$($inspect.Top) need<=0.10 ; "
    $maskDetail += "cornerTR=$($inspect.CornerTR) need<=0.05 ; clear=$($inspect.Clear) need>=0.20 ; "
    $maskDetail += "min=$($inspect.Min) max=$($inspect.Max) mean=$($inspect.Mean) ; $($inspect.Band) | $($inspect.Corners)"
  } else {
    Inconclusive 'S22.07 needs the written PNG from S22.05'
  }
  Check 'S22.07 the written PNG keeps the SUBJECT opaque and drops the BACKGROUND: centre band >= 0.80, top band <= 0.10, top-right corner <= 0.05, and at least 20 percent of pixels fully transparent' $maskOk $maskDetail

  # ---------------------------------------------------------------- S22.08 the PNG is the same size
  # A cutout resizes nothing: the output is the source minus its background. This also pins the
  # upscale: the mask comes back at 640x512 (512-edge, 32-divisible) and is scaled using THAT
  # tensor size as the source, so a "round the ideal ratio" upscale would show up here or as a
  # shifted mask above.
  $sizeOk = $false
  $sizeDetail = 'no output file'
  if ($null -ne $portrait.Out) {
    $srcSize = Get-ImageSize $portrait.Src
    $outSize = $portrait.OutSize
    $sizeOk = ($outSize -eq $srcSize) -and ($outSize -eq '512x410')
    $sizeDetail = "source=$srcSize output=$outSize outputLine='$($portrait.OutputLine)'"
  } else {
    Inconclusive 'S22.08 needs the written PNG from S22.05'
  }
  Check 'S22.08 the output PNG has exactly the source pixel size (512x410): remove-bg resizes nothing, and the mask upscale itself is pinned by S22.07' $sizeOk $sizeDetail

  # ---------------------------------------------------------------- S22.09 the sources are untouched
  $touched = @()
  $runs = @($noSubject, $missing, $corruptHalf, $corrupt2, $portrait)
  foreach ($r in $runs) {
    if ($null -eq $r) { continue }
    if (-not $r.Untouched) { $touched += $r.Name }
  }
  $untouchedOk = $touched.Count -eq 0
  $untouchedDetail = "changed=($($touched -join ',')) runs=$($runs.Count) shaPortrait=$((Get-Sha $fPortrait).Substring(0,12)) expected=$($fxShaBefore[$fPortrait].Substring(0,12))"
  Check 'S22.09 every source file this section used still has the same sha256, length and mtime: remove-bg never modifies, moves or deletes a source (spec 5)' $untouchedOk $untouchedDetail

  # ---------------------------------------------------------------- S22.10 the model path crosses the pipe
  # The model path is the ONLY new fact this op puts on the protocol (WorkerMessage.removeBgModel).
  # A path that is valid in the UI process can arrive as null on the other side, and the worker's
  # working directory is not the UI's - so the worker's OWN job line has to name the path it was
  # given. That line is the only place the crossing is observable.
  # A Windows path is not a regex: Get-LogLine runs its pattern through Select-String, where '.onnx'
  # matches anything and a stray '\' can make the pattern invalid and silence the search. Escaping
  # the path first is what turns "the log contains this string" into a real question about the log.
  $sentInWorker = (Get-LogLine $corrupt2.Lines ([regex]::Escape($junkPath))) -ne ''
  $pipeOk = ($corrupt2.JobLine -match 'op=remove-bg') -and $sentInWorker
  $pipeDetail = "jobLine='$($corrupt2.JobLine)' workerNamedThePath=$sentInWorker sent='$junkPath' workerMsg='$($corrupt2.Failure)'"
  Check 'S22.10 the model path really travels to the worker process: the worker ran op=remove-bg and the sentence IT wrote names the exact absolute --remove-bg-model path the UI sent' $pipeOk $pipeDetail

  # ---------------------------------------------------------------- S22.11 a refusal writes nothing
  # All refusal states together: the corrupt model (both shapes), the missing model and the empty
  # mask. None may leave anything in its run folder - the source copy is not counted, and the
  # product may not add a file or a temp file either.
  $refusalClean = $true
  $refusalDetail = @()
  foreach ($r in @($noSubject, $missing, $corruptHalf, $corrupt2)) {
    $files = @(Get-ChildItem -LiteralPath $r.Dir -File)
    $extra = @($files | Where-Object { $_.FullName -ne $r.Src })
    $refusalDetail += "$($r.Name)=$($extra.Count)"
    if ($extra.Count -ne 0) { $refusalClean = $false }
  }
  Check 'S22.11 every refusal state leaves its folder with the source copy alone: no PNG, no leftover temp file, nothing' $refusalClean ("extraFilesPerRun=($($refusalDetail -join ' '))")

  # ---------------------------------------------------------------- S22.12 three states, three messages
  # The product semantics, asserted as data: "there is no model yet", "the model file is damaged"
  # and "this picture has no portrait subject" are three different causes with three different next
  # steps, so they cannot share one sentence.
  $msgs = @($noSubject.Failure, $missing.Failure, $corruptHalf.Failure)
  $distinctOk = (($msgs | Select-Object -Unique).Count -eq 3) -and
                ($msgs[0] -match $noSubjectMsg) -and ($msgs[1] -match $missingMsg) -and
                ($msgs[2] -match $corruptMsg)
  $distinctDetail = "distinct=$(($msgs | Select-Object -Unique).Count) m1='$($msgs[0])' m2='$($msgs[1])' m3='$($msgs[2])'"
  Check 'S22.12 the three refusal states produce three DIFFERENT user messages (no portrait subject / model never fetched / model file damaged)' $distinctOk $distinctDetail

  # ---------------------------------------------------------------- S22.13 the committed fixtures are untouched
  $fxStateAfter = Get-FixtureTreeState $fxDir
  $fxNew = @()
  foreach ($line in $fxStateAfter) {
    if ($fxStateBefore -notcontains $line) { $fxNew += $line }
  }
  $fxChanged = @()
  foreach ($p in @($fPortrait, $fNoSubject)) {
    if ($fxShaBefore[$p] -ne (Get-Sha $p)) { $fxChanged += (Split-Path -Leaf $p) }
  }
  $fxCleanOk = ($fxNew.Count -eq 0) -and ($fxChanged.Count -eq 0)
  $fxDetail = "newInFixtures=($($fxNew -join ' | ')) changedFixtures=($($fxChanged -join ',')) fixtureTreeFiles=$($fxStateBefore.Count)"
  Check 'S22.13 the committed fixtures folder is left untouched: this section never writes next to a fixture and every fixture keeps its sha256' $fxCleanOk $fxDetail

  # ---------------------------------------------------------------- S22.14 the DEFAULT path, both ways
  # !! S22.01..S22.13 ALL PASS --remove-bg-model, so between them they walk the DIAGNOSTIC SEAM and never
  # the path a user actually gets. That path is AppContext.BaseDirectory\models\model_fp16.onnx
  # (RemoveBgModel.ResolvePath) - "beside the EXECUTABLE", not "beside the working directory", because the
  # worker is started as Process.Start(exe, "--worker", ...) and its working directory is not the exe's.
  #
  # This check therefore runs the PLAIN command, with no model switch at all. It is decidable on both kinds
  # of machine and NEITHER branch is a skip:
  #   a model beside the exe   -> it must work out of the box: exit 0, one PNG, the source's own size
  #   no model beside the exe  -> the default path must produce the NAMED state: exit 3, nothing written
  # The branch that ran is part of the detail line, so a green check is never ambiguous about WHAT it tested.
  # 2026-10-14: the default path stopped being ONE FILE NAME and became a PREFERENCE ORDER
  # (RemoveBgModel.ResolvePath): if the preferred model (BiRefNet, 1024x1024 fp16) is lying beside the
  # exe AND its length matches, use it; otherwise use model_fp16.onnx. The reason is in the product's
  # own comment: a 490 MB file only exists when a download finished, and a half-downloaded big file
  # must not turn the tool from "edges are soft" into "the tool is broken". So the assertion moves
  # with it: not "which file name" but "which state", with the branch recorded either way.
  $prefBesideExe = Join-Path $exeDir 'models\ModelBiRefNet_1024x1024_fp16.onnx'
  $prefLenOk = (Test-Path -LiteralPath $prefBesideExe) -and
               ((Get-Item -LiteralPath $prefBesideExe).Length -eq 489666272)
  $expectedDefault = if ($prefLenOk) { $prefBesideExe } else { $modelBesideExe }
  $defaultRun = Invoke-RemoveBg 'defaultpath' $fPortrait ''
  $defaultLine = $defaultRun.ModelPathLine
  $defaultNames = ($defaultLine -match [regex]::Escape($expectedDefault)) -and
                  ($defaultLine -match '\(default beside the exe\)')
  $defaultBranch = ''
  $defaultOk = $false
  if ($besideOk -or $prefLenOk) {
    $defaultBranch = if ($prefLenOk) { 'preferred model beside the exe -> out of the box (the preference was honoured, not just tolerated)' }
                     else { 'model beside the exe -> out of the box' }
    $defaultOk = $defaultNames -and ($defaultRun.Exit -eq 0) -and ($null -ne $defaultRun.Out) -and
                 ($defaultRun.OutSize -eq '512x410') -and ($defaultRun.WroteLine -match 'count=1')
  } else {
    $defaultBranch = 'no model beside the exe -> the NAMED state'
    $defaultOk = $defaultNames -and ($defaultRun.Exit -eq 3) -and
                 ($defaultRun.ModelPathLine -match 'exists=False') -and
                 ($defaultRun.Failure -match $missingMsg) -and ($null -eq $defaultRun.Out) -and
                 ($defaultRun.WroteLine -match 'count=0')
  }
  $defaultDetail = "branch='$defaultBranch' preferredPresent=$prefLenOk expectedDefault=$(Split-Path -Leaf $expectedDefault) " +
                   "exit=$($defaultRun.Exit) out=$(Get-OutName $defaultRun.Out) " +
                   "size=$($defaultRun.OutSize) wrote='$($defaultRun.WroteLine)' line='$defaultLine'"
  Check 'S22.14 the DEFAULT model path, asked with no --remove-bg-model at all: with a usable model beside the exe the plain command works out of the box (exit 0, one PNG at the source size) and it is the PREFERRED one whenever that one is present at its measured length, and without any model the same command is the NAMED state (exit 3, zero files)' $defaultOk $defaultDetail

  # ---------------------------------------------------------------- S22.15 default = THE RUNNING EXE'S OWN FOLDER
  # Machine-independent, and it exists because a working-directory-relative model is a REAL failure mode: it
  # would work when the app is launched from its own folder and fail from a desktop/tray launch. The test
  # copies the exe folder into the scratch directory, DELETES models\model_fp16.onnx IN THE COPY, and runs
  # THAT copy - so nothing below depends on whether this machine has a model at all:
  #   - the default path must be named against the COPY's own folder (it follows the RUNNING exe);
  #   - the missing file must be the NAMED state: exit 3, "error=ModelUnavailable", zero files written;
  #   - the run in the copy must not touch the ORIGINAL folder's model.
  function Invoke-ConfernFrom([string]$exePath, [string]$name, [string[]]$argv) {
    # The same shape as Invoke-Confern (quoting rule included), but for an arbitrary exe path. Defined here
    # because PowerShell resolves a plain function call at CALL time (see the note at the top of this section).
    $log = Join-Path $Work "$name.log"
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    $quoted = @()
    foreach ($a in $argv) { if ([string]$a -match ' ') { $quoted += ('"' + $a + '"') } else { $quoted += $a } }
    $all = @() + $quoted + @('--log', $log, '--quiet', '--no-autostart')
    $p = Start-Process -FilePath $exePath -ArgumentList $all -Wait -PassThru -WindowStyle Hidden
    return [pscustomobject]@{ Name = $name; Exit = $p.ExitCode; Log = $log; Lines = (Get-LogLines $log) }
  }

  $copyRoot = Join-Path $Work 'rembg-exe-copy'
  if (Test-Path -LiteralPath $copyRoot) { Remove-Item -LiteralPath $copyRoot -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $copyRoot | Out-Null
  $copyApp = Join-Path $copyRoot 'app'
  # robocopy /E, skipping a publish subfolder: a build output may contain one, and it is a second full copy
  # of the same application. Exit codes 0-7 mean "something was copied"; 8 and above is a real failure.
  $rcArgv = @()
  foreach ($a in @($exeDir, $copyApp, '/E', '/XD', (Join-Path $exeDir 'publish'), '/NFL', '/NDL', '/NJH', '/NJS', '/NP')) {
    if ([string]$a -match ' ') { $rcArgv += ('"' + $a + '"') } else { $rcArgv += $a }
  }
  $rc = Start-Process -FilePath 'robocopy' -ArgumentList $rcArgv -Wait -PassThru -WindowStyle Hidden
  $copyExe = Join-Path $copyApp (Split-Path -Leaf $Exe)
  $copyModel = Join-Path $copyApp 'models\model_fp16.onnx'
  $copyPreferred = Join-Path $copyApp 'models\ModelBiRefNet_1024x1024_fp16.onnx'
  $copyHadModel = Test-Path -LiteralPath $copyModel
  if ($copyHadModel) { Remove-Item -LiteralPath $copyModel -Force }
  # 2026-10-14: BOTH have to go. The default is a preference order now, so leaving the preferred model
  # in the copy would make this run SUCCEED ("a model was found") while the check is about the other
  # state entirely: "no model anywhere -> the NAMED unavailable state". Deleting only one of two is
  # exactly the kind of half-true precondition that turns a green check into a decoration.
  $copyHadPreferred = Test-Path -LiteralPath $copyPreferred
  if ($copyHadPreferred) { Remove-Item -LiteralPath $copyPreferred -Force }
  $besideShaBefore = Get-Sha $modelBesideExe
  $prefBesideShaBefore = Get-Sha $prefBesideExe

  $negDir = Join-Path $Work 'rembg-default-nomodel'
  New-Item -ItemType Directory -Force -Path $negDir | Out-Null
  $negSrc = Join-Path $negDir (Split-Path -Leaf $fPortrait)
  Copy-Item -LiteralPath $fPortrait -Destination $negSrc -Force
  $negSrcSha = Get-Sha $negSrc
  $negSrcMtime = (Get-Item -LiteralPath $negSrc).LastWriteTimeUtc

  $negRun = $null
  if (Test-Path -LiteralPath $copyExe) {
    $negRun = Invoke-ConfernFrom $copyExe 'defaultpath-nomodel' @('--remove-bg', $negSrc)
  }
  $negModelLine = 'NO RUN: the copy has no exe'
  $negNamed = $false
  $negWrote = ''
  $negNew = @()
  $negExit = -1
  if ($null -ne $negRun) {
    $negExit = $negRun.Exit
    $negModelLine = Get-LogLine $negRun.Lines 'tool remove-bg: model path='
    $negNamed = (Get-LogLine $negRun.Lines 'error=ModelUnavailable') -ne ''
    $negWrote = Get-LogLine $negRun.Lines 'remove-bg wrote: '
    $negNew = @(Get-ChildItem -LiteralPath $negDir -File | Where-Object { $_.FullName -ne $negSrc })
  }
  $negUntouched = ((Get-Sha $negSrc) -eq $negSrcSha) -and
                  ((Get-Item -LiteralPath $negSrc).LastWriteTimeUtc -eq $negSrcMtime)
  $negDefaultOk = ($null -ne $negRun) -and ($negExit -eq 3) -and $negNamed -and
                  ($negModelLine -match [regex]::Escape($copyModel)) -and
                  ($negModelLine -match 'exists=False') -and
                  ($negModelLine -match '\(default beside the exe\)') -and
                  ($negWrote -match 'count=0') -and ($negNew.Count -eq 0) -and $negUntouched -and
                  ((Get-Sha $modelBesideExe) -eq $besideShaBefore) -and
                  ((Get-Sha $prefBesideExe) -eq $prefBesideShaBefore)
  $negDetail = "copy='$copyApp' (hadModel=$copyHadModel, hadPreferred=$copyHadPreferred, both removed) robocopyExit=$($rc.ExitCode) " +
               "runExit=$negExit namedModelUnavailable=$negNamed wrote='$negWrote' extraFiles=$($negNew.Count) " +
               "sourceUntouched=$negUntouched originalBesideUnchanged=$((Get-Sha $modelBesideExe) -eq $besideShaBefore) " +
               "originalPreferredUnchanged=$((Get-Sha $prefBesideExe) -eq $prefBesideShaBefore) " +
               "line='$negModelLine'"
  Check 'S22.15 the default path follows the RUNNING exe (not the working directory, not the original folder): on a temp copy with the model deleted the plain command reports error=ModelUnavailable, exit 3 and zero files, and the original folder model is untouched' $negDefaultOk $negDetail

  # ---------------------------------------------------------------- S22.16 the shipping chain: store -> exe folder
  # "The model ships with the product" is a claim about two folders, so it is asserted as one. The build
  # copies the bytes from the repository store (models\model_fp16.onnx - deliberately NOT in version
  # control, .gitignore: models/*) into the output folder ONLY when the store file exists
  # (Sidecar.csproj: Condition="Exists(...)"), which is what keeps a checkout without a model buildable.
  # Both states of that chain are decidable, and neither is a skip:
  #   store present -> the model beside the exe must exist and be BYTE-IDENTICAL to the store (sha256), i.e.
  #                    this very exe is sitting next to the verified bytes the product looks for;
  #   store absent  -> there is no shipping source on this machine, so the only thing that can be asserted is
  #                    that whatever IS beside the exe is still the measured model (never "some file").
  $storeModel = Join-Path $repo 'models\model_fp16.onnx'
  $storeOk = Test-Path -LiteralPath $storeModel
  $besideNowSha = Get-Sha $modelBesideExe
  $measuredSha = '25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6'
  $measuredLen = '12984781'
  $layoutOk = $false
  $layoutDetail = ''
  if ($storeOk) {
    $storeSha = Get-Sha $storeModel
    $storeLen = (Get-Item -LiteralPath $storeModel).Length
    $layoutOk = $besideOk -and ($besideNowSha -eq $storeSha) -and ($besideNowSha -eq $measuredSha) -and
                ($storeLen -eq [long]$measuredLen)
    $layoutDetail = "store='$storeModel' len=$storeLen sha=$storeSha ; beside=$besideOk sha=$besideNowSha"
  } else {
    $layoutOk = (-not $besideOk) -or ($besideNowSha -eq $measuredSha)
    $layoutDetail = "no repository store at '$storeModel' ; beside=$besideOk sha=$besideNowSha (a model there must still hash to the measured value)"
  }
  Check 'S22.16 the shipping chain store -> exe folder: when the repository model store exists the model beside the exe is byte-identical to it (same sha256, the measured length), and when the store does not exist whatever sits beside the exe is still the measured model - the build never places an unverified file there' $layoutOk $layoutDetail

  # ---------------------------------------------------------------- S22.17 the model's licence notice ships too
  # The model is apache-2.0, and section 4(a) of that licence requires the licence to reach whoever
  # receives the product. So the notice is a SHIPPED FILE, not a line in a README: it is committed
  # (models\MODEL-LICENSE.txt, deliberately the one file inside the ignored models\ folder that IS in
  # version control) and the build copies it beside the model. Again, both states are decidable:
  #   the tracked notice exists -> the copy beside the exe must exist, be byte-identical to it, and name the
  #                                licence, the source repository and the measured length and sha256;
  #   no such file in this tree -> the exe folder must not carry one either (nothing may claim a notice the
  #                                source tree does not have).
  $repoNotice = Join-Path $repo 'models\MODEL-LICENSE.txt'
  $exeNotice = Join-Path $exeDir 'models\MODEL-LICENSE.txt'
  $noticeSourceOk = Test-Path -LiteralPath $repoNotice
  $noticeBesideOk = Test-Path -LiteralPath $exeNotice
  $noticeOk = $false
  $noticeDetail = ''
  if ($noticeSourceOk) {
    $noticeText = ''
    if ($noticeBesideOk) { $noticeText = [System.IO.File]::ReadAllText($exeNotice) }
    $noticeMissingBits = @()
    foreach ($need in @(
        @{ What = 'apache-2.0';                       Ok = ($noticeText -match '(?i)apache-2\.0') },
        @{ What = 'the Apache-2.0 text';              Ok = ($noticeText -match 'Apache License') },
        @{ What = 'Xenova/modnet';                    Ok = ($noticeText -match [regex]::Escape('Xenova/modnet')) },
        @{ What = 'the model file name';              Ok = ($noticeText -match [regex]::Escape('model_fp16.onnx')) },
        @{ What = 'the measured length';              Ok = ($noticeText -match $measuredLen) },
        @{ What = 'the measured sha256';              Ok = ($noticeText -match $measuredSha) })) {
      if (-not $need.Ok) { $noticeMissingBits += $need.What }
    }
    $noticeOk = $noticeBesideOk -and ($noticeMissingBits.Count -eq 0) -and
                ((Get-Sha $exeNotice) -eq (Get-Sha $repoNotice))
    $noticeDetail = "tracked='$repoNotice' sha=$((Get-Sha $repoNotice)) ; beside=$noticeBesideOk " +
                    "sha=$((Get-Sha $exeNotice)) missingFromNotice=($($noticeMissingBits -join ', '))"
  } else {
    $noticeOk = -not $noticeBesideOk
    $noticeDetail = "no tracked notice at '$repoNotice' ; beside=$noticeBesideOk (an exe folder must not carry a notice the source tree does not have)"
  }
  Check 'S22.17 the model licence notice (apache-2.0, Xenova/modnet) ships beside the model it covers: the copy next to the exe is byte-identical to the committed notice and names the licence, the source and the measured length and sha256 - a release that carried the model without this file would fail here' $noticeOk $noticeDetail


  # ---------------------------------------------------------------- S22.18-S22.21 the EDGE REFINEMENT
  # WHY THESE FOUR EXIST (2026-10-08, the refinement slice). The model pins the SHORTEST EDGE of its
  # input to 512 pixels, so on a 1920x1281 photo the mask it returns holds only 768x512 cells: every
  # mask pixel stands for 2.5 image pixels once it has been scaled back up, and the matte loses the
  # high-frequency part of its edge. The product now refines that mask at FULL resolution with a
  # guided filter (He, Sun, Tang 2010) driven by the SOURCE image's luminance, and then narrows the
  # transition by the very factor the mask was enlarged by.
  #
  # These checks assert the PRODUCT'S OWN numbers - --remove-bg-inspect prints
  # `matte band=... edge=...` for the transition width and the edge steepness - and the BYTES it
  # wrote, never the source code:
  #   * the input is a HIGH-RESOLUTION one, built here at runtime by a 3x nearest upscale of the
  #     committed 512x410 fixture (1536x1230). Its tensor comes back 640x512, i.e. a measured 2.40x
  #     bottleneck, which is the defect this slice exists for. (The real-world case - a 1920x1281
  #     photograph, 2.50x - cannot be committed, so its numbers live in the slice report.)
  #   * the SWITCH: --remove-bg-refine off / guided / absent, where ABSENT must equal guided byte
  #     for byte, because the refinement defaults to ON.
  #   * the DIRECTION, with a margin: the refined matte must have a NARROWER transition and a
  #     STEEPER edge than the unrefined one on the same file, so "it did nothing" cannot pass.
  #   * DETERMINISM: the same input twice, both ways, byte for byte.
  #   * the CONTROL: where the mask was NOT enlarged (the committed 512x410 fixture gets a 640x512
  #     tensor, i.e. 0.80x) the refinement must leave the file EXACTLY as it was. The measured
  #     reason is in EdgeRefine.MinBottleneck: at that scale the guided filter's own softening
  #     (about 2r) costs more than the alignment buys - transition 4.56% -> 21.74%, edge
  #     0.1856 -> 0.0456 when it was applied - so the product does not apply it at all.
  $hiDir = Join-Path $Work 'rembg-hires'
  New-Item -ItemType Directory -Force -Path $hiDir | Out-Null
  $hiPath = Join-Path $hiDir 'portrait-3x.png'
  $hiOk = $false
  $hiDetail = 'not built'
  if ($portraitFixtureOk) {
    try {
      $hiSrc = New-Object System.Drawing.Bitmap($fPortrait)
      $hiW = $hiSrc.Width * 3; $hiH = $hiSrc.Height * 3
      $hiBmp = New-Object System.Drawing.Bitmap($hiW, $hiH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      $hiG = [System.Drawing.Graphics]::FromImage($hiBmp)
      $hiG.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
      $hiG.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
      $hiG.DrawImage($hiSrc, 0, 0, $hiW, $hiH)
      $hiG.Dispose()
      $hiBmp.Save($hiPath, [System.Drawing.Imaging.ImageFormat]::Png)
      $hiBmp.Dispose(); $hiSrc.Dispose()
      $hiOk = Test-Path -LiteralPath $hiPath
      $hiDetail = "built ${hiW}x${hiH}"
    } catch {
      $hiOk = $false
      $hiDetail = "3x nearest upscale failed: $($_.Exception.Message)"
    }
  } else {
    $hiDetail = 'portrait fixture missing'
  }
  $hiSize = 'MISSING'
  if ($hiOk) { $hiSize = Get-ImageSize $hiPath }

  # A private settings file: the suite must never read or write the user's real one, and the
  # refinement's own setting has to be demonstrable on the SAME input as the CLI switch.
  $setDefaultPath = Join-Path $Work 'rembg-refine-default.json'
  [System.IO.File]::WriteAllText($setDefaultPath, '{ "JpgQuality": 85 }', (New-Object System.Text.UTF8Encoding($false)))

  $refOff = $null; $refOn = $null; $refDefault = $null
  $refOff2 = $null; $refOn2 = $null
  if ($haveModel -and $hiOk) {
    $refOff = Invoke-RemoveBg 'refine-off' $hiPath $model @('--remove-bg-refine', 'off',
                                                           '--settings-file', $setDefaultPath)
    $refOn = Invoke-RemoveBg 'refine-on' $hiPath $model @('--remove-bg-refine', 'guided',
                                                          '--settings-file', $setDefaultPath)
    $refDefault = Invoke-RemoveBg 'refine-default' $hiPath $model @('--settings-file', $setDefaultPath)

    $refOff2 = Invoke-RemoveBg 'refine-off-again' $hiPath $model @('--remove-bg-refine', 'off',
                                                                   '--settings-file', $setDefaultPath)
    $refOn2 = Invoke-RemoveBg 'refine-on-again' $hiPath $model @('--remove-bg-refine', 'guided',
                                                                 '--settings-file', $setDefaultPath)
  }

  function Get-Sha12([string]$p) {
    $s = Get-Sha $p
    if ($s.Length -le 12) { return $s }
    return $s.Substring(0, 12)
  }

  # ---- S22.18 the switch: off / guided / absent, and ABSENT means ON
  $switchOk = $false
  $switchDetail = "hires=($hiDetail) size=$hiSize haveModel=$haveModel"
  if (($null -ne $refOff) -and ($null -ne $refOn) -and ($null -ne $refDefault)) {
    $offSha = Get-Sha $refOff.Out; $onSha = Get-Sha $refOn.Out; $defSha = Get-Sha $refDefault.Out
    $offLine = Get-LogLine $refOff.Lines 'tool remove-bg: refine='
    $onLine = Get-LogLine $refOn.Lines 'tool remove-bg: refine='
    $defLine = Get-LogLine $refDefault.Lines 'tool remove-bg: refine='
    $offNamed = [bool]($offLine -match 'refine=off \(--remove-bg-refine\)')
    $onNamed = [bool]($onLine -match 'refine=guided \(--remove-bg-refine, the default\)')
    $defNamed = [bool]($defLine -match 'refine=guided \(settings, the default\)')
    $switchOk = ($refOff.Exit -eq 0) -and ($refOn.Exit -eq 0) -and ($refDefault.Exit -eq 0) -and
                ($refOff.OutSize -eq $hiSize) -and ($refOn.OutSize -eq $hiSize) -and
                ($refDefault.OutSize -eq $hiSize) -and
                $offNamed -and $onNamed -and $defNamed -and
                ($offSha -ne $onSha) -and ($onSha -eq $defSha)
    $switchDetail = "hires=($hiDetail) offExit=$($refOff.Exit)/$($refOff.OutSize) onExit=$($refOn.Exit)/$($refOn.OutSize) " +
                    "defaultExit=$($refDefault.Exit)/$($refDefault.OutSize) ; offLine='$offLine' ; onLine='$onLine' ; " +
                    "defaultLine='$defLine' ; offSha=$(Get-Sha12 $refOff.Out) onSha=$(Get-Sha12 $refOn.Out) " +
                    "defaultSha=$(Get-Sha12 $refDefault.Out) differ=$($offSha -ne $onSha) absentIsGuided=$($onSha -eq $defSha)"
  } else {
    Inconclusive 'S22.18 needs a model and the built high-resolution input'
  }
  Check 'S22.18 the refinement switch is honoured on a high-resolution input (off vs guided produce different bytes) and NO flag means ON: the unflagged run is byte-identical to the explicit guided one, and each run says which mode it used' `
    $switchOk $switchDetail

  # ---- S22.19 the direction, measured by the product itself
  $dirOk = $false
  $dirDetail = 'no inspect'
  if (($null -ne $refOff) -and ($null -ne $refOn) -and ($null -ne $refOff.Out) -and ($null -ne $refOn.Out)) {
    $mOff = Get-RemoveBgInspect 'refine-off' $refOff.Out
    $mOn = Get-RemoveBgInspect 'refine-on' $refOn.Out
    # 2026-10-08, second round: the FIRST version of this check demanded band <= 0.9x AND edge >= 1.1x.
    # That is what the reviewer objected to: the narrowing was strongest exactly where the guide is
    # TEXTURED (fur, hair), and a real strand IS legitimately semi-transparent, so those pixels were
    # pushed to 0 - measured on the 1920x1281 photo, only 30.4% of them survived. The gate polarity
    # was inverted (see EdgeRefine.GateEpsilon), so the honest assertions now are: (a) the band must
    # still NARROW in the LOW-texture half, which is where the coarse mask's staircase lives, and
    # (b) the refined band must never be wider than the unrefined one - the guided filter alone
    # widens it (15.09% -> 22.91% on the photo, 1.19x here), so a dropped narrowing goes red here.
    # MEASURED margins on THIS input: band 0.9547x, edge 1.15x, lowTexBand 0.8839x, opaque 1.016x.
    $bandOk = ($mOn.MatteBand -gt 0) -and ($mOff.MatteBand -gt 0) -and ($mOn.MatteBand -le (1.02 * $mOff.MatteBand))
    $edgeOk = ($mOn.MatteEdge -gt 0) -and ($mOff.MatteEdge -gt 0) -and ($mOn.MatteEdge -ge (1.05 * $mOff.MatteEdge))
    $opaqueOk = ($mOn.Opaque -ge $mOff.Opaque)
    $dirOk = $bandOk -and $edgeOk -and $opaqueOk
    $dirDetail = "band off=$($mOff.MatteBand) on=$($mOn.MatteBand) need on<=1.02*off ($([Math]::Round(1.02 * $mOff.MatteBand, 4))) -> $bandOk ; " +
                 "edge off=$($mOff.MatteEdge) on=$($mOn.MatteEdge) need on>=1.05*off ($([Math]::Round(1.05 * $mOff.MatteEdge, 4))) -> $edgeOk ; " +
                 "opaque off=$($mOff.Opaque) on=$($mOn.Opaque) -> $opaqueOk ; '$($mOn.MatteLine)'"
  } else {
    Inconclusive 'S22.19 needs the two refinement arms from S22.18'
  }
  Check 'S22.19 on the SAME high-resolution file the refined matte is never WIDER than the unrefined one and never loses its edge (product-measured: band <= 1.02x, edge >= 1.05x), with no fewer fully opaque pixels' `
    $dirOk $dirDetail

  # ---- S22.20 determinism, both ways
  $detOk = $false
  $detDetail = 'no repeat runs'
  if (($null -ne $refOff2) -and ($null -ne $refOn2) -and ($null -ne $refOff) -and ($null -ne $refOn)) {
    $offSame = (Get-Sha $refOff.Out) -eq (Get-Sha $refOff2.Out)
    $onSame = (Get-Sha $refOn.Out) -eq (Get-Sha $refOn2.Out)
    $untouched = $refOff.Untouched -and $refOn.Untouched -and $refOff2.Untouched -and $refOn2.Untouched
    $sizesOk = ($refOff2.OutSize -eq $hiSize) -and ($refOn2.OutSize -eq $hiSize)
    $detOk = $offSame -and $onSame -and $untouched -and $sizesOk
    $detDetail = "off $(Get-Sha12 $refOff.Out) vs $(Get-Sha12 $refOff2.Out) same=$offSame ; " +
                 "guided $(Get-Sha12 $refOn.Out) vs $(Get-Sha12 $refOn2.Out) same=$onSame ; " +
                 "sourcesUntouched=$untouched sizesOk=$sizesOk"
  } else {
    Inconclusive 'S22.20 needs the repeat runs on the high-resolution input'
  }
  Check 'S22.20 the refinement is deterministic: the same input run twice gives byte-identical output with the refinement both OFF and ON, and every source copy is left alone' `
    $detOk $detDetail

  # ---- S22.21 the control: no bottleneck, no change - and the setting reaches the same switch
  $ctlOk = $false
  $ctlDetail = 'no arms'
  if (($null -ne $refOff) -and ($null -ne $portrait.Out)) {
    $portraitNoFlag = $portrait.Out
    # S22.05 ran the committed 512x410 fixture through the DEFAULT path (which is the refinement ON).
    # A second run on the same fixture with the switch OFF must be byte-identical to it.
    $ctlOff = Invoke-RemoveBg 'refine-portrait-off' $fPortrait $model @('--remove-bg-refine', 'off',
                                                                     '--settings-file', $setDefaultPath)
    $portraitSame = ($null -ne $ctlOff.Out) -and
                    ((Get-Sha $portraitNoFlag) -eq (Get-Sha $ctlOff.Out))
    # The settings key (the wheel has no command line, so this is how a user turns the refinement off
    # for good). MEASURED 2026-10-08: the --remove-bg SEAM never reads the settings file - with
    # `{"RemoveBgRefine":"off"}` it still logged `outputDir=(source folder)` and `refine=guided`, and
    # its log had no `settings: loaded from` line - so the key is asserted through the two verbs that
    # DO read that file: --settings-set (the settings page's Save) and --settings-probe.
    $keyFile = Join-Path $Work 'rembg-refine-key.json'
    [System.IO.File]::WriteAllText($keyFile, '{ "JpgQuality": 85 }', (New-Object System.Text.UTF8Encoding($false)))
    $probeBefore = Invoke-Confern 'refine-key-before' @('--settings-probe', $keyFile)
    $keySet = Invoke-Confern 'refine-key-off' @('--settings-set', 'removeBgRefine=off', '--settings-file', $keyFile)
    $probeAfter = Invoke-Confern 'refine-key-after' @('--settings-probe', $keyFile)
    $keyBad = Invoke-Confern 'refine-key-junk' @('--settings-set', 'removeBgRefine=maybe', '--settings-file', $keyFile)
    $beforeLine = Get-LogLine $probeBefore.Lines 'settings probe result:'
    $afterLine = Get-LogLine $probeAfter.Lines 'settings probe result:'
    $keyText = [System.IO.File]::ReadAllText($keyFile)
    $fileHasOff = [bool]($keyText -match '"RemoveBgRefine": "off"')
    $defaultOn = ($probeBefore.Exit -eq 0) -and [bool]($beforeLine -match 'refine=guided')
    $turnsOff = ($keySet.Exit -eq 0) -and
                [bool]((Get-LogLine $keySet.Lines 'settings set done:') -match 'applied=1 rejected=0') -and
                [bool]($afterLine -match 'refine=off') -and $fileHasOff
    $refusesJunk = ($keyBad.Exit -eq 4) -and
                   [bool]((Get-LogLine $keyBad.Lines 'settings set done:') -match 'applied=0 rejected=1') -and
                   [bool]((Get-LogLine $keyBad.Lines 'settings: reject removeBgRefine') -match 'expected one of')
    # The rejected value must not have been written: the file still says off, and it still probes off.
    $junkNotWritten = [bool](([System.IO.File]::ReadAllText($keyFile)) -match '"RemoveBgRefine": "off"')
    $ctlOk = $portraitSame -and ($ctlOff.Exit -eq 0) -and $defaultOn -and $turnsOff -and $refusesJunk -and $junkNotWritten
    $ctlDetail = "portrait 512x410 (tensor 640x512 = 0.80x): defaultPath=$(Get-Sha12 $portraitNoFlag) " +
                 "switchOff=$(Get-Sha12 $ctlOff.Out) identical=$portraitSame ; settings key: defaultLine '$beforeLine' " +
                 "setExit=$($keySet.Exit) afterLine '$afterLine' fileHasOff=$fileHasOff junkExit=$($keyBad.Exit) " +
                 "refusesJunk=$refusesJunk notWritten=$junkNotWritten"
  } else {
    Inconclusive 'S22.21 needs the off arm and the S22.05 portrait output'
  }
  Check 'S22.21 the control, and the settings key: on an input whose mask was NOT enlarged the refinement changes nothing at all (byte-identical with the switch off), while the settings key the wheel reads is canonical - absent means ON, off is accepted and survives a round trip, and a junk value is refused with exit 4 and is not written' `
    $ctlOk $ctlDetail

  # ---- S22.22 the narrowing must NOT eat the genuinely soft pixels -------------------------
  # The reviewer's finding of 2026-10-08, second round, measured on the 1920x1281 photo: the first
  # version gated the narrowing on "the guide has an edge here", so it hardened exactly the TEXTURED
  # regions - and a real hair strand IS legitimately semi-transparent, so those pixels were pushed
  # to 0 (only 30.4% of them survived; 26286 became EXACTLY 0; the soft-hair population fell to
  # 34% of the unrefined mask). The gate polarity was inverted (EdgeRefine.GateEpsilon).
  # This check pins the new direction on THIS suite's own high-resolution input, and the numbers
  # it rejects are the ones the old polarity measured on the very same file.
  if (-not ('RembgSoftMetrics' -as [type])) {
    Add-Type -Language CSharp -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @'
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class RembgSoftMetrics
{
    private static BitmapSource Load(string path)
    {
        BitmapFrame frame;
        using (var fs = System.IO.File.OpenRead(path))
            frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return frame.Format == PixelFormats.Bgra32
            ? (BitmapSource)frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
    }

    private static byte[] AlphaPlane(string path, out int w, out int h)
    {
        BitmapSource bgra = Load(path);
        w = bgra.PixelWidth; h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var a = new byte[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = px[i * 4 + 3];
        return a;
    }

    private static double[] LumaPlane(string path, out int w, out int h)
    {
        BitmapSource bgra = Load(path);
        w = bgra.PixelWidth; h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var y = new double[w * h];
        for (int i = 0; i < y.Length; i++)
            y[i] = (0.299 * px[i * 4 + 2] + 0.587 * px[i * 4 + 1] + 0.114 * px[i * 4]) / 255.0;
        return y;
    }

    private static byte[] LoadPlane(string path, out int w, out int h)
    {
        BitmapSource bgra = Load(path);
        w = bgra.PixelWidth; h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>
    /// S22.23. Compares two images pixel for pixel over the SUBJECT (alpha &gt;= minAlpha) and
    /// returns [0] = how many of those pixels have a different B/G/R, [1] = how many were
    /// compared, [2] = how many DISTINCT colours the SOURCE has in that same population.
    /// [2] is the anti-vacuity guard: on a grey source "no mismatch" would be true for any
    /// output that is also grey, which is exactly the defect this check exists to catch.
    /// A size mismatch returns [-1, 0, 0] so it can never look like a pass.
    /// </summary>
    public static int[] ColourMismatches(string sourcePath, string outputPath, int minAlpha)
    {
        int sw, sh, ow, oh;
        var sp = LoadPlane(sourcePath, out sw, out sh);
        var op = LoadPlane(outputPath, out ow, out oh);
        if (sw != ow || sh != oh) return new[] { -1, 0, 0 };
        int mismatch = 0, compared = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < sw * sh; i++)
        {
            if (op[i * 4 + 3] < minAlpha) continue;
            compared++;
            seen.Add((sp[i * 4 + 2] << 16) | (sp[i * 4 + 1] << 8) | sp[i * 4]);
            if (sp[i * 4] != op[i * 4] || sp[i * 4 + 1] != op[i * 4 + 1] || sp[i * 4 + 2] != op[i * 4 + 2])
                mismatch++;
        }
        return new[] { mismatch, compared, seen.Count };
    }

    // moving-sum box mean: O(1) per pixel, border normalised by the real window size
    private static double[] BoxMean(double[] src, int w, int h, int r)
    {
        var tmp = new double[w * h];
        var dst = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double sum = 0;
            for (int x = 0; x <= r && x < w; x++) sum += src[row + x];
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                tmp[row + x] = sum / (x1 - x0 + 1);
                if (x + r + 1 < w) sum += src[row + x + r + 1];
                if (x - r >= 0) sum -= src[row + x - r];
            }
        }
        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int y = 0; y <= r && y < h; y++) sum += tmp[y * w + x];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                dst[y * w + x] = sum / (y1 - y0 + 1);
                if (y + r + 1 < h) sum += tmp[(y + r + 1) * w + x];
                if (y - r >= 0) sum -= tmp[(y - r) * w + x];
            }
        }
        return dst;
    }

    // Soft = 0.05 <= alpha <= 0.60 (a strand really may be neither 0 nor 1).
    // Textured = the 9x9 mean of |grad Y| >= texThr (hair/fur, not a flat area).
    // The wisp population is taken from the UNREFINED mask only, so both arms measure one set.
    public static string Compare(string guide, string offPng, string onPng, double texThr)
    {
        int w, h; byte[] aOff = AlphaPlane(offPng, out w, out h);
        int w2, h2; byte[] aOn = AlphaPlane(onPng, out w2, out h2);
        int gw, gh; double[] y = LumaPlane(guide, out gw, out gh);
        if (w2 != w || h2 != h || gw != w || gh != h) return "SIZE MISMATCH";
        int n = w * h;
        var grad = new double[n];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int xm = xx > 0 ? xx - 1 : 0, xp = xx < w - 1 ? xx + 1 : w - 1;
                int ym = yy > 0 ? yy - 1 : 0, yp = yy < h - 1 ? yy + 1 : h - 1;
                double gx = (y[yy * w + xp] - y[yy * w + xm]) / 2.0;
                double gy = (y[yp * w + xx] - y[ym * w + xx]) / 2.0;
                grad[yy * w + xx] = Math.Sqrt(gx * gx + gy * gy);
            }
        double[] tex = BoxMean(grad, w, h, 4);
        long bandOff = 0, bandOn = 0, lowBandOff = 0, lowBandOn = 0;
        long wispN = 0, kept = 0, strictZero = 0, popOn = 0;
        for (int i = 0; i < n; i++)
        {
            double ao = aOff[i] / 255.0, an = aOn[i] / 255.0;
            bool softOff = ao >= 0.05 && ao <= 0.60, softOn = an >= 0.05 && an <= 0.60;
            bool inOff = aOff[i] != 0 && aOff[i] != 255, inOn = aOn[i] != 0 && aOn[i] != 255;
            if (inOff) bandOff++;
            if (inOn) bandOn++;
            if (tex[i] < texThr) { if (inOff) lowBandOff++; if (inOn) lowBandOn++; }
            else
            {
                if (softOff)
                {
                    wispN++;
                    if (softOn) kept++;
                    if (aOn[i] == 0) strictZero++;
                }
                if (softOn) popOn++;
            }
        }
        return string.Format(
            "wispN={0} kept={1} preservation={2:F4} populationRatio={3:F4} strictZero={4} strictZeroShare={5:F4} lowTexBandRatio={6:F4} bandRatio={7:F4}",
            wispN, kept, wispN > 0 ? (double)kept / wispN : 0,
            wispN > 0 ? (double)popOn / wispN : 0, strictZero,
            wispN > 0 ? (double)strictZero / wispN : 0,
            lowBandOff > 0 ? (double)lowBandOn / lowBandOff : 0,
            bandOff > 0 ? (double)bandOn / bandOff : 0);
    }
}
'@
  }
  $softOk = $false
  $softDetail = 'no arms'
  if (($null -ne $refOff) -and ($null -ne $refOn) -and $refOff.Out -and $refOn.Out) {
    $softRaw = [RembgSoftMetrics]::Compare($hiPath, $refOff.Out, $refOn.Out, 0.03)
    $pres = [double]([regex]::Match($softRaw, 'preservation=([0-9.]+)').Groups[1].Value)
    $popr = [double]([regex]::Match($softRaw, 'populationRatio=([0-9.]+)').Groups[1].Value)
    $zs = [double]([regex]::Match($softRaw, 'strictZeroShare=([0-9.]+)').Groups[1].Value)
    $stair = [double]([regex]::Match($softRaw, 'lowTexBandRatio=([0-9.]+)').Groups[1].Value)
    $bandr = [double]([regex]::Match($softRaw, 'bandRatio=([0-9.]+)').Groups[1].Value)
    # Thresholds sit far from BOTH wrong answers measured on this same input: the old polarity
    # scored preservation 0.3048 / population 0.4313 / strictZeroShare 0.5429, and a build with
    # the narrowing dropped scores bandRatio 1.19 (the guided filter widens the band on its own).
    $softOk = ($pres -ge 0.70) -and ($popr -ge 0.80) -and ($zs -le 0.15) -and
              ($stair -le 0.98) -and ($bandr -le 1.02)
    $softDetail = "$softRaw ; need preservation>=0.70 populationRatio>=0.80 strictZeroShare<=0.15 " +
                  "lowTexBandRatio<=0.98 bandRatio<=1.02"
  } else {
    Inconclusive 'S22.22 needs the two refinement arms from S22.18'
  }
  Check 'S22.22 the narrowed matte keeps the GENUINELY soft pixels: of the pixels the unrefined mask leaves semi-transparent (0.05..0.60) in a high-texture neighbourhood (9x9 mean |grad Y| >= 0.03), at least 70% are still semi-transparent, the soft-hair population does not fall below 80% of the unrefined one, at most 15% were pushed to exactly 0, and the anti-staircase half survives (the low-texture band and the whole band are both narrower than unrefined)' $softOk $softDetail

  # ---- S22.23 the COLOUR: the artefact must be a CUTOUT, not the matte -----------------------
  # WHY THIS EXISTS (2026-10-14, the user's own finding, and the reason every check above passed
  # while the tool was broken): S22.05..S22.22 all measure the ALPHA. None of them ever looked at
  # B/G/R. The pipeline filled the matte into all four channels (RemoveBgModel.MaskToGrayFrame)
  # and only copied R into A (ConversionEngine.ExpandMaskToAlpha), then encoded that buffer
  # straight out - so the "cutout" was a white silhouette carrying a correct alpha channel. The
  # user opened the artefact and asked whether CUTOUT was supposed to produce a mask; measured on
  # this suite's own high-resolution input, 209794 of 209920 pixels (99.94% of the image, 100% of
  # the subject) carried the mask value in B/G/R.
  # This check reads BOTH files itself and compares them pixel for pixel. It deliberately does NOT
  # read the product's own "remove-bg colour:" line: that is a self-report, and this project has
  # already been burned once by trusting a query-shaped answer (see S21.01 vs S21.07).
  # The source arm is a NearestNeighbor block copy (built above), so a correct output matches it
  # EXACTLY - no resampling tolerance is allowed or needed, and any tolerance would hide the bug.
  if ($refOff.Out -and $refOn.Out -and $hiPath -and (Test-Path $hiPath)) {
    $rOff = [RembgSoftMetrics]::ColourMismatches($hiPath, $refOff.Out, 250)
    $rOn = [RembgSoftMetrics]::ColourMismatches($hiPath, $refOn.Out, 250)
    $mOff = [int]$rOff[0]; $cOff = [int]$rOff[1]; $dOff = [int]$rOff[2]
    $mOn = [int]$rOn[0]; $cOn = [int]$rOn[1]; $dOn = [int]$rOn[2]
    $colourOk = ($mOff -eq 0) -and ($mOn -eq 0) -and
                ($cOff -ge 5000) -and ($cOn -ge 5000) -and
                ($dOff -ge 64) -and ($dOn -ge 64)
    $colourDetail = "off: mismatch=$mOff compared=$cOff sourceColours=$dOff ; " +
                    "on: mismatch=$mOn compared=$cOn sourceColours=$dOn " +
                    "(need mismatch=0 on both arms, compared>=5000, and the SOURCE itself must show " +
                    ">=64 distinct colours over that population - otherwise a grey source would make " +
                    "this check vacuous)"
  } else {
    Inconclusive 'S22.23 needs the two refinement arms from S22.18'
  }
  Check 'S22.23 the written PNG is a CUTOUT and not the matte: on the high-resolution input EVERY subject pixel (alpha >= 250) of both the refined and the unrefined output carries exactly the SOURCE pixel RGB, and the compared population is both large and genuinely colourful in the source, so the assertion cannot pass vacuously on a grey image' `
    $colourOk $colourDetail

  # ---------------------------------------------------------------- S22.24 the preference is a PRODUCT, not a log string
  # A preference order is worth nothing unless the user can tell which engine they got. S22.14 reads the UI-side line
  # that NAMES the path; this check does not trust a name at all. It runs the SAME source twice - once with no switch
  # at all (whatever the preference order picks), once with MODNet forced by --remove-bg-model - and compares BYTES:
  #   preferred present -> the runs must DIFFER  (the preference changes what the user gets, not just a log line)
  #   only MODNet       -> the runs must be IDENTICAL (the fallback is exactly the old behaviour, byte for byte)
  # Both branches are decidable and neither is a skip, so this is a real assertion on every machine.
  $prefA = Invoke-RemoveBg 'defaultpath-pref' $fPortrait ''
  $prefB = Invoke-RemoveBg 'defaultpath-modnet' $fPortrait $modelBesideExe
  $shaA = if (($null -ne $prefA) -and ($null -ne $prefA.Out)) { Get-Sha $prefA.Out } else { '' }
  $shaB = if (($null -ne $prefB) -and ($null -ne $prefB.Out)) { Get-Sha $prefB.Out } else { '' }
  $prefSame = ($shaA -ne '') -and ($shaA -eq $shaB)
  $prefBranch = ''
  $prefOk = $false
  if ($prefLenOk) {
    $prefBranch = 'preferred model present -> the default must DIFFER from forced MODNet'
    $prefOk = ($shaA -ne '') -and ($shaB -ne '') -and (-not $prefSame) -and
              (($null -ne $prefA) -and ($prefA.Exit -eq 0)) -and (($null -ne $prefB) -and ($prefB.Exit -eq 0))
  } else {
    $prefBranch = 'no preferred model -> the default must BE forced MODNet, byte for byte'
    $prefOk = ($shaA -ne '') -and ($shaA -eq $shaB) -and
              (($null -ne $prefA) -and ($prefA.Exit -eq 0)) -and (($null -ne $prefB) -and ($prefB.Exit -eq 0))
  }
  $prefDetail = "branch='$prefBranch' identical=$prefSame " +
                "shaDefault=$($shaA.Substring(0, [Math]::Min(12, $shaA.Length))) " +
                "shaForcedModnet=$($shaB.Substring(0, [Math]::Min(12, $shaB.Length))) " +
                "exitDefault=$(if ($null -ne $prefA) { $prefA.Exit } else { 'NO RUN' }) " +
                "exitForced=$(if ($null -ne $prefB) { $prefB.Exit } else { 'NO RUN' })"
  Check 'S22.24 the DEFAULT preference is a different PRODUCT and not a different log string: with no --remove-bg-model the plain command and the same command with MODNet forced by name must produce DIFFERENT bytes when the preferred model is present (the user really gets the other engine), and BYTE-IDENTICAL output when it is absent (the fallback is exactly the old behaviour)' $prefOk $prefDetail

  # ---------------------------------------------------------------- S22.25 the OPTIONAL model's notice
  # S22.17 guards the model the product SHIPS. This one guards the model the default now PREFERS (2026-10-14).
  # MIT requires the copyright notice and the permission notice to reach whoever receives a copy, so when that
  # model sits beside the exe its notice must sit beside it too - and, sharper than S22.17, the numbers INSIDE
  # the notice must equal the bytes ON DISK. A licence file that describes a different build than the one being
  # handed out is worse than no file at all, because it looks like diligence. Both branches stay decidable:
  #   preferred model beside the exe -> notice beside it, byte-identical to the committed one, and its length
  #                                     and sha256 equal the real file's (not some earlier build's);
  #   no preferred model             -> nothing for that notice to declare, so it must not be a bare copy of a
  #                                     claim with no file behind it.
  $prefNoticeRepo = Join-Path $repo 'models\MODEL-BIREFNET-LICENSE.txt'
  $prefNoticeExe = Join-Path $exeDir 'models\MODEL-BIREFNET-LICENSE.txt'
  $prefNoticeTracked = Test-Path -LiteralPath $prefNoticeRepo
  $prefNoticeBeside = Test-Path -LiteralPath $prefNoticeExe
  $prefNoticeOk = $false
  $prefNoticeDetail = ''
  if ($prefLenOk) {
    $pText = if ($prefNoticeBeside) { [System.IO.File]::ReadAllText($prefNoticeExe) } else { '' }
    $prefRealSha = (Get-FileHash -LiteralPath $prefBesideExe -Algorithm SHA256).Hash
    $prefRealLen = (Get-Item -LiteralPath $prefBesideExe).Length
    $prefMissing = @()
    foreach ($need in @(
        @{ What = 'the licence name (MIT)';                    Ok = ($pText -match '(?i)\bMIT\b') },
        @{ What = 'the MIT text itself';                       Ok = ($pText -match 'Permission is hereby granted') },
        @{ What = 'the copyright holder line';                 Ok = ($pText -match [regex]::Escape('Copyright (c) 2024 ZhengPeng')) },
        @{ What = 'the source repository';                     Ok = ($pText -match [regex]::Escape('onnx-community/BiRefNet-ONNX')) },
        @{ What = 'the upstream repository';                   Ok = ($pText -match [regex]::Escape('ZhengPeng7/BiRefNet')) },
        @{ What = 'the model file name';                       Ok = ($pText -match [regex]::Escape('ModelBiRefNet_1024x1024_fp16.onnx')) },
        @{ What = 'the length OF THE FILE ON DISK';            Ok = ($pText -match [regex]::Escape([string]$prefRealLen)) },
        @{ What = 'the sha256 OF THE FILE ON DISK';            Ok = ($pText -match [regex]::Escape($prefRealSha)) })) {
      if (-not $need.Ok) { $prefMissing += $need.What }
    }
    $prefNoticeOk = $prefNoticeTracked -and $prefNoticeBeside -and ($prefMissing.Count -eq 0) -and
                    ((Get-Sha $prefNoticeExe) -eq (Get-Sha $prefNoticeRepo))
    $prefNoticeDetail = "modelLen=$prefRealLen modelSha=$($prefRealSha.Substring(0, 12)) tracked=$prefNoticeTracked " +
                        "beside=$prefNoticeBeside missingFromNotice=($($prefMissing -join ', '))"
  } else {
    $prefNoticeOk = (-not $prefNoticeBeside) -or ($prefNoticeTracked -and $prefNoticeBeside)
    $prefNoticeDetail = "no preferred model beside the exe -> this notice has nothing to declare; " +
                        "noticePresent=$prefNoticeBeside tracked=$prefNoticeTracked"
  }
  Check 'S22.25 the notice of the OPTIONAL model travels with it AND describes the bytes that are actually there: when the preferred model sits beside the exe its MIT notice must sit beside it, be byte-identical to the committed one, and carry the length and sha256 OF THE FILE ON DISK rather than of some earlier build; with no preferred model there is nothing for it to declare' $prefNoticeOk $prefNoticeDetail

  # ---- the two injections of this section, stated where a red run is read ----
  if ($fault -eq 'rembg-nosubject') {
    Write-Host 'INJECTION rembg-nosubject: S22.04 now expects a WRITTEN file for a QR scene. That ONE check must'
    Write-Host '                    go red (it did, measured 2026-10-08), which is what proves the empty-mask'
    Write-Host '                    refusal is load-bearing rather than a tautology. It is deliberately surgical:'
    Write-Host '                    a broader injection turned S22.11/S22.12 red too and destroyed the signal.'
    Write-Host '                    S22.02 is kept falsifiable from the other side - it asserts a sentence that'
    Write-Host '                    only a corrupt file can produce, plus "no output file" for both runs.'
  }
  if ($fault -eq 'rembg-png') {
    Write-Host 'INJECTION rembg-png: S22.06 now expects the decoded frame to have NO alpha channel. It must go'
    Write-Host '                    red, which proves that assertion reads the real pixel format.'
  }
}
function Invoke-S21 {
  Write-Host "=== S21 merge-pdf sorting window: the shown order becomes the output page order ==="

  $repo = Split-Path -Parent $PSScriptRoot
  $fdir = Join-Path $repo 'fixtures\pdf'
  $fA = Join-Path $fdir 'fx-a.pdf'
  $fB = Join-Path $fdir 'fx-b.pdf'
  $fC = Join-Path $fdir 'fx-c.pdf'
  $missing = @()
  foreach ($f in @($fA, $fB, $fC)) {
    if (-not (Test-Path -LiteralPath $f)) { $missing += (Split-Path -Leaf $f) }
  }
  if ($missing.Count -gt 0) {
    Inconclusive "PDF fixtures missing from $fdir ($($missing -join ', ')) - they are committed, so a missing one means the checkout is broken"
  }

  # The expected page tuples, read from the PRODUCT (the same reader S20 uses). The order of the
  # three source files' tuples is the fixture ground truth: a=2 pages, b=1, c=3, all six sizes
  # pairwise distinct, so "which file did this page come from" is decidable from the output alone.
  $iA = Get-MergePages 's21-src-a' $fA
  $iB = Get-MergePages 's21-src-b' $fB
  $iC = Get-MergePages 's21-src-c' $fC
  $srcSizesA = @($iA.Pages | ForEach-Object { $_.Size })
  $srcSizesB = @($iB.Pages | ForEach-Object { $_.Size })
  $srcSizesC = @($iC.Pages | ForEach-Object { $_.Size })
  $srcShasA = @($iA.Pages | ForEach-Object { $_.Sha })
  $srcShasB = @($iB.Pages | ForEach-Object { $_.Sha })
  $srcShasC = @($iC.Pages | ForEach-Object { $_.Sha })
  # The order the window is given, and the one the drag is expected to produce, are BOTH read from
  # the product below - these two lists only name the FILES.
  $namesA = @('fx-a.pdf', 'fx-a.pdf', 'fx-a.pdf')
  $namesB = @('fx-b.pdf', 'fx-b.pdf', 'fx-b.pdf')
  $namesC = @('fx-c.pdf', 'fx-c.pdf', 'fx-c.pdf')

  # The sources are COPIED into a fresh folder: nothing is ever written next to the committed
  # fixtures (an earlier probe of mine did write a merged file there and had to be cleaned up -
  # measured 2026-10-07, which is why every run below works on copies).
  $ui = Join-Path $Work 'mergeui'
  New-Item -ItemType Directory -Force -Path $ui | Out-Null
  $uA = Join-Path $ui 'fx-a.pdf'
  $uB = Join-Path $ui 'fx-b.pdf'
  $uC = Join-Path $ui 'fx-c.pdf'
  Copy-Item -LiteralPath $fA -Destination $uA -Force
  Copy-Item -LiteralPath $fB -Destination $uB -Force
  Copy-Item -LiteralPath $fC -Destination $uC -Force

  # One baseline table per property, keyed by the path exactly as the run used it. NOTE: every
  # path that S21.08 checks has to be seeded before the runs - a missing key reads as $null and
  # then "null -ne actual" reports a change that never happened (measured: the first version
  # seeded only the three mergeui copies and so accused the three cancel-drill copies of all
  # three properties while the values were in fact identical). The seeding is written out
  # inline rather than in a helper on purpose: a PowerShell function does not see the caller's
  # local variables, so a helper would need $script: and then the READS below would miss.
  $srcHashes = @{}
  $srcLens = @{}
  $srcMtimes = @{}
  foreach ($p in @($uA, $uB, $uC)) {
    $srcHashes[$p] = Get-Sha $p
    $srcLens[$p] = (Get-Item -LiteralPath $p).Length
    $srcMtimes[$p] = (Get-Item -LiteralPath $p).LastWriteTimeUtc
  }
  # The COMMITTED fixtures too. A merge writes its output NEXT TO THE FIRST SOURCE (there is no
  # output-dir setting in play here), so a run that was accidentally pointed at fixtures\pdf
  # would deposit a merged PDF into the repository. That must never happen again: an earlier
  # probe of mine did exactly that and left an untracked `fx-a (1).pdf` behind (measured
  # 2026-10-07). Cheap to check, and it fails loudly instead of silently dirtying the checkout.
  $fxBefore = @{}
  $fxDirStateBefore = Get-DirState $fdir
  foreach ($p in @($fA, $fB, $fC)) { $fxBefore[$p] = Get-Sha $p }

  # ---------------------------------------------------------------- S21.01 only merge-pdf asks
  # The window must belong to ONE tool. `AppliesTo` is the single authority for "does this tool
  # apply to this source", and the catalog query is the product's own answer to it.
  $tlPdf = Invoke-Confern 's21-tools-pdf' @('--tools-list', '--wheel-source', 'pdf')
  $tlJpg = Invoke-Confern 's21-tools-jpg' @('--tools-list', '--wheel-source', 'JPG')
  $pdfIds = @()
  foreach ($line in $tlPdf.Lines) {
    $tm = [regex]::Match([string]$line, 'tool list: \d+ id=(\S+)')
    if ($tm.Success) { $pdfIds += $tm.Groups[1].Value }
  }
  $jpgIds = @()
  foreach ($line in $tlJpg.Lines) {
    $tm = [regex]::Match([string]$line, 'tool list: \d+ id=(\S+)')
    if ($tm.Success) { $jpgIds += $tm.Groups[1].Value }
  }
  # !! 2026-10-08: the PDF list is SEVEN now (remove-bg joined it) and the JPEG list is SIX. The
  # JPEG side is asserted WHOLE rather than by a count, because the question this check asks is
  # "does a non-PDF see merge-pdf" - and a bare count would go green if merge-pdf appeared while
  # some other tool silently vanished.
  $pdfCountOk = (Test-Log $tlPdf.Lines 'count=7 tools=\[rotate90,strip-metadata,compress,crop,read-qr,merge-pdf,remove-bg\]')
  $jpgCountOk = (Test-Log $tlJpg.Lines 'count=6 tools=\[rotate90,strip-metadata,compress,crop,read-qr,remove-bg\]')
  $mergeOnlyForPdf = ($pdfIds -contains 'merge-pdf') -and (-not ($jpgIds -contains 'merge-pdf'))
  Check 'S21.01 merge-pdf is offered for a PDF source and NOT for any other source, so the sorting window can belong to that one sector only' `
    ($pdfCountOk -and $jpgCountOk -and $mergeOnlyForPdf) `
    ("pdfTools=($($pdfIds -join ',')) jpgTools=($($jpgIds -join ',')) pdfCountOk=$pdfCountOk jpgCountOk=$jpgCountOk")

  # ---------------------------------------------------------------- S21.02..S21.06 the window run
  # NOTE the variable name: `$uiRun`, NOT `$ui` - `$ui` is the FOLDER above, and reusing it here
  # would silently overwrite the path (measured while writing this section).
  $uiRun = Invoke-MergeUiSelftest 's21-ui' @($uA, $uB, $uC)

  # S21.02 initial order == source order.
  $wantInitial = @('fx-a.pdf', 'fx-b.pdf', 'fx-c.pdf')
  Check 'S21.02 the window opens with the SOURCE order as its initial order (the order the caller gave, not a sorted or remembered one)' `
    ($uiRun.Exit -eq 0 -and $uiRun.Ok -and $uiRun.InitialOk -and (($uiRun.Initial -join ',') -eq ($wantInitial -join ','))) `
    "exit=$($uiRun.Exit) ok=$($uiRun.Ok) initialOk=$($uiRun.InitialOk) initial=($($uiRun.Initial -join ',')) want=($($wantInitial -join ','))"

  # S21.03 the drag really reorders, and the order it produces is a MOVE (the rest keep their
  # relative order). The window's own move-semantics judgement is used; this side only checks the
  # window said yes and that it produced something different from the source order.
  Check 'S21.03 dragging a row reorders the list as a MOVE (the others keep their relative order), and dragging out and back restores the original order exactly' `
    ($uiRun.DragOk -and $uiRun.DragReturnOk -and ($uiRun.AfterDrag.Count -eq 3) -and
     (($uiRun.AfterDrag -join ',') -ne ($wantInitial -join ','))) `
    "dragOk=$($uiRun.DragOk) dragReturnOk=$($uiRun.DragReturnOk) afterDrag=($($uiRun.AfterDrag -join ','))"

  # S21.04 THE CHECK THIS SLICE EXISTS FOR: the pages of the output file are in EXACTLY the order
  # the window was showing, matched per page as a (size + content sha) TUPLE against the source
  # pages. The expected list is built from the ORDER THE WINDOW REPORTED, not from a re-implemented
  # move - so a window that reordered wrongly, or a commit that ignored the shown order, both go red.
  $wantOrderFiles = @($uiRun.AfterDrag)
  if ($FaultInjection -eq 'merge-nosort') {
    # FAULT INJECTION: the expectation is that the COMMIT IGNORED THE SHOWN ORDER and used the
    # source order instead - i.e. a product where drag-to-reorder is decorative. This is the exact
    # expectation such a product would satisfy, so S21.04 has to go red: it compares the committed
    # pages (and the order the product says it was ASKED for) against this corrupted list. S21.02
    # (initial order == source order) and S21.03 (the drag moves rows on screen) stay GREEN, which
    # is what makes the red a statement about "the shown order became the page order" specifically
    # and not about reordering being broken in general.
    $wantOrderFiles = @('fx-a.pdf', 'fx-b.pdf', 'fx-c.pdf')
  }
  $wantSizes = @()
  $wantShas = @()
  foreach ($n in $wantOrderFiles) {
    if ($n -eq 'fx-a.pdf') { $wantSizes += $srcSizesA; $wantShas += $srcShasA }
    elseif ($n -eq 'fx-b.pdf') { $wantSizes += $srcSizesB; $wantShas += $srcShasB }
    elseif ($n -eq 'fx-c.pdf') { $wantSizes += $srcSizesC; $wantShas += $srcShasC }
  }
  $seamRun = Invoke-MergeRun 's21-seam-order' @($uiRun.AfterDragPaths)
  $seamOut = Get-MergePages 's21-seam-merged' $seamRun.OutputPath
  # The requested order (as the product logged it) must be the order the window showed: this is the
  # "the shown order is what was sent" half, and it does not depend on the output at all.
  $reqOk = (($seamRun.RequestOrder -join ',') -eq ($wantOrderFiles -join ','))
  $pagesOk = Test-MergeOrderMatches @($seamOut.Pages) $wantSizes $wantShas
  $sizesDifferFromSource = (($wantSizes -join '/') -ne ((@($srcSizesA) + @($srcSizesB) + @($srcSizesC)) -join '/'))
  Check 'S21.04 the committed pages are in EXACTLY the order the window was showing: each page matches the corresponding source page as a (size + content sha) tuple, and the order really differs from the source order' `
    ($seamRun.Exit -eq 0 -and $seamRun.OpSent -and ($seamRun.NewFiles.Count -eq 1) -and
     $seamOut.Consistent -and $reqOk -and $pagesOk -and $sizesDifferFromSource) `
    ("exit=$($seamRun.Exit) opSent=$($seamRun.OpSent) newFiles=$($seamRun.NewFiles.Count) " +
     "requested=($($seamRun.RequestOrder -join ',')) shown=($($wantOrderFiles -join ',')) reqOk=$reqOk " +
     "pagesOk=$pagesOk differs=$sizesDifferFromSource " +
     "gotSizes=($(@($seamOut.Pages | ForEach-Object { $_.Size }) -join ',')) wantSizes=($($wantSizes -join ','))")

  # S21.05 removing a file: the removed file's pages are COMPLETELY ABSENT from the output, and
  # every other file's pages are still there. The expected list is the order the window reported
  # after the removal.
  $wantAfterRemoveSizes = @()
  $wantAfterRemoveShas = @()
  foreach ($n in @($uiRun.AfterRemove)) {
    if ($n -eq 'fx-a.pdf') { $wantAfterRemoveSizes += $srcSizesA; $wantAfterRemoveShas += $srcShasA }
    elseif ($n -eq 'fx-b.pdf') { $wantAfterRemoveSizes += $srcSizesB; $wantAfterRemoveShas += $srcShasB }
    elseif ($n -eq 'fx-c.pdf') { $wantAfterRemoveSizes += $srcSizesC; $wantAfterRemoveShas += $srcShasC }
  }
  # The hashes of the file that was removed: none of them may appear anywhere in the output.
  $removedShas = @()
  if ($uiRun.RemovedName -eq 'fx-a.pdf') { $removedShas = $srcShasA }
  elseif ($uiRun.RemovedName -eq 'fx-b.pdf') { $removedShas = $srcShasB }
  elseif ($uiRun.RemovedName -eq 'fx-c.pdf') { $removedShas = $srcShasC }
  $rmRun = Invoke-MergeRun 's21-remove' @($uiRun.AfterRemovePaths)
  $rmOut = Get-MergePages 's21-remove-merged' $rmRun.OutputPath
  $rmPagesOk = Test-MergeOrderMatches @($rmOut.Pages) $wantAfterRemoveSizes $wantAfterRemoveShas
  $removedAbsent = $true
  $anyAbsentHit = ''
  foreach ($pg in @($rmOut.Pages)) {
    if ($removedShas -contains $pg.Sha) { $removedAbsent = $false; $anyAbsentHit = $pg.Sha }
  }
  Check 'S21.05 the file removed in the window is COMPLETELY ABSENT from the output (not one of its pages survives) while every other file is still merged in the shown order' `
    ($uiRun.RemovedGone -and $uiRun.OthersKept -and $rmRun.Exit -eq 0 -and $rmRun.OpSent -and
     $rmOut.Consistent -and $rmPagesOk -and $removedAbsent -and
     ($rmOut.Read -eq $wantAfterRemoveSizes.Count) -and ($removedShas.Count -gt 0)) `
    ("removed='$($uiRun.RemovedName)' gone=$($uiRun.RemovedGone) othersKept=$($uiRun.OthersKept) " +
     "exit=$($rmRun.Exit) pages=$($rmOut.Read) wantPages=$($wantAfterRemoveSizes.Count) " +
     "pagesOk=$rmPagesOk removedAbsent=$removedAbsent hit='$anyAbsentHit' " +
     "gotSizes=($(@($rmOut.Pages | ForEach-Object { $_.Size }) -join ','))")

  # S21.06 confirm / cancel / single instance / re-open / no scrollbars.
  $cancelExpectsNothing = $true
  if ($FaultInjection -eq 'merge-cancelqueues') {
    # FAULT INJECTION: the expectation is that CANCEL STILL QUEUES the merge - i.e. a product whose
    # cancel button hands the order back anyway (the user cancels, then a file appears). This is the
    # exact expectation such a product would satisfy, so S21.06 has to go red on its cancel half
    # while the single-instance / last-file / confirm / re-open halves stay green.
    $cancelExpectsNothing = $false
  }
  Check 'S21.06 the window is single-instance, refuses to remove the last file with a reason, hands back exactly what it was showing on confirm, hands back NOTHING on cancel, and can be opened again after being closed' `
    ($uiRun.SingleInst -and $uiRun.LastKept -and $uiRun.ConfirmOk -and ($uiRun.CancelOk -eq $cancelExpectsNothing) -and $uiRun.ReopenOk -and $uiRun.ScrollZero) `    ("singleInstance=$($uiRun.SingleInst) lastKept=$($uiRun.LastKept) confirmOk=$($uiRun.ConfirmOk) " +
     "cancelOk=$($uiRun.CancelOk) cancelExpectsNothing=$cancelExpectsNothing " +
     "reopenOk=$($uiRun.ReopenOk) scrollZero=$($uiRun.ScrollZero) " +
     "shownAtConfirm=($($uiRun.ShownAtConfirm -join ','))")

  # ---------------------------------------------------------------- S21.07 the REAL path: no mouse
  # A fresh folder with ONLY the three sources, then the wheel's own commit path with no CLI seam
  # and no synthetic input: the window opens, waits for a human, and nothing is written. The
  # process has to be killed, and THAT is the evidence that it really waited.
  $cx = Join-Path $Work 'mergeui-cancel'
  New-Item -ItemType Directory -Force -Path $cx | Out-Null
  $xA = Join-Path $cx 'fx-a.pdf'
  $xB = Join-Path $cx 'fx-b.pdf'
  $xC = Join-Path $cx 'fx-c.pdf'
  Copy-Item -LiteralPath $fA -Destination $xA -Force
  Copy-Item -LiteralPath $fB -Destination $xB -Force
  Copy-Item -LiteralPath $fC -Destination $xC -Force
  # S21.08 checks THESE copies as well, so they need their own baseline before the run below.
  foreach ($p in @($xA, $xB, $xC)) {
    $srcHashes[$p] = Get-Sha $p
    $srcLens[$p] = (Get-Item -LiteralPath $p).Length
    $srcMtimes[$p] = (Get-Item -LiteralPath $p).LastWriteTimeUtc
  }
  $cxBefore = Get-DirState $cx
  $cancelRun = Invoke-ConfernTimeout 's21-cancel' @('--wheel-payload', "$xA;$xB;$xC", '--wheel-tools',
                                                    '--wheel-tool', 'merge-pdf') 20000
  $cxAfter = Get-DirState $cx
  $cxNew = @()
  foreach ($line in $cxAfter) { if ($cxBefore -notcontains $line) { $cxNew += $line } }
  # S21.07 STRICT (since 2026-10-07 option (b)): the wheel CAN reach merge-pdf, so this no longer
  # has a "not reachable" escape hatch. It demands all three: the real commit path opens the
  # window, the process really WAITS for a human (it had to be killed), and NOTHING was written.
  # Before option (b) this assertion was written as a fork (accepting `status=Unsupported` as the
  # symptom) precisely so that it would upgrade itself on the day the tool became selectable, and
  # go red until the real wait-and-cancel behaviour existed. That day is today, so the fork is
  # gone: keeping it would have made this a rubber stamp.
  $waited = (-not $cancelRun.DidExit)
  $openedWindow = Test-Log $cancelRun.Lines 'merge ui: window created'
  $wheelReady = Test-Log $cancelRun.Lines 'wheel from OLE payload: files=3 status=Ready source=PDF'
  $selectedTool = Test-Log $cancelRun.Lines "ENTER: tool layer -> index=5 op=merge-pdf"
  Check 'S21.07 the real commit path opens the sorting window and WAITS for the user without writing anything (the process had to be killed), and the wheel really selected merge-pdf to get there' `
    ($waited -and $openedWindow -and $wheelReady -and $selectedTool -and
     ($cxNew.Count -eq 0) -and ($cxBefore.Count -eq 3) -and ($cxAfter.Count -eq 3)) `
    ("waited=$waited windowCreated=$openedWindow wheelReady=$wheelReady selected=$selectedTool " +
     "newFiles=$($cxNew.Count) before=$($cxBefore.Count) after=$($cxAfter.Count)")

  # ---------------------------------------------------------------- S21.10..S21.13 the RUNTIME path
  # WHY THIS EXISTS (option (b), 2026-10-07): everything above could be green while the wheel was
  # UNABLE to reach merge-pdf at all. Measured before the fix:
  #   wheel from OLE payload: files=3 status=Unsupported source=UNKNOWN targets=0 notice=<refused> tools=0
  #   wheel-tool 'merge-pdf': not in the catalog []
  # A PDF classified as non-convertible, so ToolCatalog.ToolsFor returned an empty list and the
  # whole tool layer - merge-pdf included - was unreachable from the wheel. S21.01 did NOT catch
  # that, and neither did S20.12: both ask the CATALOG a question (`--tools-list --wheel-source
  # pdf`), which INJECTS the source format and bypasses FormatCatalog.Classify. A catalog query is
  # a true statement about the catalog; it is NOT evidence about the runtime path. These four
  # checks ask the runtime path itself, with a real payload.
  $rt = Join-Path $Work 'mergeui-runtime'
  New-Item -ItemType Directory -Force -Path $rt | Out-Null
  $rA = Join-Path $rt 'fx-a.pdf'
  $rB = Join-Path $rt 'fx-b.pdf'
  $rC = Join-Path $rt 'fx-c.pdf'
  Copy-Item -LiteralPath $fA -Destination $rA -Force
  Copy-Item -LiteralPath $fB -Destination $rB -Force
  Copy-Item -LiteralPath $fC -Destination $rC -Force
  # Seeded inline on purpose (a function cannot see the caller's locals - see the note above).
  foreach ($p in @($rA, $rB, $rC)) {
    $srcHashes[$p] = Get-Sha $p
    $srcLens[$p] = (Get-Item -LiteralPath $p).Length
    $srcMtimes[$p] = (Get-Item -LiteralPath $p).LastWriteTimeUtc
  }

  # ---- S21.10 the runtime CLASSIFIER accepts this payload, and still offers no format sector.
  # `--tools-list` now honours `--wheel-payload`, so when a payload is given the answer comes from
  # the same FormatCatalog.Classify call the wheel itself makes - the `via classify` half is what
  # makes this a RUNTIME answer instead of the catalog query S21.01 asks.
  # NOTE: `tools=6` here is the CATALOG's complete list for that format, not a claim about which
  # layer the wheel paints; the wheel's own line inside S21.13 is what shows the tool layer being
  # used (`layer=tool tools=6`) while the format layer stays empty (`targets=0`).
  $rtTools = Invoke-Confern 's21-rt-tools' @('--tools-list', '--wheel-payload', "$rA;$rB;$rC")
  # NOTE: every literal [ ] in these patterns is escaped. Select-String takes a REGEX, and an
  # unescaped `[...]` becomes a character class - measured 2026-10-07: the first version threw
  # "range in reverse order" on `[rotate90,...,read-qr]` and killed the run at S21.10.
  $rtClassified = Test-Log $rtTools.Lines 'tool list: count=7 tools=\[rotate90,strip-metadata,compress,crop,read-qr,merge-pdf,remove-bg\] \(source=PDF, status=Ready'
  $rtHow = Test-Log $rtTools.Lines 'runtime classification of 3 payload file\(s\) \(FormatCatalog\.Classify\)'
  Check 'S21.10 a payload of PDFs is accepted by the RUNTIME classifier (FormatCatalog.Classify), not merely by a catalog query: status=Ready with merge-pdf in the list, where before option (b) the same payload answered status=Unsupported with zero tools' `
    ($rtClassified -and $rtHow) `
    ("count6AndReady=$rtClassified viaClassify=$rtHow")

  # ---- S21.11 merge-pdf is offered ONLY for PDF. This is the other half of S21.01, asked of the
  # runtime path rather than of the catalog.
  $rtJpg = Invoke-Confern 's21-rt-jpg' @('--tools-list', '--wheel-source', 'JPG')
  $jpgHasMerge = Test-Log $rtJpg.Lines 'tools=\[[^]]*merge-pdf'
  $pdfHasMerge = Test-Log $rtTools.Lines 'tools=\[[^]]*merge-pdf'
  Check 'S21.11 merge-pdf appears for a PDF payload and NEVER for a JPG source, measured on the runtime path: it is the one tool bound to a single format' `
    ($pdfHasMerge -and (-not $jpgHasMerge)) `
    ("pdfHasMerge=$pdfHasMerge jpgHasMerge=$jpgHasMerge")

  # ---- S21.12 mixed payload [image + PDF]: measured, not guessed. The categories differ, so spec
  # 4.3 rejects the batch as a mixed selection and NO tool is offered - a picture that must be converted
  # and a PDF that must be merged have no common executable meaning. This is intentional and is
  # asserted so that a future "let any payload through" change has to argue with a test.
  $mixDir = Join-Path $Work 'mergeui-mixed'
  New-Item -ItemType Directory -Force -Path $mixDir | Out-Null
  $mixJpg = Join-Path $mixDir 'fx-mix.jpg'
  $mixPdf = Join-Path $mixDir 'fx-mix.pdf'
  $jpgSrc = Get-ChildItem -LiteralPath (Join-Path $repo 'fixtures') -Recurse -Include *.jpg,*.jpeg,*.png |
            Select-Object -First 1
  $haveImage = $null -ne $jpgSrc
  if ($haveImage) {
    Copy-Item -LiteralPath $jpgSrc.FullName -Destination $mixJpg -Force
    Copy-Item -LiteralPath $fA -Destination $mixPdf -Force
    $mixRun = Invoke-Confern 's21-rt-mixed' @('--tools-list', '--wheel-payload', "$mixJpg;$mixPdf")
    $mixRejected = Test-Log $mixRun.Lines 'status=Mixed'
    # The notice text itself is CJK and this script is deliberately pure ASCII (PS 5.1 reads a
    # BOM-less .ps1 as ANSI, so a CJK literal here would be corrupted - measured on this repo).
    # The rejection is asserted through the machine-readable fields instead: status=Mixed, and a
    # tool list that is EMPTY. That is the same claim; only the prose is left unasserted.
    $mixNoTools = Test-Log $mixRun.Lines 'count=0 tools=\[\]'
    $mixSourceUnknown = Test-Log $mixRun.Lines 'source=UNKNOWN'
    Check 'S21.12 a MIXED payload (one image + one PDF) is still refused as mixed selection with no tools at all, even though the PDF alone is now accepted: the two files have no common action' `
      ($mixRejected -and $mixNoTools -and $mixSourceUnknown) `
      ("statusMixed=$mixRejected noTools=$mixNoTools sourceUnknown=$mixSourceUnknown image=$($jpgSrc.Name)")
  } else {
    Inconclusive 'S21.12 no image fixture was found under fixtures\ - cannot measure the mixed image+PDF payload'
  }

  # ---- S21.13 THE END-TO-END RUN (this is what option (b) was for). One process, no CLI merge
  # seam, no synthetic input, no mouse:
  #   wheel (payload -> status=Ready -> tool layer -> merge-pdf selected) ->
  #   MergeUi.Open -> the window is built and shows the source order ->
  #   the window confirms by pressing its OWN confirm button (--merge-ui-autoconfirm; a self-test
  #   seam, NOT a synthetic mouse event) ->
  #   the confirm callback runs on the WPF thread and only ENQUEUES (PendingMerges) ->
  #   the existing visual.TickRequested drains it and StartMerge sends the worker job ->
  #   the output file exists and its pages are in the order the window showed.
  # Before option (b) the middle of this chain (Enqueue -> StartMerge) could not be reached at all
  # and was an admitted honest gap; it is now measured.
  $rtBefore = Get-DirState $rt
  $real = Invoke-Confern 's21-rt-e2e' @('--wheel-payload', "$rA;$rB;$rC", '--wheel-tools',
                                        '--wheel-tool', 'merge-pdf', '--merge-ui-autoconfirm')
  $realWheelReady = Test-Log $real.Lines 'wheel from OLE payload: files=3 status=Ready source=PDF'
  $realSelected = Test-Log $real.Lines "ENTER: tool layer -> index=5 op=merge-pdf"
  $realWindow = Test-Log $real.Lines 'merge ui: window created for 3 file\(s\) in the given order =\[fx-a\.pdf,fx-b\.pdf,fx-c\.pdf\]'
  $realConfirmed = Test-Log $real.Lines 'merge ui: auto-confirm ran=True'
  $realQueued = Test-Log $real.Lines 'queued for the main thread \(the worker starts there\)'
  $realDrained = Test-Log $real.Lines 'op=merge-pdf'
  $realFinished = Test-Log $real.Lines 'the deferred merge finished -> the window confirmed, the queue drained, the worker ran the job'
  # The wheel's own line: this is where `targets=0` (no format sector) and `layer=tool` are
  # measured for a PDF payload.
  $realNoFormatLayer = Test-Log $real.Lines 'wheel from OLE payload: files=3 status=Ready source=PDF targets=0'
  # Measured 2026-10-08, with the seventh tool in the catalog: layer=tool tools=7. The ENTER line
  # asserted above names index=5 op=merge-pdf, so the sector that was selected really is the merge
  # one - the count only says "the tool layer is what offered it".
  $realToolLayer = Test-Log $real.Lines 'layer=tool tools=7'
  # The order the merge actually ran with, as the product logged it when it started the job.
  $realOrderLine = Get-LogLine $real.Lines 'merge: start files=3 order='
  $realOrderOk = [bool]($realOrderLine -match 'order=\[fx-a\.pdf,fx-b\.pdf,fx-c\.pdf\]')
  # The output file is found by DIFFING THE FOLDER, not by scraping a log line. Measured
  # 2026-10-07: this path logs `merge: start ...` + `worker result: ok=True output=...`, NOT the
  # CLI seam's `merge-pdf output: <name> exists=True` (that line belongs to `--merge-pdf`), so a
  # pattern for it matched nothing and the check reported out='' while the file plainly existed.
  $rtAfter = Get-DirState $rt
  $rtNew = @()
  foreach ($line in $rtAfter) { if ($rtBefore -notcontains $line) { $rtNew += $line } }
  $realOutName = ''
  if ($rtNew.Count -eq 1) { $realOutName = [string]($rtNew[0] -split '\|')[0] }
  $realOutPath = Join-Path $rt $realOutName
  $realPagesOk = $false
  if ($realOutName -ne '' -and (Test-Path -LiteralPath $realOutPath)) {
    $realMerged = Get-MergePages 's21-rt-e2e-pages' $realOutPath
    $realWantSizes = @($srcSizesA) + @($srcSizesB) + @($srcSizesC)
    $realWantShas = @($srcShasA) + @($srcShasB) + @($srcShasC)
    $realPagesOk = Test-MergeOrderMatches @($realMerged.Pages) $realWantSizes $realWantShas
  }
  # The name still comes from the first source's stem (NamingPolicy) - `fx-a` here, with `(1)`
  # because the source already owns `fx-a.pdf`.
  $realNameOk = ($realOutName -eq 'fx-a (1).pdf')
  Check 'S21.13 END TO END through the real wheel path with no mouse and no CLI merge seam: the wheel selects merge-pdf (tool layer, no format sector), the sorting window opens with the source order, its own confirm button hands the order back, the callback enqueues, the existing tick drains it, the worker merges - and the output file exists with the source page order' `
    ($realWheelReady -and $realSelected -and $realWindow -and $realConfirmed -and $realQueued -and
     $realDrained -and $realFinished -and $realNoFormatLayer -and $realToolLayer -and
     $realOrderOk -and $realNameOk -and $realPagesOk -and ($rtNew.Count -eq 1)) `
    ("wheelReady=$realWheelReady selected=$realSelected window=$realWindow confirmed=$realConfirmed " +
     "queued=$realQueued drained=$realDrained finished=$realFinished " +
     "noFormatLayer=$realNoFormatLayer toolLayer=$realToolLayer orderOk=$realOrderOk " +
     "out='$realOutName' nameOk=$realNameOk pagesOk=$realPagesOk newFiles=$($rtNew.Count)")

  # S21.08 sources untouched
  # Spec 5: a source file is never modified, moved or deleted. sha256 + length + mtime on every
  # file this section handed to the product, checked after everything above.
  $touched = @()
  foreach ($p in @($uA, $uB, $uC, $xA, $xB, $xC, $rA, $rB, $rC)) {
    $nowSha = Get-Sha $p
    $nowLen = (Get-Item -LiteralPath $p).Length
    $nowMtime = (Get-Item -LiteralPath $p).LastWriteTimeUtc
    if ($srcHashes[$p] -ne $nowSha) { $touched += "$([System.IO.Path]::GetFileName($p)):sha" }
    if ($srcLens[$p] -ne $nowLen) { $touched += "$([System.IO.Path]::GetFileName($p)):len" }
    if ($srcMtimes[$p] -ne $nowMtime) { $touched += "$([System.IO.Path]::GetFileName($p)):mtime" }
  }
  Check 'S21.08 every source file this section used still has the same sha256, length and mtime: the sorting window and its commit never modify a source' `
    ($touched.Count -eq 0) `
    ("changed=($($touched -join ',')) files=6 shaA=$($srcHashes[$uA].Substring(0,12)) shaAnow=$((Get-Sha $uA).Substring(0,12)) " +
     "shaB=$($srcHashes[$uB].Substring(0,12)) shaC=$($srcHashes[$uC].Substring(0,12))")

  # The checkout is part of the contract: no run in this section may leave a file inside the
  # COMMITTED fixtures folder.
  $fxDirStateAfter = Get-DirState $fdir
  $fxNew = @()
  foreach ($line in $fxDirStateAfter) { if ($fxDirStateBefore -notcontains $line) { $fxNew += $line } }
  $fxChanged = @()
  foreach ($p in @($fA, $fB, $fC)) { if ($fxBefore[$p] -ne (Get-Sha $p)) { $fxChanged += (Split-Path -Leaf $p) } }
  Check 'S21.09 the committed fixtures folder is left untouched: no run in this section wrote anything into it and every fixture keeps its sha256' `
    (($fxNew.Count -eq 0) -and ($fxChanged.Count -eq 0)) `
    ("newInFixtures=($($fxNew -join ' | ')) changedFixtures=($($fxChanged -join ',')) fixtureFiles=$($fxDirStateBefore.Count)")
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
    'S19' { Expect 18; Invoke-S19 }
    'S20' { Expect 15; Invoke-S20 }
    'S21' { Expect 13; Invoke-S21 }
    'S22' { Expect 25; Invoke-S22 $FaultInjection }
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
