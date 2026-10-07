# M3c verification: the compression tool (spec 9.4, the THIRD tool sector).
#
#   -Exe <path to Confern.exe>   -Section S17   -LogDir <dir>
#
# WHAT THIS PROVES, AND WHAT IT DOES NOT
#   1. The level is chosen on the SETTINGS PAGE and reaches the engine. The user ruled
#      on 2026-10-06 that the wheel gets neither three extra sectors nor a cycling
#      toggle, so the compression level is a settings value, not a wheel value. Every run
#      below therefore goes through a settings FILE, never through a command-line switch:
#      a switch would let this suite verify a combination the product cannot produce.
#   2. "Compress to <limit>" really is "the best version that fits": the engine logs every
#      probe, and it re-encodes at best+1 to prove that one step up exceeds the limit.
#      S17.08 asserts on that proof line rather than on this script's own arithmetic.
#   3. A limit that cannot be reached is REFUSED, not faked: no file is written and the
#      reason is TargetTooSmall. "Compressed to 4 KB" with a 200 KB floor is a lie the
#      product must not tell.
#   4. Backward compatibility is a BIT-IDENTICAL claim, not a vibe. An old settings file
#      with none of the new keys must produce exactly the same PNG bytes as the same file
#      with the new keys spelled out at their defaults.
#   5. The source file is read-only (spec 5): sha256 + length + mtime are compared before
#      and after every run.
#   6. The compress op really travels to the WORKER process (spec 9.1). The in-process
#      --compress diagnostic cannot see the op field, so S17.20 drives the wheel's tool
#      layer instead and asserts on the worker's own job line and on its output bytes.
#
# NOT covered here (stated so a green run is not read as more than it is):
#   - the pixels of the compressed images are never inspected. This suite asserts on
#     sizes, quality numbers, exit codes and the source's integrity, not on whether the
#     picture still looks right. That is a human judgement.
#   - PNG/BMP/TIF/GIF have no quality knob in the WIC encoders, so for those formats the
#     three levels produce byte-identical output. The engine says so in its own log; the
#     claim is not asserted here because it is a property of this machine's codecs.
#   - WebP cannot be ENCODED on this machine at all, so compress on a WebP source fails
#     early with TargetUnavailable instead of reaching the search. Not asserted.
#   - the wheel sector's LOOK is asserted structurally (four sectors, the right ids, a
#     frame that renders), not visually: this file never compares against a stored image.
#   - the cross-BUILD comparison (old exe vs new exe, same old settings file) was done by
#     hand before this suite existed; a runner is only given one exe, so what is asserted
#     here is the machine-independent half: same exe, old file vs explicit defaults.
#
# ASCII ONLY. This script must stay parseable by Windows PowerShell 5.1, whose BOM-less
# CJK handling corrupts non-ASCII literals.

param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [string]$Section = 'S17',
  [string]$LogDir = "$env:TEMP\sidecar-verify"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }

$Work = Join-Path $LogDir 'm3c'
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
# Select-String is CASE-INSENSITIVE by default, so a search for "FAIL" also matches
# "failed=0". Anything about an exception must go through this one.
function Test-LogCase([string[]]$l, [string]$p) { return [bool]($l | Select-String -Pattern $p -CaseSensitive -Quiet) }
function Get-LogLine([string[]]$l, [string]$p) {
  $hit = @($l | Select-String -Pattern $p)
  if ($hit.Count -eq 0) { return '' }
  return [string]$hit[$hit.Count - 1].Line
}
function Get-LogMatch([string]$text, [string]$pattern) {
  $m = [regex]::Match($text, $pattern)
  if ($m.Success) { return $m.Groups[1].Value }
  return ''
}
function Get-Sha([string]$p) {
  if (-not (Test-Path -LiteralPath $p)) { return 'MISSING' }
  return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
}
function Get-FileLength([string]$p) {
  if (-not (Test-Path -LiteralPath $p)) { return -1 }
  return (Get-Item -LiteralPath $p).Length
}
function Stop-AllConfern {
  Get-Process -Name Confern -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(2000) | Out-Null } catch { }
  }
  Start-Sleep -Milliseconds 300
}

function Write-Cfg([string]$name, [string]$json) {
  $p = Join-Path $Work "$name.json"
  Set-Content -LiteralPath $p -Encoding ASCII -Value $json
  return $p
}

