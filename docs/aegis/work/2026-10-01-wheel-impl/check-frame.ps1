# Wheel frame checker (direction D, 2026-10-01).
# Pure ASCII on purpose: Windows PowerShell 5.1 parses BOM-less non-ASCII .ps1 files as ANSI.
#
# Checks the rendered frame instead of trusting the description:
#   * the alpha contract (glass body alpha 255, everything outside a pure layer)
#   * the veil is really applied (the light frame's interior must stay dark)
#   * amber selection stroke / rim hairline / keycap / caption / preview / labels
#
# Usage: powershell -ExecutionPolicy Bypass -File check-frame.ps1 -Dir <folder> [-Glow 1|0]
param(
  [Parameter(Mandatory = $true)][string]$Dir,
  # 1 = the glow layer is drawn (default). Pass 0 when the build has WheelDesign.GlowEnabled
  # switched off: then the annulus outside the glass must contain the neutral black shadow
  # only, so any chroma there would mean a glow layer leaked through.
  [int]$Glow = 1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$W = 304; $H = 349
$CX = 152.0; $CY = 152.0
$R = 110.0; $RI = 47.0
$SEL = 1; $COUNT = 4
$step = 360.0 / $COUNT
$sweep = $step - 2.0                                   # 4 sectors -> 2 deg gap
$selStart = -90.0 + $SEL * $step + ($step - $sweep) / 2.0
$selEnd = $selStart + $sweep

$script:fail = 0
$script:pass = 0
function Check([string]$text, [bool]$ok, [string]$detail = '') {
  if ($ok) { $script:pass++ } else { $script:fail++ }
  $tag = if ($ok) { 'PASS' } else { 'FAIL' }
  $line = "{0}  {1}" -f $tag, $text
  if ($detail) { $line += "  [$detail]" }
  Write-Host $line
}

function Load([string]$name) {
  $p = Join-Path $Dir $name
  if (-not (Test-Path -LiteralPath $p)) { throw "missing frame: $p" }
  return [System.Drawing.Bitmap]::FromFile($p)
}

function Hex([System.Drawing.Color]$c) { return ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B) }
function Luma([System.Drawing.Color]$c) { return 0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B }

$rawDark  = Load '21-raw-dark.png'
$rawLight = Load '21-raw-light.png'
$flatDark = Load '20-frame-dark.png'
$flatLight = Load '20-frame-light.png'

Write-Host "=== 1. frame size ==="
Check "raw frame is ${W}x${H}" (($rawDark.Width -eq $W) -and ($rawDark.Height -eq $H)) `
      "$($rawDark.Width)x$($rawDark.Height)"
Check "flat frame is ${W}x${H}" (($flatDark.Width -eq $W) -and ($flatDark.Height -eq $H)) `
      "$($flatDark.Width)x$($flatDark.Height)"

Write-Host ""
Write-Host "=== 2. alpha contract ==="
$notOpaque = 0; $sampleA = 0
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $dx = $x - $CX; $dy = $y - $CY
    if (($dx * $dx + $dy * $dy) -le (106.0 * 106.0)) {
      $sampleA++
      if ($rawDark.GetPixel($x, $y).A -ne 255) { $notOpaque++ }
    }
  }
}
Check "glass body fully opaque (alpha 255) inside r<=106" ($notOpaque -eq 0) `
      "$sampleA px, $notOpaque not opaque"

$bulgeBad = 0; $bulgeN = 0
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $dx = $x - $CX; $dy = $y - $CY
    $d2 = $dx * $dx + $dy * $dy
    if (($d2 -gt 106.0 * 106.0) -and ($d2 -le 111.0 * 111.0)) {
      $deg = [Math]::Atan2($dy, $dx) * 180.0 / [Math]::PI
      if (($deg -ge ($selStart + 4)) -and ($deg -le ($selEnd - 4))) {
        $bulgeN++
        if ($rawDark.GetPixel($x, $y).A -ne 255) { $bulgeBad++ }
      }
    }
  }
}
Check "selected-sector bulge opaque up to r=111" ($bulgeBad -eq 0) `
      "$bulgeN px, $bulgeBad not opaque"

