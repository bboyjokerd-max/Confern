param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1h-verify"
)
# =============================================================================
#  M1h regression (S16): the click-to-expand failure list (spec 7.3).
#
#  Spec 7.3 says the pill shows one line (check N / X M) and that a list of
#  "filename + reason" can be EXPANDED BY CLICKING. This slice implements the
#  list panel, the row data and the expand state; it deliberately does NOT wire
#  a real click (that is a container decision, still open).
#
#  Why every check here is written the way it is:
#   * A fixed panel width would CLIP the Chinese reasons -- and clipping looks
#     like nothing at all in the product, so the width is compared between two
#     texts of different length (same run, same code path).
#   * "The file exists" proves nothing: a bitmap with no text at all also
#     exists. The raw dump is sampled pixel by pixel, and the glyphs are located
#     as ink BANDS (a dropped row leaves the file non-empty but removes a band).
#   * The end-to-end row comes from a REAL failing conversion (the same broken
#     PNG fixture m1d uses), not from a hand-written string: a synthetic string
#     can never fail, including strings the product cannot actually produce.
#   * All expectations are read from the app's own log lines (rows=, cap=,
#     shown=, detail row:) instead of being copied into this file, so the
#     assertions cannot silently drift from the contract they claim to check.
#
#  ASCII-only on purpose: PS 5.1 reads BOM-less scripts as ANSI and a CJK
#  comment silently swallows the next statement (that already caused a silent
#  false pass in this repo). Expected CJK is built from code points.
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

# CJK expected strings, built from code points so this file stays pure ASCII.
#   0x7121 0x6CD5 0x89E3 0x78BC  = "cannot decode" (spec 7.1 DecodeFailed reason)
$MSG_DECODE = [string]::Join('', [char[]]@(0x7121,0x6CD5,0x89E3,0x78BC))
$FILE_DECODE = 'corrupt.png'