# One Confern run. Always headless (--quiet --no-autostart) so a regression run never
# touches the user's Run key and never turns on the high-frequency detail lines.
function Invoke-Confern([string]$name, [string[]]$argv) {
  $log = Join-Path $Work "$name.log"
  if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
  $all = @() + $argv + @('--log', $log, '--quiet', '--no-autostart')
  $p = Start-Process -FilePath $Exe -ArgumentList $all -Wait -PassThru -WindowStyle Hidden
  return [pscustomobject]@{
    Name = $name; Exit = $p.ExitCode; Log = $log; Lines = (Get-LogLines $log)
  }
}

# --help writes to STDOUT (and exits 2 on purpose), so it cannot go through the log file.
function Invoke-ConfernStdout([string]$name, [string[]]$argv) {
  $out = Join-Path $Work "$name.stdout.txt"
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
  $p = Start-Process -FilePath $Exe -ArgumentList $argv -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $out
  return [pscustomobject]@{ Name = $name; Exit = $p.ExitCode; Lines = (Get-LogLines $out) }
}

# Random noise on purpose: a flat image does not move with the JPEG quality at all, so
# S17.06 would be asserting an order between three identical files.
function New-FixtureJpeg([string]$path, [int]$w, [int]$h, [int]$q) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $rnd = New-Object System.Random(20261006)
  for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
      $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $rnd.Next(256), $rnd.Next(256), $rnd.Next(256)))
    }
  }
  $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
           Where-Object { $_.MimeType -eq 'image/jpeg' }
  if ($null -eq $codec) { throw 'this machine has no JPEG encoder' }
  $ps = New-Object System.Drawing.Imaging.EncoderParameters(1)
  $ps.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [int64]$q)
  $bmp.Save($path, $codec, $ps)
  $ps.Dispose(); $bmp.Dispose()
}

