# M1c verification: format knowledge and codec capability probing.
#
#   -Exe <path to Sidecar.exe>   -Section S9   -LogDir <dir>
#
# WHAT THIS PROVES, AND WHAT IT DOES NOT
#   1. The codec layer answers "what can this machine really do" by MEASURING it:
#      every built-in encoder is exercised (encode + decode round-trip) and the
#      probe is shown to be able to REPORT FAILURE (a missing path must come back
#      Unreadable). A probe that can only say "yes" is worse than no probe at all.
#   2. Decode failures map to DIFFERENT user messages, which is what spec 7.1
#      requires: corrupt data is not the same as an unsupported format, and a
#      renamed text file must not be reported as "install the HEIF extension".
#   3. The sector list is the MEASURED capability set, not a constant. The wheel
#      is cross-checked against the --codecs output, so hardcoding a promise the
#      machine cannot keep fails here.
#
# NOT covered here (see docs/aegis/work/2026-10-01-m1c-formats/90-evidence.md):
#   - the "needs the HEIF extension" branch cannot be produced on a machine that
#     HAS the extension, so only the brand sniffing that feeds it is exercised;
#   - WebP encoding needs libwebp, which is not bundled yet.
#
# ASCII ONLY. This script must stay parseable by Windows PowerShell 5.1, whose
# BOM-less CJK handling corrupts non-ASCII literals.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'S9',
  [string]$LogDir = "$env:TEMP\sidecar-verify",
  [int]$DemoX = 900,
  [int]$DemoY = 400
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

$results = New-Object System.Collections.ArrayList
$inconclusive = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok) {
  [void]$results.Add([pscustomobject]@{ Name = $name; Pass = $ok })
  if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" }
}
function Inconclusive([string]$reason) {
  [void]$inconclusive.Add($reason); Write-Host "INCONCLUSIVE  $reason"
}
function Get-LogLines([string]$p) { if (Test-Path -LiteralPath $p) { return @(Get-Content -LiteralPath $p -Encoding UTF8) } else { return @() } }
function Test-Log([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -Quiet) }
# Select-String is CASE-INSENSITIVE by default, so "FAIL" also matches "failed=0" in the
# app's own summary line. Any check about the presence of a FAIL line must use this one.
function Test-LogCase([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -CaseSensitive -Quiet) }
function Count-Log([string[]]$l, [string]$p) { return @($l | Select-String -Pattern $p -CaseSensitive).Count }

function Stop-AllSidecar {
  Get-Process -Name Sidecar -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(2000) | Out-Null } catch { }
  }
  Start-Sleep -Milliseconds 300
}

# The decode-probe line for one fixture, or "" when the app never reported it.
function Get-ProbeLine([string[]]$lines, [string]$file) {
  $pattern = 'decode probe: status=\S+ format=\S+ file=' + [regex]::Escape($file) + ' ::'
  foreach ($l in $lines) { if ($l -match $pattern) { return $l } }
  return ''
}

function Get-OfferedOutputsCsv([string[]]$lines) {
  $m = [regex]::Match(($lines -join "`n"), 'offered=\[([^\]]*)\]')
  if (-not $m.Success) { return $null }
  return $m.Groups[1].Value
}

function Get-ShownTargetsCsv([string[]]$lines) {
  foreach ($l in $lines) {
    $m = [regex]::Match($l, 'targets=\[([^\]]*)\]')
    if ($m.Success) { return $m.Groups[1].Value }
  }
  return $null
}

function Remove-FromCsv([string]$csv, [string]$item) {
  if ($null -eq $csv) { return $null }
  return ((@($csv -split ',' | Where-Object { $_ -ne '' -and $_ -ne $item })) -join ',')
}

# ------------------------------------------------------------------ fixtures
# Real files, built here, so this runner never depends on whatever happens to be
# in the user's folders. The truncated copy is the point: it must come back as
# CORRUPT, not as "unsupported format" (spec 7.1 has different messages for them).
function New-Fixtures {
  Add-Type -AssemblyName System.Drawing
  $png = Join-Path $LogDir 'm1c-fixture.png'
  $bmp = New-Object System.Drawing.Bitmap 8, 8
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 32, 128, 224))
  $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()

  $truncated = Join-Path $LogDir 'm1c-fixture-truncated.png'
  $bytes = [System.IO.File]::ReadAllBytes($png)
  $take = [Math]::Min(40, $bytes.Length)
  $cut = New-Object byte[] $take
  [Array]::Copy($bytes, $cut, $take)
  [System.IO.File]::WriteAllBytes($truncated, $cut)

  # A text file wearing a .heic extension: must NOT be reported as "needs the HEIF
  # extension" -- it is not a HEIC at all. This is the false positive the brand
  # sniffing in CodecRegistry exists to prevent.
  $textHeic = Join-Path $LogDir 'm1c-fixture-text.heic'
  Set-Content -LiteralPath $textHeic -Value 'this is not an image at all' -Encoding ASCII

  # A valid HEIF brand box with no image payload: a real HEIC container header.
  # Either outcome is correct depending on whether this machine has the decoder
  # (Corrupt here, NeedsExtension elsewhere) -- but the brand MUST be recognised.
  $brandHeic = Join-Path $LogDir 'm1c-fixture-brand.heic'
  $head = [byte[]](0, 0, 0, 0x18) + [System.Text.Encoding]::ASCII.GetBytes('ftypheic') +
          [byte[]](0, 0, 0, 0) + [System.Text.Encoding]::ASCII.GetBytes('mif1heic')
  [System.IO.File]::WriteAllBytes($brandHeic, $head)

  return [pscustomobject]@{ Png = $png; Truncated = $truncated; TextHeic = $textHeic; BrandHeic = $brandHeic }
}

