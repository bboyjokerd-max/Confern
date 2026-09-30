param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Work = "$env:TEMP\m1d-verify"
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

$r = Run-Convert @("$Work\nope-does-not-exist.jpg") 'PNG' 'missing'
Check 'S12.7a missing source -> SourceMissing' ($r.Text -match 'error=SourceMissing') ''
Check 'S12.7b missing source -> readable message' ($r.Text.Contains($MSG_MISSING)) $MSG_MISSING

$pdf = Join-Path $Work 'doc.pdf'
Set-Content -LiteralPath $pdf -Value '%PDF-1.4 not an image' -Encoding ASCII
$r = Run-Convert @($pdf) 'PNG' 'unsupported'
Check 'S12.8a pdf -> refused with a message' (($r.Text -match 'error=Unsupported') -or ($r.Text -match 'error=DecodeFailed')) ''
Check 'S12.8b pdf -> readable message' ($r.Text.Contains($MSG_UNSUP)) $MSG_UNSUP

"=== S12.9-S12.10 the two-process path (spec 9.1) ==="
# offered formats must be exactly the spec-declared AND measured-encodable set
$codecLog = Join-Path $logDir 'codecs.log'
$p = Start-Process -FilePath $Exe -ArgumentList @('--codecs', '--quiet', '--log', $codecLog) -PassThru -Wait -WindowStyle Hidden
$codecText = (Get-Content -LiteralPath $codecLog -Encoding UTF8) -join "`n"
$offeredLine = ($codecText -split "`n" | Where-Object { $_ -match 'offered=\[' } | Select-Object -First 1)
if ($offeredLine) {
  $offered = ([regex]::Match($offeredLine, 'offered=\[([^\]]*)\]')).Groups[1].Value -split ','
  $sorted = ($offered | Sort-Object) -join ','
  Check 'S12.9 offered outputs = declared n encodable' ($sorted -eq 'BMP,JPG,PNG,TIF') "offered=$sorted"
  Check 'S12.9b GIF never offered (capable but not declared)' (-not ($offered -contains 'GIF')) ''
  Check 'S12.9c WEBP never offered (declared but not encodable here)' (-not ($offered -contains 'WEBP')) ''
} else {
  Inconclusive 'S12.9 offered outputs' 'no offered=[] line in the codec log'
}

# the whole point of the worker split: it must never time out
$workerLog = Join-Path $env:LOCALAPPDATA 'Sidecar\worker.log'
$before = if (Test-Path -LiteralPath $workerLog) { (Get-Item -LiteralPath $workerLog).Length } else { 0 }
$payloadLog = Join-Path $logDir 'payload.log'
$p = Start-Process -FilePath $Exe -ArgumentList @('--wheel-payload', $srcJpg, '--quiet', '--log', $payloadLog) -PassThru -Wait -WindowStyle Hidden
$payloadText = (Get-Content -LiteralPath $payloadLog -Encoding UTF8) -join "`n"
Check 'S12.10a payload path converts through the worker' ($payloadText -match 'conversion finished: finished=True ok=1 failed=0') ''
Check 'S12.10b progress is ONE window updated in place' ($payloadText -match 'progress instrument: updates=(\d+) frames=(\d+)') ''
if ($payloadText -match 'progress instrument: updates=(\d+) frames=(\d+) visible=(True|False)') {
  Check 'S12.10c frames == updates (same window, no line spam)' ($Matches[1] -eq $Matches[2]) "updates=$($Matches[1]) frames=$($Matches[2])"
}
Check 'S12.10d progress faded out on its own' ($payloadText -match 'progress faded out on its own: True') ''
Check 'S12.10e no worker timeout (regression: pipe deadlock)' (-not ($payloadText -match 'timed out')) ''
Check 'S12.10f worker reported ready' ((Get-Content -LiteralPath $workerLog -Encoding UTF8 | Select-String -Pattern 'worker: ready protocolVersion=1') -ne $null) ''
Check 'S12.10g no leftover worker process' (@(Get-Process -Name Sidecar -ErrorAction SilentlyContinue).Count -eq 0) ''

# the most natural user action: select a format, then RELEASE the mouse. It needs a real
# gesture, so this asserts the wiring through the very same Released event (spec 4.3).
Check 'S12.10h mouse release commits and converts' ($payloadText -match 'release commit finished: finished=True ok=1 failed=0') ''
$releaseLine = ($payloadText -split "`n" | Where-Object { $_ -match 'release-path: selected index=' } | Select-Object -First 1)
if ($releaseLine) {
  $target = ([regex]::Match($releaseLine, 'target=([A-Z]+)')).Groups[1].Value
  $expectedExt = '.' + $target.ToLower()
  $found = @(Get-ChildItem -LiteralPath (Split-Path -Parent $srcJpg) -File -Filter "*$expectedExt" |
             Where-Object { $_.Name -like 'art*' })
  Check 'S12.10i the released drop produced its own file' ($found.Count -ge 1) "target=$target files=$($found.Count)"
} else {
  Inconclusive 'S12.10i the released drop produced its own file' 'no release-path line'
}

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

"=== result ==="
"checks=$($script:Checks) pass=$($script:Pass) fail=$($script:Fail) inconclusive=$($script:Inconclusive)"
if ($script:Checks -eq 0) { "NO CHECKS RAN"; exit 2 }
if ($script:Fail -gt 0) { exit 1 }
if ($script:Inconclusive -gt 0) { exit 3 }
exit 0