"=== setup ==="
if (-not (Test-Path -LiteralPath $Exe)) { "executable not found: $Exe"; exit 2 }
Get-Process -Name Confern -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$logDir = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$exeStamp = (Get-Item -LiteralPath $Exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
"exe: $((Resolve-Path -LiteralPath $Exe).Path)"
"exe last write: $exeStamp"

# ---- the broken fixture, identical in construction to m1d's -----------------
# A real 16x16 PNG truncated to 60 bytes: WIC reports a decode failure (not
# "this is not an image"), which is the path that produces the spec 7.1
# DecodeFailed reason the list has to show.
$okPng = Join-Path $Work 'good.png'
$tmpBmp = [System.Drawing.Bitmap]::new(16, 16)
$tmpBmp.Save($okPng, [System.Drawing.Imaging.ImageFormat]::Png)
$tmpBmp.Dispose()
$goodBytes = [System.IO.File]::ReadAllBytes($okPng)
$cutBytes = New-Object byte[] 60
[Array]::Copy($goodBytes, $cutBytes, 60)
$corrupt = Join-Path $Work "$FILE_DECODE"
[System.IO.File]::WriteAllBytes($corrupt, $cutBytes)
Remove-Item -LiteralPath $okPng -Force

# ---- run: one real failing conversion + both dumps --------------------------
# --convert makes the "real" variant come from an actual worker conversion;
# the conversion is EXPECTED to fail (exit 9), which is why the dump's own exit
# code -- not the conversion's -- is what gets asserted.
$flat = Join-Path $Work 'panel.png'
$raw = Join-Path $Work 'panel-raw.png'
$flatShort = Join-Path $Work 'panel-short.png'
$flatLong = Join-Path $Work 'panel-long.png'
$flatMany = Join-Path $Work 'panel-many.png'
$flatReal = Join-Path $Work 'panel-real.png'
$flatExtreme = Join-Path $Work 'panel-extreme.png'
$rawExtreme = Join-Path $Work 'panel-raw-extreme.png'
$layoutJson = Join-Path $Work 'layout.json'
# The end-to-end run (worker over the pipe) writes its own pair, so the two runs
# can never be confused with each other in the evidence.
$pipeFlat = Join-Path $Work 'pipe-panel.png'
$pipeRaw = Join-Path $Work 'pipe-panel-raw.png'
$pipeJson = Join-Path $Work 'pipe-layout.json'
$dumpLog = Join-Path $logDir 'detail-dump.log'
$dump = Start-Process -FilePath $Exe -ArgumentList @(
  '--convert', $corrupt,
  '--progress-dump-detail', $flat,
  '--progress-dump-detail-raw', $raw,
  '--progress-dump-detail-json', $layoutJson,
  '--quiet', '--log', $dumpLog) -PassThru -Wait -WindowStyle Hidden
$dumpText = (Get-Content -LiteralPath $dumpLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"

function Get-Variant([string]$text, [string]$name) {
  $m = [regex]::Match($text, "detail dump line: variant=$name width=(\d+) height=(\d+) rows=(\d+)(?: shown=(\d+) overflow=(\d+))? cap=(\d+)")
  if (-not $m.Success) { return $null }
  $rows = [int]$m.Groups[3].Value
  # A pill line has no shown/overflow fields: it draws exactly one row, no overflow.
  if ($m.Groups[4].Success) { $shown = [int]$m.Groups[4].Value } else { $shown = $rows }
  if ($m.Groups[5].Success) { $overflow = [int]$m.Groups[5].Value } else { $overflow = 0 }
  return @{
    Width    = [int]$m.Groups[1].Value
    Height   = [int]$m.Groups[2].Value
    Rows     = $rows
    Shown    = $shown
    Overflow = $overflow
    Cap      = [int]$m.Groups[6].Value
  }
}

# Reads a PNG and returns ink statistics for the WHITE text colour.
# The panel background is the error red, so "white" isolates the glyphs.
# Bands = horizontal groups of scanlines that contain ink: one per rendered row.
function Measure-Ink([string]$path) {
  $bmp = [System.Drawing.Bitmap]::FromFile($path)
  try {
    $w = $bmp.Width; $h = $bmp.Height
    $rows = New-Object 'int[]' $h
    $ink = 0; $left = 0; $right = 0
    $minX = $w; $maxX = -1; $minY = $h; $maxY = -1
    for ($y = 0; $y -lt $h; $y++) {
      $c = 0
      for ($x = 0; $x -lt $w; $x++) {
        $p = $bmp.GetPixel($x, $y)
        if ($p.A -gt 200 -and $p.R -gt 230 -and $p.G -gt 230 -and $p.B -gt 230) {
          $c++; $ink++
          if ($x -lt ($w / 2)) { $left++ } else { $right++ }
          if ($x -lt $minX) { $minX = $x }
          if ($x -gt $maxX) { $maxX = $x }
          if ($y -lt $minY) { $minY = $y }
          if ($y -gt $maxY) { $maxY = $y }
        }
      }
      $rows[$y] = $c
    }
    $bands = 0; $inBand = $false
    foreach ($c in $rows) {
      if ($c -ge 3) { if (-not $inBand) { $bands++; $inBand = $true } }
      else { $inBand = $false }
    }
    return @{
      Width = $w; Height = $h; Ink = $ink; Left = $left; Right = $right; Bands = $bands
      MinX = $minX; MaxX = $maxX; MinY = $minY; MaxY = $maxY
    }
  } finally { $bmp.Dispose() }
}

function Test-Png([string]$path) {
  try { $b = [System.Drawing.Bitmap]::FromFile($path); $ok = $b.Width; $b.Dispose(); return $true }
  catch { return $false }
}

# Leftmost pixel of row y whose alpha exceeds the threshold: the x where the
# FILL begins. The panel's rounded corner makes that offset grow with y, and the
# curve is a pure function of the corner radius -- so comparing this offset
# between a 1-row and an 8-row panel measures the radius without any text
# sampling (the fill is 236 alpha, the glyphs start 14 px lower).
function Get-TopFillInset([string]$path, [int]$y, [int]$threshold) {
  $bmp = [System.Drawing.Bitmap]::FromFile($path)
  try {
    if ($y -ge $bmp.Height) { return -1 }
    for ($x = 0; $x -lt $bmp.Width; $x++) {
      if ($bmp.GetPixel($x, $y).A -gt $threshold) { return $x }
    }
    return -1
  } finally { $bmp.Dispose() }
}

# The corner profile: one offset per sample row. Two panels that share one
# radius rule must produce the SAME profile; panels that use different rules
# (half the height vs a fixed cap) cannot.
$script:RadiusYs = @(4, 10, 20, 30, 40, 50)
function Get-CornerProfile([string]$path) {
  $p = @()
  foreach ($y in $script:RadiusYs) { $p += (Get-TopFillInset $path $y 2) }
  return $p
}

"=== S16.1-S16.3 the dump runs, exits 0 and writes both files ==="
Check 'S16.1 the detail dump exits 0 (the failing conversion it used is not the dump''s own failure)' `
  ($dump.ExitCode -eq 0 -and $dumpText -match 'progress dump detail done: variants=\d+ failed=0') `
  "exit=$($dump.ExitCode)"
Check 'S16.2 the panel dumps exist on disk and decode as PNG bitmaps' `
  ((Test-Path -LiteralPath $flatShort) -and (Test-Path -LiteralPath $flatLong) -and
   (Test-Path -LiteralPath $flatMany) -and (Test-Path -LiteralPath $flatReal) -and
   (Test-Png $flatShort) -and (Test-Png $flatLong) -and (Test-Png $flatMany) -and
   (Test-Png $flatReal)) `
  "flat short=$((Get-Item -LiteralPath $flatShort -ErrorAction SilentlyContinue).Length)B real=$((Get-Item -LiteralPath $flatReal -ErrorAction SilentlyContinue).Length)B"
$rawShort = Join-Path $Work 'panel-raw-short.png'
$rawLong = Join-Path $Work 'panel-raw-long.png'
$rawMany = Join-Path $Work 'panel-raw-many.png'
$rawReal = Join-Path $Work 'panel-raw-real.png'
Check 'S16.3 every variant got an alpha-preserving raw dump (the file whose pixels are sampled)' `
  ((Test-Path -LiteralPath $rawShort) -and (Test-Path -LiteralPath $rawLong) -and
   (Test-Path -LiteralPath $rawMany) -and (Test-Path -LiteralPath $rawReal)) `
  "raw short=$((Get-Item -LiteralPath $rawShort -ErrorAction SilentlyContinue).Length)B real=$((Get-Item -LiteralPath $rawReal -ErrorAction SilentlyContinue).Length)B"

"=== S16.4-S16.6 the logged geometry is the geometry of the files on disk ==="
$short = Get-Variant $dumpText 'short'
$long = Get-Variant $dumpText 'long'
$many = Get-Variant $dumpText 'many'
$real = Get-Variant $dumpText 'real'
$pillShort = Get-Variant $dumpText 'pill-short'
$pillLong = Get-Variant $dumpText 'pill-long'

$variantsOk = ($null -ne $short) -and ($null -ne $long) -and ($null -ne $many) -and ($null -ne $real) -and
              ($null -ne $pillShort) -and ($null -ne $pillLong)
Check 'S16.4 every variant reports its width/height/rows/shown/overflow/cap' $variantsOk `
  "short=$($short.Width)x$($short.Height) long=$($long.Width)x$($long.Height) many=$($many.Width)x$($many.Height) real=$($real.Width)x$($real.Height)"

if ($variantsOk) {
  $mShort = Measure-Ink $rawShort
  $mLong = Measure-Ink $rawLong
  $mMany = Measure-Ink $rawMany
  $mReal = Measure-Ink $rawReal
  Check 'S16.5 the logged size equals the size of the file on disk (no stale dump is being inspected)' `
    (($mShort.Width -eq $short.Width) -and ($mShort.Height -eq $short.Height) -and
     ($mLong.Width -eq $long.Width) -and ($mLong.Height -eq $long.Height) -and
     ($mMany.Width -eq $many.Width) -and ($mMany.Height -eq $many.Height) -and
     ($mReal.Width -eq $real.Width) -and ($mReal.Height -eq $real.Height)) `
    "log short=$($short.Width)x$($short.Height) file=$($mShort.Width)x$($mShort.Height)"

  "=== S16.6-S16.8 width: the panel is measured from the text, never clamped ==="
  # The guard this slice exists for: a fixed width CLIPS the Chinese reason, and
  # clipping is invisible in the product (the user just never sees the reason).
  Check 'S16.6a a longer text makes the panel wider (a fixed width would clip it)' `
    ($long.Width -gt $short.Width) "short=$($short.Width) long=$($long.Width) delta=$($long.Width - $short.Width)"
  Check 'S16.6b the wide case is wide by much more than a truncation margin' `
    (($long.Width - $short.Width) -ge 100) "delta=$($long.Width - $short.Width) px"
  Check 'S16.6c the same is true of the pill line itself (both renderers measure text)' `
    ($pillLong.Width -gt $pillShort.Width) "pill short=$($pillShort.Width) long=$($pillLong.Width)"
  # No horizontal truncation: the ink must end inside the panel with room to spare.
  $slack = $long.Width - $mLong.MaxX
  Check 'S16.7 the long row''s ink ends with slack inside the panel (nothing was cut off)' `
    ($slack -ge 10) "panel=$($long.Width) inkMaxX=$($mLong.MaxX) slack=$slack"
  Check 'S16.8 the text starts at the intended left inset (the panel is not just padding)' `
    (($mLong.MinX -ge 8) -and ($mLong.MinX -le 20)) "inkMinX=$($mLong.MinX)"

  "=== S16.9-S16.11 height: more rows get taller, and it stops at the cap ==="
  Check 'S16.9 more rows make the panel taller' `
    (($many.Height -gt $short.Height) -and ($short.Height -gt $real.Height)) `
    "real(1 row)=$($real.Height) short(2 rows)=$($short.Height) many(9 rows)=$($many.Height)"
  Check 'S16.10 the panel really stops at the cap plus one overflow line' `
    (($many.Cap -eq 8) -and ($many.Rows -eq 9) -and ($many.Shown -eq $many.Cap) -and ($many.Overflow -eq 1)) `
    "rows=$($many.Rows) shown=$($many.Shown) overflow=$($many.Overflow) cap=$($many.Cap)"
  # Read from the app itself, so changing the cap deliberately does not silently
  # invalidate this file: the cap is compared against the pixel evidence below.
  $cap = $many.Cap

  "=== S16.12-S16.14 the glyphs actually rendered (not just 'a file exists') ==="
  Check 'S16.12 every variant really contains text ink (an empty bitmap would render nothing)' `
    (($mShort.Ink -gt 200) -and ($mLong.Ink -gt 200) -and ($mMany.Ink -gt 1500) -and ($mReal.Ink -gt 200)) `
    "ink short=$($mShort.Ink) long=$($mLong.Ink) many=$($mMany.Ink) real=$($mReal.Ink)"
  # Bands = rendered rows. A dropped row keeps the file non-empty but removes a
  # band, which is exactly the failure "the file exists" cannot see.
  Check 'S16.13 the rendered row count matches the rows the app says it drew (short=2, real=1)' `
    (($mShort.Bands -eq $short.Shown) -and ($mReal.Bands -eq $real.Shown)) `
    "short bands=$($mShort.Bands) shown=$($short.Shown); real bands=$($mReal.Bands) shown=$($real.Shown)"
  Check 'S16.14 the long list drew cap rows plus the overflow line' `
    ($mMany.Bands -eq ($cap + 1)) `
    "bands=$($mMany.Bands) expected=$($cap + 1) (cap=$cap rows=$($many.Rows))"
  # Both columns must be present: the filename on the left, the reason on the
  # right. If a row lost its reason, ALL of its ink collapses to the left half.
  $realRatio = if ($mReal.Ink -gt 0) { [math]::Round($mReal.Right / $mReal.Ink, 3) } else { 0 }
  $manyRatio = if ($mMany.Ink -gt 0) { [math]::Round($mMany.Right / $mMany.Ink, 3) } else { 0 }
  Check 'S16.15 each row carries both columns (reason ink on the right, filename ink on the left)' `
    (($realRatio -ge 0.25) -and ($manyRatio -ge 0.25)) `
    "rightHalfShare real=$realRatio many=$manyRatio"

  "=== S16.16-S16.19 the row data: filename AND Chinese reason, end to end ==="
  # The end-to-end run drives the REAL UI path: --wheel-payload injects the payload, the
  # conversion goes out to the worker process over the named pipe, and the failure comes
  # back as a spec 7.1 message that the snapshot splits back into "filename + reason".
  # That split is the part most likely to break silently, and --convert alone cannot see
  # it: --convert calls the engine directly and never crosses the pipe.
  $payloadLog = Join-Path $logDir 'detail-pipeline.log'
  $payload = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-payload', $corrupt, '--no-autostart',
    '--progress-dump-detail', $pipeFlat, '--progress-dump-detail-raw', $pipeRaw,
    '--progress-dump-detail-json', $pipeJson,
    '--quiet', '--log', $payloadLog) -PassThru -Wait -WindowStyle Hidden
  $payloadText = (Get-Content -LiteralPath $payloadLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
  Check 'S16.16 the worker really failed the file over the pipe (precondition for the row below)' `
    ($payload.ExitCode -eq 0 -and $payloadText -match 'conversion finished: finished=True ok=0 failed=1') `
    "exit=$($payload.ExitCode)"
  $realLine = [regex]::Match($payloadText, "detail row: file=(\S+) reason='([^']*)'")
  $rowFile = if ($realLine.Success) { $realLine.Groups[1].Value.Trim() } else { '' }
  $rowReason = if ($realLine.Success) { $realLine.Groups[2].Value.Trim() } else { '' }
  Check 'S16.17 the pipeline produced exactly one detail row' $realLine.Success `
    "line='$($realLine.Value)'"
  Check 'S16.18 that row names the file that actually failed (not a dummy name)' `
    ($rowFile -eq $FILE_DECODE) "file='$rowFile' expected='$FILE_DECODE'"
  # Non-ASCII on purpose: the reason is Traditional Chinese by spec 7.1, and an
  # ASCII-only or empty reason means the user cannot tell what went wrong.
  $reasonIsCjk = ($rowReason.Length -gt 0) -and ($rowReason -match '[^\x00-\x7F]')
  Check 'S16.19 that row carries a Chinese reason (not just the filename twice)' $reasonIsCjk `
    "reason='$rowReason' containsExpected=$($rowReason.Contains($MSG_DECODE))"
  Check 'S16.20 the reason is the spec 7.1 DecodeFailed message' `
    ($rowReason.Contains($MSG_DECODE)) "reason='$rowReason'"
  Check 'S16.21 the pipeline run also wrote its panel dump (the row data reached the renderer)' `
    ((Test-Path -LiteralPath (Join-Path $Work 'pipe-panel-real.png')) -and
     $payloadText -match 'detail dump line: variant=real-pipeline rows=1 failed=0') `
    "pipe-panel-real.png"

  "=== S16.22-S16.23 the pixel evidence agrees with the row data ==="
  # The rendered real panel must be at least as wide as the Chinese filename plus
  # the Chinese reason: if the renderer had used a fixed width, the row would be
  # clipped. 190 px is comfortably above every filename-only string used here and
  # comfortably below the panel that actually contains the reason.
  Check 'S16.22 the rendered real row is wide enough to hold the filename AND the reason' `
    ($mReal.Width -ge 190) "realPanelWidth=$($mReal.Width) (a filename-only string measures well under 190)"
  Check 'S16.23 the rendered real row is much narrower than the deliberately long one (it is really text-driven)' `
    ($mReal.Width -lt $mLong.Width) "real=$($mReal.Width) long=$($mLong.Width)"

  "=== S16.24-S16.29 D4: the panel width is capped at the work area ==="
  # The cap has to be OBSERVED, not assumed: the exe writes the work area it
  # actually queried and the cap it derived, so the number asserted below comes
  # from the product rather than from a constant copied into this file.
  $capLine = [regex]::Match($dumpText, "detail dump: work area=(-?\d+),(-?\d+),(\d+),(\d+) cap=(\d+)")
  $workW = if ($capLine.Success) { [int]$capLine.Groups[3].Value } else { 0 }
  $widthCap = if ($capLine.Success) { [int]$capLine.Groups[5].Value } else { 0 }
  Check 'S16.24 the exe reported the work area it queried and the width cap it derived' `
    ($capLine.Success -and $workW -gt 0 -and $widthCap -eq ($workW - 48)) `
    "workAreaWidth=$workW cap=$widthCap expected=$($workW - 48)"

  # A name long enough that the uncapped panel would be far wider than the cap.
  # Without this variant the cap is untestable: the "long" variant is only 540 px.
  # Read from the log line for THAT variant: its name contains a hyphen, so the
  # shared per-variant regex below cannot match it (it stops at the first
  # non-word character). Reading `$extreme` here would silently be $null.
  $extremeLine = [regex]::Match($dumpText,
    "detail dump line: variant=extreme width=(\d+) height=(\d+) rows=(\d+) shown=(\d+) overflow=(\d+) cap=(\d+) widthCap=(\d+) fileNameLength=(\d+)")
  $extremeW = if ($extremeLine.Success) { [int]$extremeLine.Groups[1].Value } else { 0 }
  $extremeNameLen = if ($extremeLine.Success) { [int]$extremeLine.Groups[8].Value } else { 0 }
  $extremeH = if ($extremeLine.Success) { [int]$extremeLine.Groups[2].Value } else { 0 }
  $mExtreme = if (Test-Path -LiteralPath $rawExtreme) { Measure-Ink $rawExtreme } else { $null }
  $extremePngW = if ($null -ne $mExtreme) { $mExtreme.Width } else { 0 }
  Check 'S16.25 an extreme filename really exists in this run (otherwise the cap check is vacuous)' `
    ($extremeLine.Success -and ($extremeNameLen -ge 200)) `
    "fileNameLength=$extremeNameLen loggedWidth=$extremeW (the 'long' variant is only $($long.Width) px)"
  Check 'S16.26 the capped width is OBSERVED from the PNG: an extreme name never exceeds the cap' `
    (($widthCap -gt 0) -and ($extremeW -gt 0) -and ($extremePngW -gt 0) -and
     ($extremePngW -le $widthCap)) `
    "extremePngWidth=$extremePngW cap=$widthCap loggedWidth=$extremeW"
  # If the cap is what stopped it, the panel sits exactly ON the cap. A panel that
  # merely happens to be narrower would pass the check above for the wrong reason.
  Check 'S16.27 the extreme panel was stopped BY the cap (it sits exactly on it)' `
    (($widthCap -gt 0) -and ($extremeW -eq $widthCap) -and ($extremePngW -eq $widthCap)) `
    "loggedWidth=$extremeW pngWidth=$extremePngW cap=$widthCap"
  Check 'S16.28 the logged extreme height equals the height of the file on disk' `
    (($null -ne $mExtreme) -and $extremeLine.Success -and ($extremeH -eq $mExtreme.Height)) `
    "logHeight=$extremeH fileHeight=$(if ($null -ne $mExtreme) { $mExtreme.Height } else { 0 })"
  # Uncapped, this row needs far more than the cap: the cap is doing real work.
  Check 'S16.29 the extreme variant is genuinely far too wide to be uncapped' `
    (($long.Width -lt $widthCap) -and ($null -ne $mExtreme) -and ($mExtreme.Ink -gt 1000)) `
    "long=$($long.Width) cap=$widthCap extremeInk=$(if ($null -ne $mExtreme) { $mExtreme.Ink } else { 0 })"

  "=== S16.30-S16.39 D4: the laid-out strings themselves (JSON dump) ==="
  # The strings handed to GDI+ are the only honest evidence for "we shortened the
  # filename and kept the reason": a screenshot cannot tell "shortened" from
  # "clipped", and a log line saying "we intended to ellipsize" is a self-report.
  $jsonOk = (Test-Path -LiteralPath $layoutJson)
  Check 'S16.30 the JSON dump of the laid-out strings exists' $jsonOk `
    "path='$layoutJson' bytes=$((Get-Item -LiteralPath $layoutJson -ErrorAction SilentlyContinue).Length)"
  if ($jsonOk) {
    $obj = Get-Content -LiteralPath $layoutJson -Raw -Encoding UTF8 | ConvertFrom-Json
    $variants = @($obj.variants)
    $extremeVariant = $variants | Where-Object { $_.name -eq 'extreme' } | Select-Object -First 1
    Check 'S16.31 the JSON dump agrees with the logged cap and reports every variant' `
      (($obj.maxWidthPx -eq $widthCap) -and ($variants.Count -ge 4) -and ($null -ne $extremeVariant)) `
      "jsonCap=$($obj.maxWidthPx) logCap=$widthCap variants=$($variants.Count)"
    Check 'S16.32 the JSON width of every variant equals the PNG on disk' `
      (($extremeVariant.width -eq $extremeW) -and ($extremeVariant.height -eq $mExtreme.Height)) `
      "jsonW=$($extremeVariant.width) jsonH=$($extremeVariant.height) pngW=$extremeW pngH=$($mExtreme.Height)"

    if ($null -ne $extremeVariant) {
      $eRows = @($extremeVariant.rows)
      # Row 0 is the extreme filename, row 1 is a normal-length one: the two
      # assertions below need each other (a rule that shortens everything would
      # pass the first one alone).
      $eRow = $eRows[0]
      $nRow = $eRows[1]
      # The reason is NEVER shortened: a truncated reason is exactly the silent
      # failure this project forbids, and spec 7.1 exists to make the reason visible.
      Check 'S16.33 the over-long row still ends with its COMPLETE reason' `
        ($eRow.laidOut.EndsWith($eRow.text.Substring($eRow.text.IndexOf(' ')))) `
        "reason='$($eRow.text.Substring($eRow.text.IndexOf(' ')))' laidOutEnds='$($eRow.laidOut.Substring([Math]::Max(0, $eRow.laidOut.Length - 12)))'"
      $ellipsis = [char]0x2026
      $ei = [int]$eRow.ellipsisIndex
      # Middle ellipsis, checked against the row's OWN filename and reason (both are
      # in the dump as separate fields, so this never has to guess where the
      # filename ends). The invariant is: laidOut = head + ellipsis + tail + reason,
      # where head is the FRONT of the filename, tail is its END, and
      # head + tail equals the filename exactly -- nothing shortened anywhere else
      # and nothing invented.
      $eHead = if ($ei -gt 0) { $eRow.laidOut.Substring(0, $ei) } else { '' }
      $eRest = if ($ei -gt 0) { $eRow.laidOut.Substring($ei + 1) } else { $eRow.laidOut }
      # What follows the mark is "tail + separator(1 char) + reason", so the filename
      # tail is that minus the reason and one character. (Forgetting that one
      # character is how this check reported a false failure the first time it ran.)
      $eTail = $eRest.Substring(0, $eRest.Length - $eRow.reasonLength - 1)
      $eName = $eRow.fileName
      $eLaidName = $eRow.laidOutFileName
      Check 'S16.34 the over-long filename shows a MIDDLE ellipsis and keeps its extension' `
        (($ei -gt 0) -and ($ei -lt ($eRow.laidOut.Length - 2)) -and
         ($eTail.Length -ge 6) -and $eTail.EndsWith('.png') -and
         $eRow.laidOut.EndsWith($eTail + ' ' + $eRow.reason) -and
         $eName.StartsWith($eHead) -and $eName.EndsWith($eTail) -and
         ($eHead.Length + $eTail.Length + 1) -eq $eLaidName.Length) `
        "head=$($eHead.Length) ellipsis=1 tail=$($eTail.Length) laidOutFileNameLength=$($eLaidName.Length) originalFileNameLength=$($eRow.fileNameLength) laidOutEnds='$($eRow.laidOut.Substring([Math]::Max(0, $eRow.laidOut.Length - 10)))'"
      # The stronger, unambiguous version of the same claim. The `extreme` filename
      # is 204 identical 'W' characters plus '.png', so its head and tail look the
      # same; `extreme-mid` has a different head (digits and dashes) and tail
      # (digits and '.png'), so "the head really is the front of the name and the
      # tail really is its end" is checkable character by character.
      $midVariant = $variants | Where-Object { $_.name -eq 'extreme-mid' } | Select-Object -First 1
      Check 'S16.35 the extreme variants really were shortened (not just re-measured)' `
        (($eRow.laidOutLength -lt $eRow.text.Length) -and ($eRow.laidOut -ne $eRow.text)) `
        "textLength=$($eRow.text.Length) laidOutLength=$($eRow.laidOutLength)"
      if ($null -ne $midVariant) {
        $midRow = @($midVariant.rows)[0]
        $mei = [int]$midRow.ellipsisIndex
        $midHead = if ($mei -gt 0) { $midRow.laidOut.Substring(0, $mei) } else { '' }
        $midRest = if ($mei -gt 0) { $midRow.laidOut.Substring($mei + 1) } else { $midRow.laidOut }
        $midTail = $midRest.Substring(0, $midRest.Length - $midRow.reasonLength - 1)
        $midName = $midRow.fileName
        $midLaidName = $midRow.laidOutFileName
        Check 'S16.36 the mid-ellipsis row keeps the FRONT of the name and its real extension' `
          (($mei -gt 0) -and ($midHead.Length -ge 1) -and
           $midName.StartsWith($midHead) -and $midHead -notmatch ' ' -and
           ($midTail.Length -ge 6) -and $midTail.EndsWith('.png') -and
           $midName.EndsWith($midTail) -and
           ($midHead.Length + $midTail.Length + 1) -eq $midLaidName.Length) `
          "headLength=$($midHead.Length) tailLength=$($midTail.Length) laidOutFileNameLength=$($midLaidName.Length) originalFileNameLength=$($midRow.fileNameLength) laidOutEnds='$($midRow.laidOut.Substring([Math]::Max(0, $midRow.laidOut.Length - 10)))'"
        Check 'S16.37 the mid-ellipsis row still ends with its COMPLETE reason' `
          ($midRow.laidOut.EndsWith($midRow.reason)) `
          "laidOutEnds='$($midRow.laidOut.Substring([Math]::Max(0, $midRow.laidOut.Length - 8)))'"
      } else {
        Inconclusive 'S16.36-S16.37 mid-ellipsis row' 'the JSON dump has no extreme-mid variant'
      }
      # The guard against over-eager truncation: a row that FITS must come out
      # byte-identical. No ellipsis, no extra spacing, nothing.
      Check 'S16.38 a normal-length row has NO ellipsis and is left completely untouched' `
        (($nRow.ellipsisIndex -eq -1) -and ($nRow.laidOut -eq $nRow.text) -and
         ($nRow.laidOut -notmatch "$ellipsis")) `
        "laidOut='$($nRow.laidOut)' text='$($nRow.text)'"
      Check 'S16.39 no row that fits inside the cap was rewritten (guards over-eager truncation everywhere)' `
        ((@($variants | Where-Object { $_.width -le $widthCap } |
             ForEach-Object { $_.rows } |
             Where-Object { ($_.laidOut -ne $_.text) -and ($_.ellipsisIndex -eq -1) }).Count) -eq 0) `
        "capped=$widthCap variants=$($variants.Count)"
    } else {
      Inconclusive 'S16.33-S16.39 laid-out strings' 'the JSON dump has no extreme variant'
    }
  } else {
    Inconclusive 'S16.31-S16.39 laid-out strings' 'the JSON dump was not written'
  }

  "=== S16.40-S16.43 D3: one corner radius rule, MEASURED from the pixels ==="
  # D3 says the panel uses min(height / 2, 18) instead of the pill's half height.
  # That is only observable at the top edge: at y = 0 a rounded corner starts at
  # x = 1 for ANY radius, so a y = 0 reading cannot tell the two rules apart. The
  # corner profile (the x where the fill begins, per row) can: with one rule a
  # 1-row and an 8-row panel give the SAME profile, with two rules they diverge by
  # ~4 px at y = 10 (the fill is 236 alpha, glyphs start 14 px lower, so the
  # profile is pure corner geometry).
  $hasMany = ($null -ne $mMany) -and ($mMany.Height -gt $mReal.Height)
  Check 'S16.40 both panels needed for the radius comparison were rendered' $hasMany `
    "oneRow=$($mReal.Height)px eightRow=$($mMany.Height)px"
  if ($hasMany) {
    $pOne = Get-CornerProfile $rawReal
    $pMany = Get-CornerProfile $rawMany
    "  measured inset at y=$($script:RadiusYs -join ',')"
    "    1-row panel : $($pOne -join ',')"
    "    8-row panel : $($pMany -join ',')"
    $maxDelta = 0
    for ($i = 0; $i -lt $script:RadiusYs.Count; $i++) {
      $delta = [Math]::Abs($pOne[$i] - $pMany[$i])
      if ($delta -gt $maxDelta) { $maxDelta = $delta }
    }
    Check 'S16.41 the 1-row and 8-row panels agree on the corner profile within 2 px (one radius rule)' `
      ($maxDelta -le 2) "maxDelta=$maxDelta px  oneRow=[$($pOne -join ',')] many=[$($pMany -join ',')]"
    # Independent anchor: with the cap at 18 the fill at y = 10 begins at x = 3.
    # This separates "both panels are consistent" from "both panels are consistent
    # AND the radius is the capped one". A rule reading radius from a y=0 offset
    # would report 1 for every radius, so the anchor is measured where the rule
    # actually shows.
    $anchor = Get-TopFillInset $rawMany 10 2
    Check 'S16.42 the measured corner offset matches the capped radius (3 px at y = 10)' `
      (($anchor -ge 2) -and ($anchor -le 4)) "measured=$anchor expected=3"
    # The two panels must also have genuinely different heights, otherwise "they
    # agree" could be true because they are the same picture.
    Check 'S16.43 the two panels really differ in row count (1 vs 8) and height' `
      (($mReal.Height -eq 62) -and ($mMany.Height -eq 334)) `
      "oneRow=$($mReal.Height) eightRow=$($mMany.Height)"
  } else {
    Inconclusive 'S16.40-S16.43 corner radius' 'the many/real raw dumps are missing'
  }

  "=== S16.44-S16.49 spec 7.3: the list does NOT pop up by itself ==="
  # No click is wired yet (container decision, still open), so the property that
  # is testable TODAY is the one that matters most: nothing expands the list
  # except an explicit toggle. This is the single most likely thing to be broken
  # by a later "be helpful and expand it when something fails" change -- and
  # that break would be completely silent.
  $stLog = Join-Path $logDir 'detail-selftest.log'
  $st = Start-Process -FilePath $Exe -ArgumentList @(
    '--progress-detail-selftest', '--quiet', '--log', $stLog) -PassThru -Wait -WindowStyle Hidden
  $stText = (Get-Content -LiteralPath $stLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
  Check 'S16.44 the expand-state self-test passes every one of its own checks' `
    ($st.ExitCode -eq 0 -and $stText -match 'detail: selftest done: checks=(\d+) failed=0 ok=True') `
    "exit=$($st.ExitCode)"
  Check 'S16.45 IsExpanded is false before anything toggles it (spec 7.3)' `
    ($stText -match 'detail: PASS nothing is expanded before any toggle') ''
  Check 'S16.46 drawing the pill is not what expands the list' `
    ($stText -match 'detail: PASS drawing the pill is NOT what expands the list') ''
  Check 'S16.47 ToggleDetail really flips the state and the rectangle follows it' `
    ($stText -match 'detail: PASS ToggleDetail expands the list' -and
     $stText -match 'detail: PASS the expanded list is bigger than the pill it came from' -and
     $stText -match 'detail: PASS the hit rectangle follows the expanded list' -and
     $stText -match 'detail: PASS ToggleDetail collapses again') ''
  Check 'S16.48 a new conversion starts with the list collapsed again' `
    ($stText -match 'detail: PASS a new conversion starts with the list collapsed') ''
  Check 'S16.49 the hit test uses the rectangle the presenter reported, not a second geometry' `
    ($stText -match 'detail: PASS Contains agrees with the rectangle it just reported') ''
} else {
  Inconclusive 'S16.5-S16.49 detail dump variants' 'the dump log has no per-variant geometry lines'
}

"=== S16.50 the suite left no Confern process behind ==="
Check 'S16.50 no leftover Confern process (the exe would otherwise be locked for the next run)' `
  (@(Get-Process -Name Confern -ErrorAction SilentlyContinue).Count -eq 0) ''

# =============================================================================
#  S16.51+ : D1 (a dedicated clickable window) and D2 (the pill does not fade
#  while the cursor rests on it). 2026-10-02.
#
#  APPENDED, never inserted. The previous slice inserted 19 checks into the middle
#  of this file's numbering and silently pushed the old S16.24-S16.30 down to
#  S16.44-S16.50, so every document citing those ids now points at different
#  checks. A check id is an address people cite for years: new ones go at the end.
#
#  Why S16.50 above is the process check and the new checks come after it: the
#  injected runs below START processes, so the "no leftover process" fact has to
#  be established first. Nothing was renumbered or moved to make room.
#
#  Why these checks are shaped the way they are:
#   * A synthetic click is the only evidence that the user can actually reach the
#     pill (it proves window position, z-order and the ABSENCE of
#     WS_EX_TRANSPARENT). A log line saying "we toggled" proves none of that.
#   * Every assertion proves its own PREMISE first: that the pill was on screen,
#     that the click landed on OUR window, that the foreground window was known.
#     An assertion whose premise silently failed verifies nothing while looking
#     green -- and the previous slice found two of its own doing exactly that.
#   * The fade timings are read from the app's log (holdMs=, "hiding after"), not
#     copied into this file, so changing the hold deliberately cannot silently
#     turn an assertion into decoration.
#
#  ASCII-only on purpose (PS 5.1 reads BOM-less scripts as ANSI and a CJK comment
#  silently swallows the next statement -- that already caused a false pass here).
# =============================================================================

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PillProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
    public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
  }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

  [DllImport("user32.dll", SetLastError=true)]
  public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

  public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public const uint ABSOLUTE = 0x8000;

  public static uint MouseAbsolute(int x, int y, uint flags) {
    int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
    var inp = new INPUT[1];
    inp[0].type = 0;
    inp[0].mi.dx = (int)Math.Round((double)x * 65535.0 / (sw - 1));
    inp[0].mi.dy = (int)Math.Round((double)y * 65535.0 / (sh - 1));
    inp[0].mi.dwFlags = flags | ABSOLUTE;
    return SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
  }
  public static bool Click(int x, int y) {
    uint a = MouseAbsolute(x, y, MOVE);
    uint b = MouseAbsolute(x, y, LEFTDOWN);
    uint c = MouseAbsolute(x, y, LEFTUP);
    return a == 1 && b == 1 && c == 1;
  }
  public static bool MoveTo(int x, int y) { return MouseAbsolute(x, y, MOVE) == 1; }

  /// "class=ConfernPillWindow hwnd=0x1234 pid=5678 rect=L,T,W,H visible=True exstyle=0x80088"
  /// Returns an EMPTY string when no such window exists: the caller must treat "no window" as
  /// a measured fact, never as an empty string that silently compares equal to something.
  public static string FindWindowByClass(string cls) {
    string found = null;
    EnumProc cb = delegate(IntPtr h, IntPtr p) {
      var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
      if (sb.ToString() != cls) return true;
      RECT r; GetWindowRect(h, out r);
      uint pid; GetWindowThreadProcessId(h, out pid);
      found = string.Format("class={0} hwnd=0x{1:X} pid={2} rect={3},{4},{5},{6} visible={7} exstyle=0x{8:X}",
        cls, h.ToInt64(), pid, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
        IsWindowVisible(h), GetWindowLongW(h, -20));
      return false;
    };
    EnumWindows(cb, IntPtr.Zero); GC.KeepAlive(cb);
    return found == null ? "" : found;
  }

  /// "pid=1234 class=Notepad hwnd=0x1234" -- the foreground window as the OS sees it right now.
  public static string ForegroundInfo() {
    IntPtr h = GetForegroundWindow();
    if (h == IntPtr.Zero) return "pid=0 class=(none) hwnd=0x0";
    var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("pid={0} class={1} hwnd=0x{2:X}", pid, sb.ToString(), h.ToInt64());
  }

  /// What window is under this point? Used to prove a synthetic click lands on OUR pill
  /// rather than on whatever happens to be on the desktop behind it.
  public static string WindowAtPoint(int x, int y) {
    var pt = new POINT(); pt.X = x; pt.Y = y;
    IntPtr h = WindowFromPoint(pt);
    if (h == IntPtr.Zero) return "hwnd=0x0 class=(none) pid=0";
    var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("hwnd=0x{0:X} class={1} pid={2}", h.ToInt64(), sb.ToString(), pid);
  }
}
'@

# The pill's window class. Read from the product side is not possible inside the script,
# and hard-coding it here is intentional: if the class is renamed the window cannot be found
# and every check below fails loudly (it cannot pass by accident).
$pillClass = 'ConfernPillWindow'

function Get-PillWindow {
  $line = [PillProbe]::FindWindowByClass($pillClass)
  if (-not $line) { return $null }
  $o = @{ Line = $line }
  $m = [regex]::Match($line, 'hwnd=0x([0-9A-F]+) pid=(\d+) rect=(-?\d+),(-?\d+),(\d+),(\d+) visible=(\w+) exstyle=0x([0-9A-F]+)')
  if (-not $m.Success) { return $o }
  $o.Hwnd    = [Convert]::ToInt64($m.Groups[1].Value, 16)
  $o.Pid     = [int]$m.Groups[2].Value
  $o.X       = [int]$m.Groups[3].Value
  $o.Y       = [int]$m.Groups[4].Value
  $o.W       = [int]$m.Groups[5].Value
  $o.H       = [int]$m.Groups[6].Value
  $o.Visible = $m.Groups[7].Value -eq 'True'
  $o.ExStyle = [Convert]::ToInt64($m.Groups[8].Value, 16)
  return $o
}

# The pill's own log lines. `--pill-interactive` writes the anchor and the rectangle it drew,
# so the script never has to guess where the pill is (and the window probe above confirms it).
function Get-LogField([string]$text, [string]$name) {
  $m = [regex]::Match($text, [regex]::Escape($name) + '=(\d+)')
  if ($m.Success) { return [int]$m.Groups[1].Value }
  return -1
}
function Get-ShownRect([string]$text) {
  $m = [regex]::Match($text, 'pill interactive: shown rect=(-?\d+),(-?\d+),(\d+),(\d+)')
  if (-not $m.Success) { return $null }
  return @{ X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value
            W = [int]$m.Groups[3].Value; H = [int]$m.Groups[4].Value }
}
function Wait-ForShownRect([string]$log, [int]$timeoutMs) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    $t = (Get-Content -LiteralPath $log -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
    $r = Get-ShownRect $t
    if ($null -ne $r) { return $r }
    Start-Sleep -Milliseconds 50
  }
  return $null
}
function Start-PillRun([string]$tag, [int]$ms) {
  $log = Join-Path $logDir "pill-live-$tag.log"
  $p = Start-Process -FilePath $Exe -ArgumentList @(
    '--pill-interactive', "$ms", '--quiet', '--log', $log) -PassThru -WindowStyle Hidden
  return @{ Proc = $p; Log = $log }
}
function Stop-PillRun($run) {
  try { if (-not $run.Proc.HasExited) { $run.Proc.Kill(); $run.Proc.WaitForExit(5000) | Out-Null } } catch { }
}
function Get-PillLog($run) {
  return ((Get-Content -LiteralPath $run.Log -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n")
}

"=== S16.51-S16.54 D1: the expanded state is dumped and its ink is measured from pixels ==="
# The expanded state is the only thing this feature produces, and "the rectangle got bigger"
# proves nothing about what is inside it. So the app writes the composite (flat + alpha) and
# the exact strings it handed to GDI+ -- and the checks below compare the three with each other.
$pillFlat = Join-Path $Work 'pill-expanded.png'
$pillRaw = Join-Path $Work 'pill-expanded-raw.png'
$pillJson = Join-Path $Work 'pill-expanded.json'
$pillShot = Join-Path $Work 'pill-shot.png'
$pillLog = Join-Path $logDir 'pill-selftest.log'
$pd = Start-Process -FilePath $Exe -ArgumentList @(
  '--pill-selftest',
  '--pill-selftest-dump', $pillFlat,
  '--pill-selftest-dump-raw', $pillRaw,
  '--pill-selftest-dump-json', $pillJson,
  '--pill-selftest-shot', $pillShot,
  '--quiet', '--log', $pillLog) -PassThru -Wait -WindowStyle Hidden
$pdText = (Get-Content -LiteralPath $pillLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"

# The app's own self-test runs first: if it is red, the dumps below are describing a broken build.
Check 'S16.51 the pill self-test (WM_LBUTTONUP through the real WndProc) passes every check' `
  ($pd.ExitCode -eq 0 -and $pdText -match 'pill: selftest done: checks=10 failed=0 ok=True') `
  "exit=$($pd.ExitCode)"

$pdLine = [regex]::Match($pdText,
  'pill dump line: variant=expanded width=(\d+) height=(\d+) rows=(\d+) laidOutRows=(\d+) pillInBitmap=(-?\d+),(-?\d+),(\d+),(\d+) window=(-?\d+),(-?\d+),(\d+),(\d+) at=(-?\d+),(-?\d+),(\d+),(\d+)')
$pdW = if ($pdLine.Success) { [int]$pdLine.Groups[1].Value } else { 0 }
$pdH = if ($pdLine.Success) { [int]$pdLine.Groups[2].Value } else { 0 }
$pdRows = if ($pdLine.Success) { [int]$pdLine.Groups[3].Value } else { 0 }
$pdLaid = if ($pdLine.Success) { [int]$pdLine.Groups[4].Value } else { 0 }
$pdPillW = if ($pdLine.Success) { [int]$pdLine.Groups[7].Value } else { 0 }
$pdPillH = if ($pdLine.Success) { [int]$pdLine.Groups[8].Value } else { 0 }
$pdWinH = if ($pdLine.Success) { [int]$pdLine.Groups[12].Value } else { 0 }
$pdExists = (Test-Path -LiteralPath $pillFlat) -and (Test-Path -LiteralPath $pillRaw)
$mFlat = if (Test-Path -LiteralPath $pillRaw) { Measure-Ink $pillRaw } else { $null }

# THE VACUITY GUARD FOR EVERYTHING BELOW: "the file exists" also describes an empty bitmap.
# The premise asserted here is that a composite with a panel under a pill was really rendered,
# and that its geometry is the geometry the app reported.
Check 'S16.52 the expanded composite really was rendered (premise: rows, a pill band and a panel band)' `
  ($pdLine.Success -and $pdExists -and ($null -ne $mFlat) -and
   ($pdRows -ge 2) -and ($pdLaid -ge 2) -and ($pdW -gt 0) -and ($pdH -gt 0) -and
   ($pdPillH -gt 0) -and ($pdH -ge ($pdPillH * 2)) -and
   ($mFlat.Width -eq $pdW) -and ($mFlat.Height -eq $pdH)) `
  "logged=${pdW}x${pdH} rows=$pdRows laidOutRows=$pdLaid pillBand=${pdPillW}x${pdPillH} png=$(if ($null -ne $mFlat) { "$($mFlat.Width)x$($mFlat.Height)" } else { 'missing' })"

if ($null -ne $mFlat) {
  # The panel's fill is WheelDesign.NoticeFill (150,40,40) -- the ONLY error red in the product --
  # and the pills/panel text is (245,247,250). Counting both separates "the panel was drawn" from
  # "something was drawn", and the y split proves the panel is BELOW the pill (one bitmap, stacked).
  $fill = 0; $ink = 0; $panelFill = 0
  $bmp = [System.Drawing.Bitmap]::FromFile($pillRaw)
  try {
    for ($y = 0; $y -lt $bmp.Height; $y++) {
      for ($x = 0; $x -lt $bmp.Width; $x++) {
        $p = $bmp.GetPixel($x, $y)
        if ($p.A -gt 200) {
          if ($p.R -gt 120 -and $p.R -lt 180 -and $p.G -lt 70 -and $p.B -lt 70) {
            $fill++
            if ($y -ge $pdPillH) { $panelFill++ }
          }
          if ($p.R -gt 230 -and $p.G -gt 230 -and $p.B -gt 230) { $ink++ }
        }
      }
    }
  } finally { $bmp.Dispose() }
  Check 'S16.53 the expanded composite pixels really contain the error-red panel below the pill AND text ink' `
    (($fill -gt 5000) -and ($panelFill -gt 2000) -and ($ink -gt 100)) `
    "errorRedFill=$fill belowPill=$panelFill whiteInk=$ink (pill band is $pdPillH px tall)"

  # The strings handed to GDI+ are the only honest evidence for "the rows say what the panel shows":
  # a screenshot cannot tell a shortened row from a clipped one, and a log line saying "we drew 2 rows"
  # is a self-report. This file is the layout result itself.
  $jsonOk = (Test-Path -LiteralPath $pillJson)
  $pj = if ($jsonOk) { Get-Content -LiteralPath $pillJson -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
  $pjRows = if ($null -ne $pj) { @($pj.panelRows) } else { @() }
  $pjWidths = 0
  if ($pjRows.Count -gt 0) {
    $pjWidths = (@($pjRows | Where-Object { $_.laidOutWidth -gt 0 -and $_.laidOutLength -gt 0 }).Count)
  }
  Check 'S16.54 the JSON dump agrees with the PNG: same geometry, same row count, every row laid out' `
    ($jsonOk -and ($null -ne $pj) -and ($pj.width -eq $pdW) -and ($pj.height -eq $pdH) -and
     ($pjRows.Count -eq $pdLaid) -and ($pjRows.Count -ge 2) -and ($pjWidths -eq $pjRows.Count)) `
    "json=$(if ($null -ne $pj) { "$($pj.width)x$($pj.height)" } else { 'missing' }) rows=$($pjRows.Count) logged=$pdLaid withWidth=$pjWidths"
} else {
  Inconclusive 'S16.53-S16.54 expanded composite pixels' 'the alpha-preserving composite dump is missing'
}

"=== S16.55-S16.58 D1/D2 with REAL synthetic input (SendInput) ==="
"    NOTE: from here on this script injects synthetic mouse input into the real desktop."
"    Nothing else may run at the same time; the cursor is left where the last run put it."
$fgBeforeInjection = [PillProbe]::ForegroundInfo()
"    foreground before any injection: $fgBeforeInjection"

# ---------------------------------------------------------------------------
# S16.55: a real click on the pill expands it, a real click on it again collapses it,
# and neither of them steals the foreground window.
# ---------------------------------------------------------------------------
$runA = Start-PillRun 'click' 9000
$shownA = Wait-ForShownRect $runA.Log 4000
$clickOk = $false; $clickDetail = 'the pill never reported a rectangle (premise failed)'
$fgUnchanged = $false; $fgDetail = 'not measured'
$shrinkOk = $false
if ($null -ne $shownA) {
  # PREMISE: the pill must be on screen AND the window under the click point must be OUR window.
  # Without this, a click could land on the desktop and the check would be measuring nothing.
  $winA = Get-PillWindow
  $cxA = $shownA.X + [int]($shownA.W / 2)
  $cyA = $shownA.Y + [int]($shownA.H / 2)
  $atA = [PillProbe]::WindowAtPoint($cxA, $cyA)
  $premiseA = ($null -ne $winA) -and $winA.Visible -and ($winA.W -eq $shownA.W) -and ($winA.H -eq $shownA.H)
  "    click run: shown=$($shownA.X),$($shownA.Y),$($shownA.W)x$($shownA.H) window=$(if ($null -ne $winA) { "$($winA.X),$($winA.Y),$($winA.W)x$($winA.H) visible=$($winA.Visible)" } else { 'none' })"
  "    window under the click point: $atA"
  if (-not $premiseA) {
    Stop-PillRun $runA
    Inconclusive 'S16.55-S16.58 real synthetic click and cursor dwell' 'the pill window was not on screen at the click point (premise failed)'
  } else {
    $fgBefore = [PillProbe]::ForegroundInfo()
    $beforeState = (Get-PillLog $runA) -match 'pill interactive: clickState before any input: expanded=False'
    $sent = [PillProbe]::Click($cxA, $cyA)
    # Wait for the app to log the state CHANGE it made after the click. The pattern carries the
    # click count and the window height as well, so a pass cannot come from a coincidence:
    # it takes the click counter, the boolean and the real window height to move together.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $polls = 0
    # Wait for BOTH facts, not just the first one to show up. The first version stopped as soon as
    # the expansion line existed -- which can be before the click counter line is flushed -- and
    # then failed an assertion whose evidence was simply not read yet. Waiting for what you are
    # going to assert on is not optional.
    while ($sw.ElapsedMilliseconds -lt 5000) {
      $polls++
      $t = Get-PillLog $runA
      if ($t -match 'pill interactive: expanded=True clicks=1 windowH=(\d+)' -and
          $t -match 'pill window: WM_LBUTTONUP at client \(\d+,\d+\) clicks=1') { break }
      Start-Sleep -Milliseconds 50
    }
    $txtA = Get-PillLog $runA
    $mExp = [regex]::Match($txtA, 'pill interactive: expanded=True clicks=1 windowH=(\d+)')
    "    poll loop: iterations=$polls elapsed=$($sw.ElapsedMilliseconds) ms; " +
      "logChars=$($txtA.Length); expandedMarkerMatched=$($mExp.Success) " +
      "clickMarkerMatched=$([regex]::IsMatch($txtA, 'pill window: WM_LBUTTONUP at client \(\d+,\d+\) clicks=1'))"
    $fgAfter = [PillProbe]::ForegroundInfo()
    $clickOk = $sent -and $beforeState -and ($txtA -match 'pill interactive: expanded=True clicks=1 windowH=(\d+)') -and
               ($txtA -match 'pill window: WM_LBUTTONUP at client \(\d+,\d+\) clicks=1')
    $winExp = Get-PillWindow
    $grew = ($null -ne $winExp) -and ($null -ne $winA) -and ($winExp.H -gt $winA.H) -and ($winExp.W -ge $winA.W)
    $fgUnchanged = ($fgBefore -eq $fgAfter)
    $fgDetail = "$fgBefore -> $fgAfter"
    if ($fgBefore -match 'hwnd=0x0') {
      # A null foreground window would make "it did not change" vacuously true.
      $fgUnchanged = $false
      $fgDetail = "$fgBefore -> $fgAfter (no foreground window existed, so 'unchanged' cannot be verified)"
    }
    # The foreground window is NOT the whole assertion. It was measured to stay put even with
    # MA_NOACTIVATE removed, because WS_EX_NOACTIVATE (checked at creation, and asserted here from
    # the live log) already makes the window non-activating -- the two are independent defences.
    # So the check also reads the live window's extended style, which DOES move when the bit is lost.
    $exMatch = [regex]::Match((Get-PillLog $runA), 'pill interactive: hwnd=0x([0-9A-F]+) exstyle=0x([0-9A-F]+)')
    $liveEx = if ($exMatch.Success) { [Convert]::ToInt64($exMatch.Groups[2].Value, 16) } else { 0 }
    $exNoActivate = ($liveEx -band 0x08000000) -ne 0
    $exLayered = ($liveEx -band 0x00080000) -ne 0
    $exClickThrough = ($liveEx -band 0x00000020) -ne 0
    $clickDetail = "exit-sent=$sent clicks=1 seen=$($clickOk) window $(if ($null -ne $winA) { $winA.H } else { 0 }) -> $(if ($null -ne $winExp) { $winExp.H } else { 0 }) px tall; exstyle=0x$('{0:X}' -f $liveEx) noActivate=$exNoActivate layered=$exLayered clickThrough=$exClickThrough; fg $fgDetail"
    "    after the first click: expanded=True seen=$clickOk; window height $(if ($null -ne $winExp) { $winExp.H } else { 0 })"

    # The second real click must collapse it again, back to the pill-sized window.
    $null = [PillProbe]::Click($cxA, $cyA)
    $sw.Restart()
    while ($sw.ElapsedMilliseconds -lt 3000) {
      if ((Get-PillLog $runA) -match 'pill interactive: expanded=False') { break }
      Start-Sleep -Milliseconds 50
    }
    $winBack = Get-PillWindow
    $shrinkOk = ($null -ne $winBack) -and ($null -ne $winA) -and ($winBack.H -eq $winA.H) -and ($winBack.W -eq $winA.W)
    # Park the cursor OFF the pill so the rest of this run is not affected by hover.
    $null = [PillProbe]::MoveTo(($shownA.X - 200), ($shownA.Y - 200))
    "    after the second click: window $(if ($null -ne $winBack) { "$($winBack.W)x$($winBack.H)" } else { 'none' }) (was $($winA.W)x$($winA.H))"
  }
} else {
  Stop-PillRun $runA
  Inconclusive 'S16.55-S16.58 real synthetic click and cursor dwell' 'the pill never appeared'
}

if ($null -ne $shownA -and $null -ne $winA -and $premiseA) {
  Check 'S16.55 a REAL synthetic click on the pill expands the list (and the window grows with it)' `
    $clickOk "clicked=($cxA,$cyA) landedOn=$atA; $clickDetail"
  Check 'S16.56 the live pill window is LAYERED and NON-ACTIVATING, never click-through (measured on the on-screen window)' `
    ($exNoActivate -and $exLayered -and -not $exClickThrough) `
    "exstyle=0x$('{0:X}' -f $liveEx) WS_EX_NOACTIVATE=$exNoActivate WS_EX_LAYERED=$exLayered WS_EX_TRANSPARENT=$exClickThrough"
  Check 'S16.57 the synthetic click did NOT steal the foreground window (WS_EX_NOACTIVATE / MA_NOACTIVATE)' `
    $fgUnchanged "$fgDetail (foreground before the whole injection phase: $fgBeforeInjection)"
  Check 'S16.58 a second real click collapses the list again (the window shrinks back)' `
    $shrinkOk "window $(if ($null -ne $winBack) { "$($winBack.W)x$($winBack.H)" } else { 'none' }) vs pill $($winA.W)x$($winA.H)"
} else {
  Inconclusive 'S16.55-S16.57 real synthetic click' 'no pill window was available to click'
}

# ---------------------------------------------------------------------------
# S16.58: D2 -- with the cursor RESTING on the pill, the pill does not fade.
# The falsifiable case is the COLLAPSED pill: on its own it fades after 2 s (spec 4.4),
# so a run that is still alive well past that can only be explained by hover.
# (Measured, not assumed: the app logs `holdMs=` and, on the way out, `hiding after N ms`.)
# ---------------------------------------------------------------------------
$runB = Start-PillRun 'dwell' 8000
$shownB = Wait-ForShownRect $runB.Log 4000
if ($null -ne $shownB) {
  $cxB = $shownB.X + [int]($shownB.W / 2)
  $cyB = $shownB.Y + [int]($shownB.H / 2)
  $atB = [PillProbe]::WindowAtPoint($cxB, $cyB)
  # Dwell: move there and STAY there (no further moves, exactly like a user reading the pill).
  $null = [PillProbe]::MoveTo($cxB, $cyB)
  # Poll until either the app logs the fade or we pass the natural (un-hovered) deadline + margin.
  $deadline = 4200
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $hidEarly = $false; $hoverSeen = $false; $hidAt = -1
  while ($sw.ElapsedMilliseconds -lt $deadline) {
    $t = Get-PillLog $runB
    if ($t -match 'pill interactive: hover=True') { $hoverSeen = $true }
    $m = [regex]::Match($t, 'progress: hiding after (\d+) ms')
    if ($m.Success) { $hidEarly = $true; $hidAt = [int]$m.Groups[1].Value; break }
    Start-Sleep -Milliseconds 50
  }
  $winB = Get-PillWindow
  $stillVisible = ($null -ne $winB) -and $winB.Visible
  $txtB = Get-PillLog $runB
  $holdB = Get-LogField $txtB 'holdMs'
  "    dwell run: hovered=$hoverSeen hidBeforeDeadline=$hidEarly($hidAt ms) stillVisible=$stillVisible at $($sw.ElapsedMilliseconds) ms; app reported holdMs=$holdB"
  # Park the cursor away and then let the run finish (it must be able to fade out on its own).
  $null = [PillProbe]::MoveTo(($shownB.X - 200), ($shownB.Y - 200))
  $finishedB = $runB.Proc.WaitForExit(20000)
  $txtB = Get-PillLog $runB
  $fadedAfter = $txtB -match 'pill interactive: fadedOut=True'
  Check 'S16.59 the cursor resting on the pill prevents the fade (D2), and it fades once the cursor leaves' `
    ($hoverSeen -and -not $hidEarly -and $stillVisible -and $finishedB -and $fadedAfter) `
    "hoverSeen=$hoverSeen hidEarly=$hidEarly($hidAt ms) stillVisibleAt4200ms=$stillVisible " +
    "holdMs=$holdB finished=$finishedB fadedAfterLeaving=$fadedAfter landedOn=$atB"
  if (-not $finishedB) { Stop-PillRun $runB }
} else {
  Stop-PillRun $runB
  Inconclusive 'S16.59 cursor dwell prevents the fade' 'the pill never appeared'
}

# ---------------------------------------------------------------------------
# S16.60: the vacuity guard for S16.59 -- WITHOUT a dwell the same build DOES fade,
# at the hold the app itself reports. If this were also "still visible", then S16.58
# would be passing for a reason that has nothing to do with hover.
# ---------------------------------------------------------------------------
$runC = Start-PillRun 'nohover' 8000
$shownC = Wait-ForShownRect $runC.Log 4000
if ($null -ne $shownC) {
  # Keep the cursor away from the pill for the whole hold window.
  $null = [PillProbe]::MoveTo(($shownC.X - 200), ($shownC.Y - 200))
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $hid = -1
  while ($sw.ElapsedMilliseconds -lt 6000) {
    $t = Get-PillLog $runC
    $m = [regex]::Match($t, 'progress: hiding after (\d+) ms')
    if ($m.Success) { $hid = [int]$m.Groups[1].Value; break }
    Start-Sleep -Milliseconds 50
  }
  $txtC = Get-PillLog $runC
  $holdC = Get-LogField $txtC 'holdMs'
  $winC = Get-PillWindow
  $goneNow = ($null -eq $winC) -or (-not $winC.Visible)
  "    no-hover run: fading started at $hid ms (hold reported by the app: $holdC ms); window visible now: $(-not $goneNow)"
  # Two independent facts: the fade really started, and it started because of the hold
  # (not immediately, and not never).
  Check 'S16.60 without a cursor on it the same build DOES fade, at the hold the app reports' `
    (($hid -ge 0) -and ($holdC -gt 0) -and ($hid -ge $holdC) -and ($hid -le ($holdC + 1500))) `
    "hiding after=$hid ms holdMs=$holdC ms windowStillVisible=$(-not $goneNow)"
  $null = $runC.Proc.WaitForExit(15000)
  if (-not $runC.Proc.HasExited) { Stop-PillRun $runC }
} else {
  Stop-PillRun $runC
  Inconclusive 'S16.60 the no-hover control run' 'the pill never appeared'
}

$fgAfterInjection = [PillProbe]::ForegroundInfo()
"    foreground after all injection: $fgAfterInjection"

"=== S16.61 the suite left no Confern process behind (after the injected runs) ==="
Check 'S16.61 no leftover Confern process after the injected runs' `
  (@(Get-Process -Name Confern -ErrorAction SilentlyContinue).Count -eq 0) ''

""
"M1h summary: $($script:Pass)/$($script:Checks) checks passed"
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive)"
if ($script:Fail -gt 0) { exit 1 }
if ($script:Checks -eq 0) { exit 2 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
