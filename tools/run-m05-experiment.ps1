<#
  run-m05-experiment.ps1

  Runs one M0.5 experiment: start the M05 overlay in --force-overlay mode,
  drive a synthetic drag with DragSim, stop M05, print the evidence tail.

  ASCII-ONLY ON PURPOSE (PowerShell 5.1 reads BOM-less UTF-8 .ps1 as ANSI).
  All paths arrive as parameters.

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run-m05-experiment.ps1 `
      -ProjectRoot "C:\...\tangerine-win" -TargetX 960 -TargetY 540
#>
param(
  [Parameter(Mandatory = $true)][string]$ProjectRoot,
  [int]$TargetX = 960,
  [int]$TargetY = 540,
  [int]$StartX = -1,
  [int]$StartY = -1,
  [int]$WarmupMs = 3000
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$m05 = Join-Path $ProjectRoot 'src\M05\bin\Release\net8.0-windows\M05.exe'
$sim = Join-Path $ProjectRoot 'tools\dragsim\bin\Release\net8.0-windows\DragSim.exe'
$log = Join-Path $env:TEMP 'm05\m05.log'

foreach ($p in @($m05, $sim)) {
  if (-not (Test-Path -LiteralPath $p)) { throw "Not built: $p" }
}

# 乾淨的樣本檔
$sampleDir = Join-Path $env:TEMP 'm05'
if (-not (Test-Path -LiteralPath $sampleDir)) { New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null }
$sample = Join-Path $sampleDir 'sample-drag.txt'
Set-Content -LiteralPath $sample -Value 'M05 drag target sample' -Encoding UTF8

if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }

Write-Output ("experiment: target=({0},{1}) start=({2},{3})" -f $TargetX, $TargetY, $StartX, $StartY)

$proc = Start-Process -FilePath $m05 -ArgumentList '--force-overlay' -PassThru
Start-Sleep -Milliseconds $WarmupMs

$simArgs = @($sample, "$TargetX", "$TargetY")
if ($StartX -ge 0 -and $StartY -ge 0) { $simArgs += @("$StartX", "$StartY") }
& $sim @simArgs

Start-Sleep -Milliseconds 800
if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(3000) | Out-Null }

Write-Output '--- m05.log ---'
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 } else { Write-Output '(no log produced)' }

$crash = Join-Path $env:TEMP 'm05\crash.log'
if (Test-Path -LiteralPath $crash) {
  Write-Output '--- crash.log ---'
  Get-Content -LiteralPath $crash -Encoding UTF8
}