# Outside the glass+glow the frame must be pure transparency. The bottom strip (keycap,
# caption, preview) lives below the wheel and is excluded, as is the cursor ghost card.
$outsideBad = 0; $outsideN = 0
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $dx = $x - $CX; $dy = $y - $CY
    if (($dx * $dx + $dy * $dy) -le (160.0 * 160.0)) { continue }
    if ($y -ge 265) { continue }                                        # bottom strip
    if (($x -ge 20) -and ($x -le 74) -and ($y -ge 20) -and ($y -le 74)) { continue }  # ghost card
    $outsideN++
    if ($rawDark.GetPixel($x, $y).A -ne 0) { $outsideBad++ }
  }
}
Check "outside r>160 is fully transparent (pure layer)" ($outsideBad -eq 0) `
      "$outsideN px, $outsideBad not transparent"

Write-Host ""
Write-Host "=== 3. glow is a pure colour layer ==="
# A pure colour layer accumulates one RGB with a varying alpha, so along a ring of constant
# radius the RGB must be constant no matter what wallpaper is underneath. The synthetic
# wallpaper has a diagonal edge plus a 14 px grid, so a baked-in background would show up here.
$worstRing = 0; $worstA = 0; $ringReport = ''
# Group the annulus pixels by their exact alpha and compare RGB inside each group.
# Alpha is a function of the radius alone, so one alpha value == one radius: RGB must be
# constant across the angles of that group. If the wallpaper had been baked into the layer's
# RGB (the alpha red line), the spread would follow the wallpaper (~+-50) instead of ~0.
# Low-alpha pixels are skipped: GetPixel un-premultiplies, so alpha < 40 reads back as noise.
$buckets = @{}
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $dx = $x - $CX; $dy = $y - $CY
    $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if (($d -lt 115) -or ($d -gt 150)) { continue }
    # Angular window -85..-8 deg: pure glow only (ghost card is up-left, bottom strip downwards).
    $deg = [Math]::Atan2($dy, $dx) * 180.0 / [Math]::PI
    if (($deg -lt -85) -or ($deg -gt -8)) { continue }
    $c = $rawDark.GetPixel($x, $y)
    if ($c.A -lt 40) { continue }
    if (-not $buckets.ContainsKey($c.A)) {
      $buckets[$c.A] = [pscustomobject]@{ N = 0; MinR = 999; MaxR = -1; MinG = 999; MaxG = -1; MinB = 999; MaxB = -1 }
    }
    $bs = $buckets[$c.A]
    $bs.N++
    # Premultiplied values: at a fixed alpha they are exactly the layer's colour times a
    # constant, so quantisation is +-1 LSB. Un-premultiplied RGB has a +-255/a error instead,
    # which is why comparing it at low alpha produced a fake "spread" of 9.
    $pr = [int][Math]::Round($c.R * $c.A / 255.0)
    $pg = [int][Math]::Round($c.G * $c.A / 255.0)
    $pb = [int][Math]::Round($c.B * $c.A / 255.0)
    if ($pr -lt $bs.MinR) { $bs.MinR = $pr }; if ($pr -gt $bs.MaxR) { $bs.MaxR = $pr }
    if ($pg -lt $bs.MinG) { $bs.MinG = $pg }; if ($pg -gt $bs.MaxG) { $bs.MaxG = $pg }
    if ($pb -lt $bs.MinB) { $bs.MinB = $pb }; if ($pb -gt $bs.MaxB) { $bs.MaxB = $pb }
  }
}
$glowPx = 0; $aMin = 999; $aMax = -1; $worstBucketN = 0
foreach ($kv in $buckets.GetEnumerator()) {
  $bs = $kv.Value
  if ($bs.N -lt 8) { continue }
  $glowPx += $bs.N
  if ($kv.Key -lt $aMin) { $aMin = $kv.Key }
  if ($kv.Key -gt $aMax) { $aMax = $kv.Key }
  $sp = [Math]::Max([Math]::Max($bs.MaxR - $bs.MinR, $bs.MaxG - $bs.MinG), $bs.MaxB - $bs.MinB)
  if ($sp -gt $worstRing) { $worstRing = $sp; $worstA = $kv.Key; $worstBucketN = $bs.N }
  if ($kv.Key % 20 -eq 0) { $ringReport += ("a{0}:R{1}-{2} " -f $kv.Key, $bs.MinR, $bs.MaxR) }
}
if ($Glow -eq 1) {
Check "glow is a pure layer (premultiplied RGB constant within one alpha = one radius)" `
      ($glowPx -gt 200 -and $worstRing -le 4) `
      "worst alpha bucket a$worstA spread=$worstRing over $worstBucketN px; alpha range $aMin-$aMax, $glowPx px; $ringReport"

function RingMeanAlpha([System.Drawing.Bitmap]$bmp, [double]$radius) {
  $sum = 0.0; $n = 0
  for ($i = 0; $i -lt 720; $i++) {
    $ang = $i * [Math]::PI / 360.0
    $x = [int][Math]::Round($CX + [Math]::Cos($ang) * $radius)
    $y = [int][Math]::Round($CY + [Math]::Sin($ang) * $radius)
    if (($x -lt 0) -or ($y -lt 0) -or ($x -ge $bmp.Width) -or ($y -ge $bmp.Height)) { continue }
    $sum += $bmp.GetPixel($x, $y).A; $n++
  }
  if ($n -eq 0) { return 0 }
  return $sum / $n
}
$a118 = RingMeanAlpha $rawDark 118
$a130 = RingMeanAlpha $rawDark 130
$a145 = RingMeanAlpha $rawDark 145
Check "glow alpha falls off with radius" (($a118 -gt $a130) -and ($a130 -gt $a145)) `
      ("r118={0:F1} r130={1:F1} r145={2:F1}" -f $a118, $a130, $a145)
} else {
# Glow switched off: the same annulus may still hold the shadow, but the shadow is neutral
# black, so |G-R| must stay at zero. Low alpha is skipped for the usual un-premultiply reason.
$maxChroma = 0; $chromaPx = 0
for ($y = 0; $y -lt $H; $y++) {
  for ($x = 0; $x -lt $W; $x++) {
    $dx = $x - $CX; $dy = $y - $CY
    $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if (($d -lt 115) -or ($d -gt 150)) { continue }
    $deg = [Math]::Atan2($dy, $dx) * 180.0 / [Math]::PI
    if (($deg -lt -85) -or ($deg -gt -8)) { continue }
    $c = $rawDark.GetPixel($x, $y)
    if ($c.A -lt 40) { continue }
    $ch = [Math]::Abs($c.G - $c.R)
    if ($ch -gt $maxChroma) { $maxChroma = $ch }
    if ($ch -gt 3) { $chromaPx++ }
  }
}
Check "glow is off: annulus carries the neutral shadow only (no chroma)" `
      ($chromaPx -eq 0) "max |G-R| = $maxChroma, $chromaPx px above 3"
}

Write-Host ""
Write-Host "=== 4. veil is really applied ==="
# veil = rgba(8,11,15,.80) => glass = 0.2 * wallpaper + 0.8 * veil.
# Sample r=65 along each sector's mid-angle (inside the sector ring, clear of the label
# ring at r=84 and of the centre capsule) and compare the two backdrops.
#   light synthetic wallpaper mean luma 219:
#     with veil    -> 0.2*(150,180,205) + 0.8*(0.2*wallpaper + veil) ~= (73,82,90), luma ~81
#     veil cancelled -> 0.2*(150,180,205) + 0.8*wallpaper ~= (222,231,239), luma ~230
# So an absolute ceiling is decisive here: no parameter fitting, the answer is 81 or 230.
function SectorInterior([System.Drawing.Bitmap]$bmp) {
  $sumR = 0.0; $sumG = 0.0; $sumB = 0.0; $n = 0
  for ($i = 0; $i -lt $COUNT; $i++) {
    $m = (-90.0 + ($i + 0.5) * $step) * [Math]::PI / 180.0
    foreach ($rr in 60, 65, 70) {
      $x = [int][Math]::Round($CX + [Math]::Cos($m) * $rr)
      $y = [int][Math]::Round($CY + [Math]::Sin($m) * $rr)
      $c = $bmp.GetPixel($x, $y)
      $sumR += $c.R; $sumG += $c.G; $sumB += $c.B; $n++
    }
  }
  $r = $sumR / $n; $g = $sumG / $n; $b = $sumB / $n
  return [pscustomobject]@{
    R = $r; G = $g; B = $b
    Luma = 0.2126 * $r + 0.7152 * $g + 0.0722 * $b
    MaxChannel = [Math]::Max($r, [Math]::Max($g, $b))
  }
}
$inDark = SectorInterior $flatDark
$inLight = SectorInterior $flatLight
Check "veil applied on the dark backdrop (glass stays dark)" ($inDark.MaxChannel -lt 115) `
      ("rgb=({0:F0},{1:F0},{2:F0}) luma={3:F1} (veil cancelled would be ~193)" -f `
       $inDark.R, $inDark.G, $inDark.B, $inDark.Luma)
Check "veil applied: light backdrop glass stays dark (no cancellation)" ($inLight.MaxChannel -lt 150) `
      ("rgb=({0:F0},{1:F0},{2:F0}) luma={3:F1} (cancelled veil would be ~239)" -f `
       $inLight.R, $inLight.G, $inLight.B, $inLight.Luma)
# The decisive, assumption-free form: the two synthetic wallpapers differ by ~186 luma, and an
# 80 % veil compresses that to 0.2 * 186 ~= 37 inside the glass. Measuring ~37 means the veil is
# doing its job; measuring ~186 would mean the wallpaper reaches the glass untouched.
$delta = [Math]::Abs($inLight.Luma - $inDark.Luma)
Check "glass compresses the wallpaper difference to ~20 %" ($delta -lt 80) `
      ("|luma(light) - luma(dark)| = {0:F1} (expect ~37 with the veil, ~186 without)" -f $delta)

Write-Host ""
Write-Host "=== 5. amber stroke, rim, labels ==="
$mid = ($selStart + $selEnd) / 2.0
$rad = $mid * [Math]::PI / 180.0
$amber = @()
for ($r = 100; $r -le 120; $r++) {
  $x = [int][Math]::Round($CX + [Math]::Cos($rad) * $r)
  $y = [int][Math]::Round($CY + [Math]::Sin($rad) * $r)
  $c = $flatDark.GetPixel($x, $y)
  if (($c.R -gt 200) -and ($c.G -gt 140) -and ($c.G -lt 235) -and ($c.B -lt 150)) {
    $amber += "r$r=$(Hex $c)"
  }
}
Check "amber #FFC24D stroke on the selected sector edge" ($amber.Count -ge 1) ($amber -join ' ')

$unselMid = $selStart - ($step - $sweep) / 2.0 - $step / 2.0
$rad2 = $unselMid * [Math]::PI / 180.0
$rimHits = @()
for ($r = 104; $r -le 113; $r++) {
  $x = [int][Math]::Round($CX + [Math]::Cos($rad2) * $r)
  $y = [int][Math]::Round($CY + [Math]::Sin($rad2) * $r)
  $c = $flatDark.GetPixel($x, $y)
  if (($c.G -gt 120) -and ($c.B -gt 120)) { $rimHits += "r$r=$(Hex $c)" }
}
Check "mint rim hairline visible at the wheel edge" ($rimHits.Count -ge 1) ($rimHits -join ' ')

function RegionStats([System.Drawing.Bitmap]$bmp, [int]$x0, [int]$y0, [int]$x1, [int]$y1) {
  $sum = 0.0; $n = 0; $mn = 999.0; $mx = -1.0; $bright = 0
  for ($y = $y0; $y -le $y1; $y++) {
    for ($x = $x0; $x -le $x1; $x++) {
      $l = Luma $bmp.GetPixel($x, $y)
      $sum += $l; $n++
      if ($l -lt $mn) { $mn = $l }
      if ($l -gt $mx) { $mx = $l }
      if ($l -gt 200) { $bright++ }
    }
  }
  return [pscustomobject]@{ Mean = $sum / $n; Min = $mn; Max = $mx; Bright = $bright; N = $n }
}

$labMax = @()
for ($i = 0; $i -lt $COUNT; $i++) {
  $m = (-90.0 + ($i + 0.5) * $step) * [Math]::PI / 180.0
  $lr = $R * 0.76
  $x = [int][Math]::Round($CX + [Math]::Cos($m) * $lr)
  $y = [int][Math]::Round($CY + [Math]::Sin($m) * $lr)
  $st = RegionStats $flatDark ($x - 24) ($y - 9) ($x + 24) ($y + 9)
  $labMax += [pscustomobject]@{ Sector = $i; Max = $st.Max }
}
$drawn = ($labMax | Where-Object { $_.Max -gt 110 }).Count
$selLab = ($labMax | Where-Object { $_.Sector -eq $SEL }).Max
Check "all $COUNT labels drawn and the selected one is brightest" `
      (($drawn -eq $COUNT) -and ($selLab -gt 200) -and ($selLab -eq ($labMax | Measure-Object -Property Max -Maximum).Maximum)) `
      (($labMax | ForEach-Object { "s$($_.Sector)=$([int]$_.Max)" }) -join ' ')

Write-Host ""
Write-Host "=== 6. bottom stack ==="
$key = RegionStats $flatDark 60 268 244 297
Check "keycap pill below the wheel" (($key.Max -gt 230) -and ($key.Bright -gt 150)) `
      ("mean={0:F0} max={1:F0} bright={2}/{3}" -f $key.Mean, $key.Max, $key.Bright, $key.N)

$cap = RegionStats $flatDark 60 302 244 318
Check "caption line present (dark outline + light glyphs)" (($cap.Min -lt 90) -and ($cap.Max -gt 200)) `
      ("min={0:F0} max={1:F0}" -f $cap.Min, $cap.Max)

$prev = RegionStats $flatDark 60 321 244 343
Check "preview pill present at the bottom" (($prev.Min -lt 60) -and ($prev.Max -gt 180)) `
      ("min={0:F0} max={1:F0}" -f $prev.Min, $prev.Max)

Write-Host ""
Write-Host ("frame check: {0}/{1} passed, failed={2}" -f $script:pass, ($script:pass + $script:fail), $script:fail)
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
