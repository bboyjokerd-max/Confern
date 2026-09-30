param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$t = "$env:TEMP\m1d-orient"
if (Test-Path $t) { Remove-Item $t -Recurse -Force }
New-Item -ItemType Directory -Force -Path $t | Out-Null

$RED = [System.Drawing.Color]::FromArgb(255, 255, 0, 0)
$GREEN = [System.Drawing.Color]::FromArgb(255, 0, 255, 0)
$BLUE = [System.Drawing.Color]::FromArgb(255, 0, 0, 255)
$YELLOW = [System.Drawing.Color]::FromArgb(255, 255, 255, 0)

function New-Fixture([string]$path, [int]$orientation) {
  $bmp = New-Object System.Drawing.Bitmap -ArgumentList @(8, 4)
  for ($y = 0; $y -lt 4; $y++) {
    for ($x = 0; $x -lt 8; $x++) {
      $c = if ($x -lt 4) { if ($y -lt 2) { $RED } else { $BLUE } }
           else { if ($y -lt 2) { $GREEN } else { $YELLOW } }
      $bmp.SetPixel($x, $y, $c)
    }
  }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $bmp.Dispose()

  if ($orientation -le 1) { return }

  $s = [System.IO.File]::OpenRead($path)
  $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create(
      $s, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
      [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
  $meta = New-Object System.Windows.Media.Imaging.BitmapMetadata -ArgumentList @('jpg')
  $meta.SetQuery('/app1/ifd/{ushort=274}', [uint16]$orientation)
  $enc = New-Object System.Windows.Media.Imaging.JpegBitmapEncoder
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame, $null, $meta, $null))
  $tmp = "$path.tmp"
  $fs = [System.IO.File]::Create($tmp)
  $enc.Save($fs); $fs.Dispose(); $s.Dispose()
  Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Name-Area([System.Drawing.Bitmap]$bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $fx = [int]($w * 0.25); $fy = [int]($h * 0.25)
  $tl = $bmp.GetPixel($fx, $fy); $tr = $bmp.GetPixel($w - 1 - $fx, $fy)
  $bl = $bmp.GetPixel($fx, $h - 1 - $fy); $br = $bmp.GetPixel($w - 1 - $fx, $h - 1 - $fy)
  # Nearest of the four quadrant colours. Exact RGB matching does not work here:
  # an 8x4 JPEG uses chroma subsampling, so a 4x2 quadrant bleeds heavily.
  $ref = @(
    [pscustomobject]@{ N = 'A'; R = 255; G = 0;   B = 0 },
    [pscustomobject]@{ N = 'B'; R = 0;   G = 255; B = 0 },
    [pscustomobject]@{ N = 'C'; R = 0;   G = 0;   B = 255 },
    [pscustomobject]@{ N = 'D'; R = 255; G = 255; B = 0 })
  $names = @()
  foreach ($p in @($tl, $tr, $bl, $br)) {
    $best = $null; $bestD = [int]::MaxValue
    foreach ($r in $ref) {
      $d = [Math]::Abs($p.R - $r.R) + [Math]::Abs($p.G - $r.G) + [Math]::Abs($p.B - $r.B)
      if ($d -lt $bestD) { $bestD = $d; $best = $r.N }
    }
    $names += "$best($($p.R),$($p.G),$($p.B))"
  }
  return (($names | ForEach-Object { $_.Substring(0, 1) }) -join '')
}

# expected upright quadrant reading order: TL,TR,BL,BR   (A=red B=green C=blue D=yellow)
$expect = @{
  1 = @('AB', 'CD', 8, 4); 2 = @('BA', 'DC', 8, 4); 3 = @('DC', 'BA', 8, 4); 4 = @('CD', 'AB', 8, 4)
  5 = @('AC', 'BD', 4, 8); 6 = @('CA', 'DB', 4, 8); 7 = @('DB', 'CA', 4, 8); 8 = @('BD', 'AC', 4, 8)
}

$pass = 0; $fail = 0
foreach ($o in 1..8) {
  $jpg = Join-Path $t "o$o.jpg"
  New-Fixture $jpg $o
  $log = Join-Path $t "o$o.log"
  $p = Start-Process -FilePath $Exe -ArgumentList @('--convert', $jpg, '--target', 'PNG', '--quiet', '--log', $log) -PassThru -Wait
  $png = Join-Path $t "o$o.png"
  if (-not (Test-Path $png)) { "EXIF $o : NO OUTPUT (exit $($p.ExitCode))"; $fail++; continue }
  $ob = New-Object System.Drawing.Bitmap -ArgumentList @($png)
  $w = $ob.Width; $h = $ob.Height
  $got = Name-Area $ob
  $ob.Dispose()
  $want = ($expect[$o][0] + $expect[$o][1])
  $wantDims = "$($expect[$o][2])x$($expect[$o][3])"
  $ok = ($got -eq $want) -and ("${w}x${h}" -eq $wantDims)
  if ($ok) { $pass++ } else { $fail++ }
  "EXIF $o : got ${w}x${h} $got   want $wantDims $want   $(if ($ok) { 'OK' } else { 'MISMATCH' })"
}
""
"orientation matrix: $pass/8 correct"
exit $(if ($fail -eq 0) { 0 } else { 1 })
