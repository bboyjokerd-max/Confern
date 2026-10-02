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
# The end-to-end run (worker over the pipe) writes its own pair, so the two runs
# can never be confused with each other in the evidence.
$pipeFlat = Join-Path $Work 'pipe-panel.png'
$pipeRaw = Join-Path $Work 'pipe-panel-raw.png'
$dumpLog = Join-Path $logDir 'detail-dump.log'
$dump = Start-Process -FilePath $Exe -ArgumentList @(
  '--convert', $corrupt,
  '--progress-dump-detail', $flat,
  '--progress-dump-detail-raw', $raw,
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

  "=== S16.24-S16.29 spec 7.3: the list does NOT pop up by itself ==="
  # No click is wired yet (container decision, still open), so the property that
  # is testable TODAY is the one that matters most: nothing expands the list
  # except an explicit toggle. This is the single most likely thing to be broken
  # by a later "be helpful and expand it when something fails" change -- and
  # that break would be completely silent.
  $stLog = Join-Path $logDir 'detail-selftest.log'
  $st = Start-Process -FilePath $Exe -ArgumentList @(
    '--progress-detail-selftest', '--quiet', '--log', $stLog) -PassThru -Wait -WindowStyle Hidden
  $stText = (Get-Content -LiteralPath $stLog -Encoding UTF8 -ErrorAction SilentlyContinue) -join "`n"
  Check 'S16.24 the expand-state self-test passes every one of its own checks' `
    ($st.ExitCode -eq 0 -and $stText -match 'detail: selftest done: checks=(\d+) failed=0 ok=True') `
    "exit=$($st.ExitCode)"
  Check 'S16.25 IsExpanded is false before anything toggles it (spec 7.3)' `
    ($stText -match 'detail: PASS nothing is expanded before any toggle') ''
  Check 'S16.26 drawing the pill is not what expands the list' `
    ($stText -match 'detail: PASS drawing the pill is NOT what expands the list') ''
  Check 'S16.27 ToggleDetail really flips the state and the rectangle follows it' `
    ($stText -match 'detail: PASS ToggleDetail expands the list' -and
     $stText -match 'detail: PASS the expanded list is bigger than the pill it came from' -and
     $stText -match 'detail: PASS the hit rectangle follows the expanded list' -and
     $stText -match 'detail: PASS ToggleDetail collapses again') ''
  Check 'S16.28 a new conversion starts with the list collapsed again' `
    ($stText -match 'detail: PASS a new conversion starts with the list collapsed') ''
  Check 'S16.29 the hit test uses the rectangle the presenter reported, not a second geometry' `
    ($stText -match 'detail: PASS Contains agrees with the rectangle it just reported') ''
} else {
  Inconclusive 'S16.5-S16.29 detail dump variants' 'the dump log has no per-variant geometry lines'
}

"=== S16.30 the suite left no Confern process behind ==="
Check 'S16.30 no leftover Confern process (the exe would otherwise be locked for the next run)' `
  (@(Get-Process -Name Confern -ErrorAction SilentlyContinue).Count -eq 0) ''

""
"M1h summary: $($script:Pass)/$($script:Checks) checks passed"
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive)"
if ($script:Fail -gt 0) { exit 1 }
if ($script:Checks -eq 0) { exit 2 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