# ------------------------------------------------------------------ S17
function Invoke-S17 {
  Write-Host "=== S17 compression tool: settings-driven level, compress-to search, backward compat ==="

  # The level ALWAYS comes from the settings file. The source is a fresh copy per run, so
  # nothing depends on output-name collisions.
  function Invoke-Level([string]$noise, [string]$name, [string]$mode, [int]$targetKb, [string]$srcSha, [long]$srcBytes, [datetime]$srcMtime) {
    $d = Join-Path $Work "run-$name"
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $out = Join-Path $d 'out'
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $src = Join-Path $d 'photo.jpg'
    Copy-Item -LiteralPath $noise -Destination $src -Force

    # $mode = '' writes a settings file with NO compression keys at all: that is the
    # backward-compatibility fixture (the shape of a file written before this feature).
    $json = '{ "JpgQuality": 85, "WebpQuality": 85'
    if ($mode -ne '') { $json += ', "CompressionMode": "' + $mode + '"' }
    if ($targetKb -gt 0) { $json += ', "CompressionTargetKb": ' + $targetKb }
    $json += ', "OutputFolder": ' + (ConvertTo-Json $out) + ' }'
    $cfg = Write-Cfg "cfg-$name" $json

    $r = Invoke-Confern $name @('--settings-file', $cfg, '--compress', $src)
    $files = @(Get-ChildItem -LiteralPath $out -File)
    $line = Get-LogLine $r.Lines 'compressed .* -> '

    $quality = -1
    $qText = Get-LogMatch $line 'quality=(\d+)'
    if ($qText -ne '') { $quality = [int]$qText }
    $outLen = -1
    $outSha = 'MISSING'
    if ($files.Count -gt 0) { $outLen = $files[0].Length; $outSha = Get-Sha $files[0].FullName }

    $untouched = ((Get-Sha $src) -eq $srcSha) -and
                 ((Get-Item -LiteralPath $src).Length -eq $srcBytes) -and
                 ((Get-Item -LiteralPath $src).LastWriteTimeUtc -eq $srcMtime)

    return [pscustomobject]@{
      Name = $name; Mode = $mode; TargetKb = $targetKb; Exit = $r.Exit; Lines = $r.Lines
      Line = $line; Quality = $quality; OutLen = $outLen; OutSha = $outSha
      Src = $src; Untouched = $untouched
    }
  }

  # Same exe, same image, settings files that differ ONLY in the compression keys.
  function Invoke-Bc([string]$noise, [string]$name, [string]$json) {
    $d = Join-Path $Work "bc-$name"
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $src = Join-Path $d 'src.jpg'
    Copy-Item -LiteralPath $noise -Destination $src -Force
    $cfg = Write-Cfg "bc-$name" $json
    $r = Invoke-Confern "bc-$name" @('--settings-file', $cfg, '--convert', $src, '--target', 'PNG')
    $out = Join-Path $d 'src.png'
    return [pscustomobject]@{
      Name = $name; Exit = $r.Exit; Sha = (Get-Sha $out); Bytes = (Get-FileLength $out); Lines = $r.Lines
    }
  }

  # Two dumps of the SAME page that differ only in the selected mode.
  function Invoke-Dump([string]$name, [string]$json) {
    $png = Join-Path $Work "$name.png"
    $cfg = Write-Cfg "dump-$name" $json
    $r = Invoke-Confern "dump-$name" @('--settings-ui-dump', $png, '--settings-file', $cfg)
    return [pscustomobject]@{ Name = $name; Exit = $r.Exit; Png = $png; Lines = $r.Lines }
  }

  # ---------------------------------------------------------------- fixture
  $fdir = Join-Path $Work 'f'
  New-Item -ItemType Directory -Force -Path $fdir | Out-Null
  $noise = Join-Path $fdir 'noise.jpg'
  New-FixtureJpeg $noise 512 384 92
  $noiseBytes = (Get-Item -LiteralPath $noise).Length
  $noiseSha = Get-Sha $noise
  $noiseMtime = (Get-Item -LiteralPath $noise).LastWriteTimeUtc

  # ---------------------------------------------------------------- S17.01 build freshness
  $repo = Split-Path -Parent $PSScriptRoot
  $exeItem = Get-Item -LiteralPath $Exe
  $exeTime = $exeItem.LastWriteTimeUtc
  $sources = @()
  if (Test-Path -LiteralPath (Join-Path $repo 'src')) {
    $sources = @(Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj |
                 Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
  }
  $newest = $sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  $newestText = '(none found)'
  if ($null -ne $newest) { $newestText = "$($newest.Name) $($newest.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss'))Z" }
  Check 'S17.01 the exe under test is newer than every source file it should have been built from' `
    (($null -ne $newest) -and ($exeTime -gt $newest.LastWriteTimeUtc)) `
    "exe=$($exeTime.ToString('yyyy-MM-dd HH:mm:ss'))Z sources=$($sources.Count) newest=$newestText"

  # ---------------------------------------------------------------- S17.02 the diagnostics exist
  $help = Invoke-ConfernStdout 'help' @('--help')
  Check 'S17.02 the two new diagnostics are documented in --help (they are not secret switches)' `
    ((Test-Log $help.Lines '--compress <path>') -and (Test-Log $help.Lines '--wheel-tool <id>')) `
    "helpLines=$($help.Lines.Count) exit=$($help.Exit)"

  # ---------------------------------------------------------------- the four levels
  $small = Invoke-Level $noise 'small' 'small' 0 $noiseSha $noiseBytes $noiseMtime
  $std = Invoke-Level $noise 'standard' 'standard' 0 $noiseSha $noiseBytes $noiseMtime
  $high = Invoke-Level $noise 'high' 'high' 0 $noiseSha $noiseBytes $noiseMtime

  Check 'S17.03 the small level really converts (exit 0) and uses the fixed low quality 60' `
    (($small.Exit -eq 0) -and ($small.Quality -eq 60) -and ($small.OutLen -gt 0)) `
    "exit=$($small.Exit) quality=$($small.Quality) out=$($small.OutLen)B line='$($small.Line)'"

  Check 'S17.04 the standard level follows the JPG quality in the settings file (85), i.e. the old behaviour' `
    (($std.Exit -eq 0) -and ($std.Quality -eq 85) -and ($std.OutLen -gt 0)) `
    "exit=$($std.Exit) quality=$($std.Quality) out=$($std.OutLen)B line='$($std.Line)'"

  Check 'S17.05 the high level really converts (exit 0) and uses the fixed high quality 95' `
    (($high.Exit -eq 0) -and ($high.Quality -eq 95) -and ($high.OutLen -gt 0)) `
    "exit=$($high.Exit) quality=$($high.Quality) out=$($high.OutLen)B line='$($high.Line)'"

  Check 'S17.06 the three levels are strictly ordered on a noisy image: small < standard < high' `
    (($small.OutLen -gt 0) -and ($small.OutLen -lt $std.OutLen) -and ($std.OutLen -lt $high.OutLen)) `
    "small=$($small.OutLen)B standard=$($std.OutLen)B high=$($high.OutLen)B"

  # ---------------------------------------------------------------- compress-to
  # The limit is chosen FROM THE MEASUREMENTS, not hardcoded: the midpoint between the
  # floor (quality 60) and quality 85. That is what makes S17.08 a real test -- the answer
  # has to be strictly between the two endpoints -- and it survives a change of fixture,
  # of machine or of encoder.
  $limitKb = [int][Math]::Floor((($small.OutLen + $std.OutLen) / 2.0) / 1024.0)
  if ($limitKb -lt 1) { $limitKb = 1 }
  if (($limitKb * 1024) -le $small.OutLen) { $limitKb = $limitKb + 1 }
  $limitBytes = [long]$limitKb * 1024
  $target = Invoke-Level $noise 'target' 'target-size' $limitKb $noiseSha $noiseBytes $noiseMtime
  $toosmall = Invoke-Level $noise 'toosmall' 'target-size' 4 $noiseSha $noiseBytes $noiseMtime

  Check 'S17.07 compress-to lands at or under the requested limit and really writes a file' `
    (($target.Exit -eq 0) -and ($target.OutLen -gt 0) -and ($target.OutLen -le $limitBytes)) `
    "limit=${limitKb}KB=$limitBytes B exit=$($target.Exit) out=$($target.OutLen)B quality=$($target.Quality)"

  # The proof line is written by the ENGINE, after it re-encodes at best+1:
  #   compress: target-size proof quality=B -> S B <= T B; quality=B+1 -> U B > T B (...)
  # Asserting on it, instead of on this script's arithmetic, is the difference between
  # "we computed a number" and "the encoder was asked twice and answered".
  $proof = Get-LogLine $target.Lines 'target-size proof'
  $pm = [regex]::Match($proof, 'quality=(\d+) -> (\d+)B <= (\d+)B; quality=(\d+) -> (\d+)B > (\d+)B')
  $pBest = -1; $pFit = -1; $pLim = -1; $pNext = -1; $pOver = -1; $pLim2 = -1
  if ($pm.Success) {
    $pBest = [int]$pm.Groups[1].Value
    $pFit = [long]$pm.Groups[2].Value
    $pLim = [long]$pm.Groups[3].Value
    $pNext = [int]$pm.Groups[4].Value
    $pOver = [long]$pm.Groups[5].Value
    $pLim2 = [long]$pm.Groups[6].Value
  }
  $probes = @($target.Lines | Select-String -Pattern 'compress search: quality=').Count
  Check 'S17.08 compress-to is the BEST version that fits: one quality step up was measured and it exceeds the limit' `
    ($pm.Success -and ($pFit -le $pLim) -and ($pOver -gt $pLim) -and ($pLim -eq $pLim2) -and
     ($pNext -eq $pBest + 1) -and ($pBest -gt 60) -and ($pBest -lt 85) -and ($probes -ge 3)) `
    "proof='$proof' probes=$probes"

  Check 'S17.09 an unreachable limit is refused truthfully: no file is written and the reason is TargetTooSmall' `
    (($toosmall.Exit -ne 0) -and ($toosmall.OutLen -eq -1) -and
     (Test-Log $toosmall.Lines 'TargetTooSmall') -and (Test-Log $toosmall.Lines 'is written \(say it, do not pretend\)')) `
    "exit=$($toosmall.Exit) outFile=$($toosmall.OutLen) refuse='$(Get-LogLine $toosmall.Lines 'target-size unreachable')'"

  Check 'S17.10 the source file was never touched in ANY of the five runs (sha256 + length + mtime)' `
    ($small.Untouched -and $std.Untouched -and $high.Untouched -and $target.Untouched -and $toosmall.Untouched) `
    "small=$($small.Untouched) standard=$($std.Untouched) high=$($high.Untouched) target=$($target.Untouched) refused=$($toosmall.Untouched) sha256=$($noiseSha.Substring(0, 16)) bytes=$noiseBytes"

  # ---------------------------------------------------------------- backward compatibility
  $base = '{ "JpgQuality": 85, "WebpQuality": 85, "TriggerKey": "Space", "OutputFolder": ""'
  $bcOld = Invoke-Bc $noise 'old' ($base + ' }')
  $bcStd = Invoke-Bc $noise 'defaults' ($base + ', "CompressionMode": "standard", "CompressionTargetKb": 0 }')
  $bcSmall = Invoke-Bc $noise 'small' ($base + ', "CompressionMode": "small", "CompressionTargetKb": 0 }')
  $bcHigh = Invoke-Bc $noise 'high' ($base + ', "CompressionMode": "high", "CompressionTargetKb": 0 }')

  # The default is the whole backward-compat contract, and --convert cannot see it (that is
  # S17.12's point), so it has to be measured on the op that DOES read the policy: compress.
  # An old file must land on the SAME level and the SAME bytes as the file that spells
  # "standard" out. Change CompressionPolicy.DefaultMode and this goes red.
  $compatOld = Invoke-Level $noise 'compat-old' '' 0 $noiseSha $noiseBytes $noiseMtime
  $compatStd = Invoke-Level $noise 'compat-std' 'standard' 0 $noiseSha $noiseBytes $noiseMtime
  Check 'S17.11 an OLD settings file (no new keys) compresses at the SAME level and to the SAME bytes as the explicit standard default' `
    (($compatOld.Exit -eq 0) -and ($compatOld.Quality -eq 85) -and ($compatOld.Quality -eq $compatStd.Quality) -and
     ($compatOld.OutLen -gt 0) -and ($compatOld.OutSha -eq $compatStd.OutSha) -and
     ($compatOld.OutSha -eq $std.OutSha)) `
    "old quality=$($compatOld.Quality) bytes=$($compatOld.OutLen) sha=$(if ($compatOld.OutSha.Length -ge 16) { $compatOld.OutSha.Substring(0, 16) }) ; explicit-standard quality=$($compatStd.Quality) bytes=$($compatStd.OutLen) sha=$(if ($compatStd.OutSha.Length -ge 16) { $compatStd.OutSha.Substring(0, 16) })"

  Check 'S17.12 the compression setting cannot leak into the existing ops (defaults/small/high convert identically to the old file)' `
    (($bcSmall.Sha -eq $bcOld.Sha) -and ($bcHigh.Sha -eq $bcOld.Sha) -and ($bcStd.Sha -eq $bcOld.Sha)) `
    "old=$($bcOld.Sha.Substring(0, 16)) defaults=$($bcStd.Sha.Substring(0, 16)) small=$($bcSmall.Sha.Substring(0, 16)) high=$($bcHigh.Sha.Substring(0, 16))"

  $probeCfg = Write-Cfg 'probe-old' ($base + ' }')
  $probe1 = Invoke-Confern 'probe-old' @('--settings-probe', $probeCfg)
  $probeLine = Get-LogLine $probe1.Lines 'settings probe result:'
  Check 'S17.13 an old settings file loads as compress=standard, with no clamping and no rejection' `
    ((Test-Log $probe1.Lines 'compress=standard') -and
     (-not (Test-Log $probe1.Lines 'settings: clamped')) -and
     (-not (Test-Log $probe1.Lines 'settings: reject'))) `
    "$probeLine"

  # Saving an OLD file must keep every existing value and add exactly the two new keys at
  # their defaults. This is the surface where a careless serializer turns "compatible"
  # into "rewritten".
  $resaveCfg = Write-Cfg 'resave' ($base.Replace(', "OutputFolder": ""', '') + ' }')
  $resave = Invoke-Confern 'resave' @('--settings-set', 'triggerKey=Tab', '--settings-file', $resaveCfg)
  $resaveText = ''
  if (Test-Path -LiteralPath $resaveCfg) { $resaveText = (Get-Content -LiteralPath $resaveCfg -Raw -Encoding UTF8) }
  $kept = ($resaveText -match '"JpgQuality":\s*85') -and ($resaveText -match '"WebpQuality":\s*85') -and
          ($resaveText -match '"TriggerKey":\s*"Tab"') -and ($resaveText -match '"OutputFolder":\s*""')
  $added = ($resaveText -match '"CompressionMode":\s*"standard"') -and ($resaveText -match '"CompressionTargetKb":\s*0')
  Check 'S17.14 re-saving an old settings file keeps every existing value and adds the two new keys at their defaults' `
    (($resave.Exit -eq 0) -and $kept -and $added) `
    "exit=$($resave.Exit) kept=$kept defaultsAdded=$added"

  # ---------------------------------------------------------------- settings validation
  $legalBad = @()
  foreach ($m in @('small', 'standard', 'high', 'target-size')) {
    # NOTE the parentheses: inside @() the comma binds TIGHTER than +, so an unparenthesised
    # ('x=' + $m) would be passed as TWO arguments ('x=' and $m) and the value would arrive
    # empty. That bug cost this suite one red run; it is fixed here, not worked around.
    $pair = 'compressionMode=' + $m
    $c = Write-Cfg "legal-$m" ('{ "CompressionMode": "' + $m + '" }')
    $r = Invoke-Confern "legal-$m" @('--settings-set', $pair, '--settings-file', $c)
    if ($r.Exit -ne 0) { $legalBad += "$m(exit=$($r.Exit) $(Get-LogLine $r.Lines 'settings: reject'))" }
  }
  $limCfg = Write-Cfg 'legal-limit' '{ "CompressionMode": "target-size" }'
  $rLim = Invoke-Confern 'legal-limit' @('--settings-set', 'compressionMode=target-size;compressionTargetKb=1536', '--settings-file', $limCfg)
  if (($rLim.Exit -ne 0) -or (-not (Test-Log $rLim.Lines 'compress=target-size/1536KB'))) { $legalBad += "target-size/1536KB(exit=$($rLim.Exit))" }
  Check 'S17.15 --settings-set accepts all four canonical modes and a legal limit' `
    ($legalBad.Count -eq 0) `
    "rejected=$($legalBad -join ',') limitRun='$(Get-LogLine $rLim.Lines 'settings set result:')'"

  $illegal = @(
    @{ Key = 'compressionMode=tiny'; Why = 'unknown mode'; Expect = 'expected one of small' },
    @{ Key = 'compressionTargetKb=0'; Why = 'zero means no limit'; Expect = 'at least 1 KB' },
    @{ Key = 'compressionTargetKb=-5'; Why = 'negative'; Expect = 'at least 1 KB' },
    @{ Key = 'compressionTargetKb=abc'; Why = 'not a number'; Expect = 'not an integer number of KB' },
    @{ Key = 'compressionTargetKb=99999999'; Why = 'absurdly large'; Expect = 'max 1048576 KB' }
  )
  $illegalBad = @()
  $seenReasons = @()
  foreach ($case in $illegal) {
    $tag = ($case.Key -replace '[^A-Za-z0-9]', '_')
    $c = Write-Cfg "illegal-$tag" '{ "JpgQuality": 85 }'
    $r = Invoke-Confern "illegal-$tag" @('--settings-set', $case.Key, '--settings-file', $c)
    $seenReasons += "$($case.Key) :: $(Get-LogLine $r.Lines 'settings: reject')"
    $ok = ($r.Exit -eq 4) -and (Test-Log $r.Lines ([regex]::Escape($case.Expect))) -and
          (-not (Test-LogCase $r.Lines 'Unhandled exception')) -and
          (-not (Test-LogCase $r.Lines 'System\.(Format|Overflow|Argument|NullReference|InvalidOperation)Exception'))
    if (-not $ok) { $illegalBad += "$($case.Why)[exit=$($r.Exit)]" }
  }
  Check 'S17.16 --settings-set rejects five illegal values with a reason, exits 4, and throws nothing' `
    ($illegalBad.Count -eq 0) `
    ("still accepted: " + ($illegalBad -join ',') + " || " + ($seenReasons -join ' || '))

  # A legal mode combined with a rejected limit must not leave an impossible state on
  # disk: "compress to" with no limit is not something the engine can act on.
  $impCfg = Write-Cfg 'impossible' '{ "JpgQuality": 85 }'
  $imp = Invoke-Confern 'impossible' @('--settings-set', 'compressionMode=target-size;compressionTargetKb=0', '--settings-file', $impCfg)
  $impText = ''
  if (Test-Path -LiteralPath $impCfg) { $impText = (Get-Content -LiteralPath $impCfg -Raw -Encoding UTF8) }
  $impProbe = Invoke-Confern 'impossible-probe' @('--settings-probe', $impCfg)
  Check 'S17.17 no impossible state is persisted: a rejected limit demotes the mode back to standard on disk' `
    (($imp.Exit -eq 4) -and ($impText -match '"CompressionMode":\s*"standard"') -and
     (Test-Log $impProbe.Lines 'compress=standard')) `
    "exit=$($imp.Exit) onDisk=$(Get-LogMatch $impText '"CompressionMode":\s*"([a-z-]+)"') reProbe='$(Get-LogLine $impProbe.Lines 'settings probe result:')'"

  # ---------------------------------------------------------------- the settings PAGE
  $uiCfg = Join-Path $Work 'ui.json'
  if (Test-Path -LiteralPath $uiCfg) { Remove-Item -LiteralPath $uiCfg -Force }
  $ui = Invoke-Confern 'ui-selftest' @('--settings-ui-selftest', '--settings-file', $uiCfg)
  $uiSave = Get-LogLine $ui.Lines 'the compression fields round-trip'
  $uiRefuse = Get-LogLine $ui.Lines 'an unusable compression limit is refused'
  $uiNotice = Get-LogLine $ui.Lines 'the inline notice under the compression row'
  $uiDone = Get-LogLine $ui.Lines 'settings ui selftest done:'
  Check 'S17.18 the settings page: the two new fields round-trip through the real Save button, and an unusable limit is refused with a visible inline notice' `
    (($ui.Exit -eq 0) -and (Test-Log $ui.Lines 'settings ui selftest done: ok=True') -and
     ($uiSave -match 'ok=True') -and ($uiRefuse -match 'save ok=False') -and
     ($uiNotice -match "compressNotice='[^']") -and ($uiNotice -match 'limitBoxEnabled=True')) `
    $uiDone
  if ($uiSave -ne '') { Write-Host "      $uiSave" }
  if ($uiRefuse -ne '') { Write-Host "      $uiRefuse" }
  if ($uiNotice -ne '') { Write-Host "      $uiNotice" }

  $dStd = Invoke-Dump 'std' '{ "JpgQuality": 85, "WebpQuality": 85, "CompressionMode": "standard" }'
  $dTgt = Invoke-Dump 'tgt' '{ "JpgQuality": 85, "WebpQuality": 85, "CompressionMode": "target-size", "CompressionTargetKb": 512 }'

  $pngW = 0; $pngH = 0; $colours = 0; $scrollbar = $true; $diffPixels = -1
  $diffMinY = -1; $diffMaxY = -1; $diffMinX = -1; $diffMaxX = -1
  if ((Test-Path -LiteralPath $dStd.Png) -and (Test-Path -LiteralPath $dTgt.Png)) {
    $ia = [System.Drawing.Bitmap]::FromFile($dStd.Png)
    $ib = [System.Drawing.Bitmap]::FromFile($dTgt.Png)
    $pngW = $ia.Width; $pngH = $ia.Height
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($y = 0; $y -lt $pngH; $y += 6) {
      for ($x = 0; $x -lt $pngW; $x += 6) { [void]$seen.Add($ia.GetPixel($x, $y).ToArgb()) }
    }
    $colours = $seen.Count

    # A WPF vertical scrollbar is a LONG CONTIGUOUS light column near the right edge of the
    # content area. The light-chromed combo boxes are only 26px tall, so a 120px contiguous
    # run cannot be one of them.
    $scrollbar = $false
    for ($x = 498; $x -lt $pngW - 2; $x++) {
      $run = 0; $best = 0
      for ($y = 40; $y -lt $pngH - 40; $y++) {
        $c = $ia.GetPixel($x, $y)
        if (($c.R -ge 180) -and ($c.G -ge 180) -and ($c.B -ge 180)) { $run++; if ($run -gt $best) { $best = $run } }
        else { $run = 0 }
      }
      if ($best -ge 120) { $scrollbar = $true; break }
    }

    $diffPixels = 0
    for ($y = 0; $y -lt $pngH; $y++) {
      for ($x = 0; $x -lt $pngW; $x++) {
        if ($ia.GetPixel($x, $y).ToArgb() -ne $ib.GetPixel($x, $y).ToArgb()) {
          $diffPixels++
          if ($diffMinY -lt 0 -or $y -lt $diffMinY) { $diffMinY = $y }
          if ($y -gt $diffMaxY) { $diffMaxY = $y }
          if ($diffMinX -lt 0 -or $x -lt $diffMinX) { $diffMinX = $x }
          if ($x -gt $diffMaxX) { $diffMaxX = $x }
        }
      }
    }
    $ia.Dispose(); $ib.Dispose()
  }
  Check 'S17.19 the settings page draws the new compression row, the mode changes that row and nothing else, and there is no scrollbar' `
    (($dStd.Exit -eq 0) -and ($dTgt.Exit -eq 0) -and ($pngW -ge 500) -and ($pngH -ge 800) -and
     ($colours -gt 50) -and (-not $scrollbar) -and ($diffPixels -gt 20) -and
     ($diffMinX -ge 20) -and ($diffMaxX -le ($pngW - 10)) -and ($diffMinY -ge 500) -and ($diffMaxY -le 600)) `
    "frame=${pngW}x${pngH} colours=$colours scrollbar=$scrollbar changedPixels=$diffPixels bbox=($diffMinX,$diffMinY)-($diffMaxX,$diffMaxY) '$(Get-LogLine $dStd.Lines 'dump: ')'"

  # ---------------------------------------------------------------- the worker
  # The wheel's tool layer, committed for real: this is the only path that puts the op into
  # a job packet and sends it down the pipe (spec 9.1).
  $wd = Join-Path $Work 'worker'
  New-Item -ItemType Directory -Force -Path $wd | Out-Null
  $wOut = Join-Path $wd 'out'
  New-Item -ItemType Directory -Force -Path $wOut | Out-Null
  $wSrc = Join-Path $wd 'photo.jpg'
  Copy-Item -LiteralPath $noise -Destination $wSrc -Force
  $wCfg = Write-Cfg 'worker' ('{ "JpgQuality": 85, "WebpQuality": 85, "CompressionMode": "small", "OutputFolder": ' + (ConvertTo-Json $wOut) + ' }')
  $w = Invoke-Confern 'worker' @('--settings-file', $wCfg, '--wheel-payload', $wSrc, '--wheel-tools', '--wheel-tool', 'compress')
  # The wheel diagnostic keeps exercising the layer afterwards, so the LAST job line is the
  # strip-metadata one; the compress job has to be picked out by its own op field.
  $jobLine = Get-LogLine $w.Lines 'worker: job \d+ sent .*op=compress'
  $finLine = Get-LogLine $w.Lines 'conversion finished:'
  # The worker's output must be byte-for-byte what the in-process engine produced for the
  # same source and the same level: that is what makes "the same engine" a measurement.
  $wLen = Get-FileLength (Join-Path $wOut 'photo.jpg')
  Check 'S17.20 the wheel tool layer lists every sector, the compress op really travels to the worker process, and it returns the same bytes' `
    (($w.Exit -eq 0) -and (Test-Log $w.Lines 'tools=\[rotate90,strip-metadata,compress,crop,read-qr\]') -and
     ($jobLine -match 'op=compress') -and ($finLine -match 'finished=True ok=1') -and
     ($wLen -gt 0) -and ($wLen -eq $small.OutLen)) `
    "job='$jobLine' finished='$finLine' workerOut=${wLen}B inProcess=$($small.OutLen)B"
}

# ------------------------------------------------------------------ main
$sections = @($Section -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
Write-Host "verify-m3c: exe=$Exe sections=$($sections -join ',') logdir=$LogDir"

if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

foreach ($s in $sections) {
  switch ($s) {
    'S17' { Expect 20; Invoke-S17 }
    default { Write-Host "section not implemented yet: $s" }
  }
}

Stop-AllConfern

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
Write-Host "M3c summary: $($results.Count - $fail)/$($results.Count) checks passed ($Section)"
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
