param(
  [string]$Repo = '',
  [string]$VaultRoot = 'C:\Users\User\Documents\Obsidian\Danny_Vault_01',
  [switch]$DryRun
)
# -----------------------------------------------------------------------------
# Mirror the repo's Aegis docs into the Obsidian vault, using tools/obsidian/map.json.
# ASCII-only source: every CJK path/name comes from map.json, which the write tool
# authors. (PS 5.1 mis-decodes BOM-less CJK literals in scripts - never put them here.)
# -----------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
if (-not $Repo) {
  $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$mapPath = Join-Path $PSScriptRoot 'map.json'
$map = Get-Content -LiteralPath $mapPath -Raw -Encoding UTF8 | ConvertFrom-Json
$target = Join-Path $VaultRoot $map.vaultSubPath

"repo   = $Repo"
"vault  = $target"
if (-not (Test-Path -LiteralPath $target)) {
  if ($DryRun) { "  (would create the folder)"; exit 0 }
  New-Item -ItemType Directory -Force -Path $target | Out-Null
}

$written = 0
$missing = 0
foreach ($entry in $map.files) {
  $src = Join-Path $Repo $entry.source
  if (-not (Test-Path -LiteralPath $src)) {
    "MISSING SOURCE  $($entry.source)"
    $missing++
    continue
  }
  $body = Get-Content -LiteralPath $src -Raw -Encoding UTF8
  $banner = ''
  if ($entry.banner) {
    $bannerPath = Join-Path $Repo $entry.banner
    if (Test-Path -LiteralPath $bannerPath) {
      $banner = (Get-Content -LiteralPath $bannerPath -Raw -Encoding UTF8) + "`n"
    } else {
      "MISSING BANNER  $($entry.banner)"
    }
  }
  $dest = Join-Path $target $entry.dest
  $text = $banner + $body
  if ($DryRun) {
    "would write  $($entry.dest)  ($($text.Length) chars)"
  } else {
    Set-Content -LiteralPath $dest -Value $text -Encoding UTF8 -NoNewline
    "wrote        $($entry.dest)  ($((Get-Item -LiteralPath $dest).Length) bytes)"
  }
  $written++
}
"summary: entries=$($map.files.Count) written=$written missingSource=$missing"
if ($missing -gt 0) { exit 1 }
exit 0