# ------------------------------------------------------------------ S9
function Invoke-S9 {
  Write-Host ""
  Write-Host "=== S9 codec capability probing, decode mapping and sector list (non-invasive) ==="

  $fx = New-Fixtures
  Stop-AllSidecar

  $log = Join-Path $LogDir 'm1c-s9-codecs.log'
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $proc = Start-Process -FilePath $Exe -ArgumentList @(
    '--codecs', '--quiet', '--log', $log,
    '--codec-probe', $fx.Png,
    '--codec-probe', $fx.Truncated,
    '--codec-probe', $fx.TextHeic,
    '--codec-probe', $fx.BrandHeic) -PassThru -Wait
  $lines = Get-LogLines $log

  Check "S9.1 codec probe exits 0" ($proc.ExitCode -eq 0)
  Check "S9.2 codec probe has no FAIL line" (-not (Test-LogCase $lines 'FAIL'))

  # The five built-in WIC encoders are exercised for real: encode a bitmap, then
  # decode the bytes we just produced. On any Windows these must work; a failure
  # here is a finding about the machine, not about the instrument.
  Check "S9.3 all five built-in encoders round-trip (encode+decode)" `
    ((Count-Log $lines 'PASS  codec (PNG|JPG|BMP|TIF|GIF) encode\+decode round-trip') -eq 5)

  # Three invariants, each of which can fail:
  #   offered subset of the declared MVP matrix  (extra = promising what the spec never declared)
  #   offered subset of what was measured        (extra = promising what the machine cannot do)
  #   every declared output is offered or explicitly reported unavailable (nothing vanishes)
  Check "S9.4a offered outputs stay inside the declared MVP output matrix" `
    (Test-LogCase $lines 'PASS  offered \[[^\]]*\] is a subset of the declared MVP matrix')
  Check "S9.4b offered outputs stay inside the measured capability set" `
    (Test-LogCase $lines 'PASS  offered \[[^\]]*\] is a subset of what was measured')
  Check "S9.4c every declared output is accounted for (offered or unavailable)" `
    (Test-LogCase $lines 'PASS  every declared output is either offered or reported unavailable')
  Check "S9.4d capable-but-undeclared outputs (e.g. GIF encode) stay out of the wheel" `
    (Test-LogCase $lines 'PASS  capable-but-not-declared outputs stay out of the wheel')

  # Instrument honesty: the probe must be able to report failure.
  Check "S9.5 probe reports a missing file as Unreadable (it can fail)" `
    (Test-LogCase $lines 'PASS  decode probe reports a missing file as Unreadable')

  # No false promise: WEBP/HEIC are declared per-file, and neither claims encode.
  Check "S9.6 WEBP is per-file decode and NOT offered as an output" `
    (Test-Log $lines 'codec WEBP: decode=perfile encode=no')
  Check "S9.7 HEIC is per-file decode and NOT offered as an output" `
    (Test-Log $lines 'codec HEIC: decode=perfile encode=no')

  # Real file: decodes.
  $pngLine = Get-ProbeLine $lines $fx.Png
  if ($pngLine -eq '') { Inconclusive "no decode probe line for the PNG fixture" }
  else { Check "S9.8 a real PNG decodes (status=Ok)" ($pngLine -match 'status=Ok format=PNG') }

  # Truncated file: CORRUPT, which is a different user message than "unsupported".
  $truncLine = Get-ProbeLine $lines $fx.Truncated
  if ($truncLine -eq '') { Inconclusive "no decode probe line for the truncated fixture" }
  else {
    Check "S9.9 truncated PNG is Corrupt, not Unsupported" ($truncLine -match 'status=Corrupt')
    if ($truncLine -notmatch 'status=Corrupt') { Write-Host "      measured: $truncLine" }
  }

  # A text file named .heic must NOT be answered with "install the HEIF extension".
  $textLine = Get-ProbeLine $lines $fx.TextHeic
  if ($textLine -eq '') { Inconclusive "no decode probe line for the text-.heic fixture" }
  else {
    Check "S9.10 text renamed .heic is Unsupported (not 'needs extension')" `
      (($textLine -match 'status=Unsupported') -and ($textLine -notmatch 'heifBrand='))
    if ($textLine -match 'status=NeedsExtension') { Write-Host "      measured: $textLine" }
  }

  # A real HEIF container header must be RECOGNISED as HEIF, whichever branch the
  # machine's capability puts it in.
  $brandLine = Get-ProbeLine $lines $fx.BrandHeic
  if ($brandLine -eq '') { Inconclusive "no decode probe line for the HEIF-brand fixture" }
  else {
    Check "S9.11 HEIF brand box is recognised as HEIF (heifBrand=heic)" ($brandLine -match 'heifBrand=heic')
    Check "S9.12 HEIF brand box maps to Corrupt or NeedsExtension" `
      ($brandLine -match 'status=(Corrupt|NeedsExtension)')
    if ($brandLine -notmatch 'heifBrand=heic') { Write-Host "      measured: $brandLine" }
  }

  $measured = Get-OfferedOutputsCsv $lines
  if ($null -eq $measured) { Inconclusive "no offered-output set parsed from --codecs" }
  else { Write-Host "  (offered outputs on this machine: [$measured])" }

  # Same rule as S7, from the other side: the wheel DEMO must also show the
  # measured set minus its source, never a hardcoded list.
  Stop-AllSidecar
  $dump = Join-Path $LogDir 'm1c-s9-frame.png'
  if (Test-Path -LiteralPath $dump) { Remove-Item -LiteralPath $dump -Force }
  $demoLog = Join-Path $LogDir 'm1c-s9-demo.log'
  if (Test-Path -LiteralPath $demoLog) { Remove-Item -LiteralPath $demoLog -Force }
  $null = Start-Process -FilePath $Exe -ArgumentList @(
    '--wheel-demo', "$DemoX", "$DemoY", '--wheel-source', 'JPG', '--wheel-dump', $dump,
    '--quiet', '--log', $demoLog) -PassThru -Wait
  $demoLines = Get-LogLines $demoLog
  $demoTargets = Get-ShownTargetsCsv $demoLines
  if ($null -eq $measured) { Inconclusive "demo sectors vs offered set (probe unavailable)" }
  elseif ($null -eq $demoTargets) { Check "S9.14 wheel demo sectors equal offered minus source" $false }
  else {
    Check "S9.14 wheel demo sectors equal offered minus source" `
      ($demoTargets -eq (Remove-FromCsv $measured 'JPG'))
  }
  Check "S9.15 wheel demo did not promise the source format as an output" `
    (-not (Test-Log $demoLines 'targets=\[[^\]]*\bJPG\b'))

  # The catalog cost only exists on the path where the app really builds it, so it is
  # measured THERE (the first cut of this check looked in the --codecs log, where the
  # line is never written -- a check that could never fail).
  $m = [regex]::Match(($demoLines -join "`n"), 'codec catalog ready in ([0-9.]+) ms')
  if (-not $m.Success) { Inconclusive "codec catalog timing line not found in the wheel path" }
  else {
    $ms = [double]$m.Groups[1].Value
    Check "S9.13 codec catalog cost is bounded (was $ms ms, bound 500 ms)" ($ms -lt 500)
  }

  # The probe cost must not leak onto the first-show path (spec 9.3 budget is 100 ms).
  $m2 = [regex]::Match(($demoLines -join "`n"), 'in ([0-9.]+) ms \(budget 100 ms\)')
  if (-not $m2.Success) { Inconclusive "wheel demo show latency line not found" }
  else {
    Check "S9.17 wheel demo first show < 100 ms (was $($m2.Groups[1].Value) ms)" `
      ([double]$m2.Groups[1].Value -lt 100)
  }

  Stop-AllSidecar
  Check "S9.16 no Sidecar process is left behind" `
    (@(Get-Process -Name Sidecar -ErrorAction SilentlyContinue).Count -eq 0)
}

# ------------------------------------------------------------------ main
$sections = @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
Write-Host "verify-m1c: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

foreach ($s in $sections) {
  switch ($s) {
    'S9' { Invoke-S9 }
    default { Write-Host "section not implemented yet: $s" }
  }
}

Stop-AllSidecar

$fail = @($results | Where-Object { -not $_.Pass }).Count
if ($results.Count -eq 0) { Write-Host "FATAL: no checks ran - refusing to report success"; exit 2 }
Write-Host ""
Write-Host "M1c summary: $($results.Count - $fail)/$($results.Count) checks passed ($Section)"
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
exit 0
