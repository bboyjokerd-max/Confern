<#
  sync-to-obsidian.ps1

  Mirrors the authority documents of the Tangerine Converter Windows project
  into an Obsidian vault folder.

  ASCII-ONLY ON PURPOSE:
  Windows PowerShell 5.1 reads a BOM-less UTF-8 .ps1 as ANSI (CP950 on this
  host), so any Chinese literal inside the script would be corrupted. All
  Chinese text lives in tools/obsidian/*.md and tools/obsidian/map.json, which
  are read explicitly as UTF-8. Paths arrive as parameters.

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\sync-to-obsidian.ps1 `
      -ProjectRoot "C:\Users\User\Desktop\<project>" -VaultRoot "C:\Users\User\Documents\Obsidian\Danny_Vault_01"
#>
param(
  [Parameter(Mandatory = $true)][string]$ProjectRoot,
  [Parameter(Mandatory = $true)][string]$VaultRoot,
  [string]$MapPath = 'tools\obsidian\map.json'
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Utf8Text([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Missing file: $path" }
  return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8Text([string]$path, [string]$content) {
  $dir = Split-Path -Parent $path
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

$mapFull = Join-Path $ProjectRoot $MapPath
$map = (Read-Utf8Text $mapFull) | ConvertFrom-Json

$sub = ($map.vaultSubPath -replace '/', '\')
$targetDir = Join-Path $VaultRoot $sub
if (-not (Test-Path -LiteralPath $targetDir)) {
  New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
  Write-Output ("created dir : " + $targetDir)
}

$ok = 0
$fail = 0
foreach ($f in $map.files) {
  try {
    $src = Join-Path $ProjectRoot ($f.source -replace '/', '\')
    $body = Read-Utf8Text $src

    if ($f.banner) {
      $bannerPath = Join-Path $ProjectRoot ($f.banner -replace '/', '\')
      $body = (Read-Utf8Text $bannerPath) + "`r`n" + $body
    }

    $dest = Join-Path $targetDir $f.dest
    Write-Utf8Text $dest $body

    $bytes = (Get-Item -LiteralPath $dest).Length
    $sha = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.Substring(0, 12)
    Write-Output ("synced : " + $f.dest + "  (" + $bytes + " bytes, sha256 " + $sha + ")")
    $ok++
  }
  catch {
    Write-Output ("FAILED : " + $f.dest + "  -- " + $_.Exception.Message)
    $fail++
  }
}

Write-Output ("done   : " + $ok + " synced, " + $fail + " failed  -> " + $targetDir)
if ($fail -gt 0) { exit 1 }
