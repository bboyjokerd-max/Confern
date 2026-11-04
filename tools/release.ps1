# release.ps1 -- one command that turns "the source is ready" into "a release people can install".
#
# WHAT IT DOES (in this order, and it stops at the first failure)
#   1. read tools\release.config.json (who publishes, where, what the two manifests are called)
#   2. build the PAID installer, then the FREE one, each with its own update-manifest url baked in as
#      the seeded settings file (see build-installer.ps1 -UpdateUrl)
#   3. collect size + sha256 of both setups
#   4. write dist\release\update-paid.json and update-free.json (the two files the products fetch)
#   5. write dist\release\RELEASE-NOTES.md and dist\release\SHA256SUMS.txt
#   6. print the exact next steps (this script never talks to the internet)
#
# WHY IT DOES NOT UPLOAD ANYTHING
#   Uploading needs credentials, and a build script holding credentials is a foot-gun: it can push
#   the wrong thing, at the wrong time, with nobody watching. The upload is two commands (git push,
#   then gh release create) or eight clicks in the browser -- and they are printed at the end with
#   this run's real file names and hashes already filled in.
#
#   When `gh` IS installed and authenticated, add -Upload and the script runs `gh release create`
#   itself. That is the only internet access it ever performs, it is opt-in, and it prints the
#   command before running it.
#
# WHAT THIS SCRIPT REFUSES TO DO
#   * guess a version (it reads it from the csproj and says so)
#   * publish with an empty changelog (a release whose notes say nothing is worse than no release)
#   * write a manifest pointing at a file that is not next to it
#
# ENCODING: ASCII only, CRLF, no BOM.

[CmdletBinding()]
param(
    # Skip the installer drill (only for iterating on this script itself; never for a real release).
    [switch]$SkipDrill,
    # The changelog for THIS release. Empty = read dist\release\notes.txt if it exists, else refuse.
    [string]$Notes = '',
    # Opt-in: run `gh release create` at the end (needs gh installed and authenticated).
    [switch]$Upload,
    # Tag/version override. Empty = the version in src\Sidecar\Sidecar.csproj.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Say([string]$Text) { Write-Host "release: $Text" }
function Die([string]$Message, [int]$Code) {
    Write-Host "release: FATAL: $Message" -ForegroundColor Red
    exit $Code
}

# ------------------------------------------------------------------------------ 1. the release config
$configPath = Join-Path $PSScriptRoot 'release.config.json'
if (-not (Test-Path -LiteralPath $configPath)) { Die "no $configPath (see the template inside this script's header)" 2 }
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$owner = [string]$config.owner
$repo = [string]$config.repo
$branch = if ([string]$config.branch) { [string]$config.branch } else { 'main' }
if ($owner -eq '' -or $repo -eq '') { Die "release.config.json needs 'owner' and 'repo' (the GitHub account and repository name)" 2 }

# The two manifests live at the repository root on the publishing branch, so their urls are stable
# even when the release assets themselves get new names.
$rawBase = "https://raw.githubusercontent.com/$owner/$repo/$branch"
$paidManifestUrl = "$rawBase/update-paid.json"
$freeManifestUrl = "$rawBase/update-free.json"

# -------------------------------------------------------------------------------------- 2. the version
$csproj = Join-Path $root 'src\Sidecar\Sidecar.csproj'
if ($Version -eq '') {
    $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { Die "no <Version> in $csproj" 2 }
    $Version = $m.Groups[1].Value.Trim()
}
Say "version $Version (from $(if ($Version) { 'the request or Sidecar.csproj' }))"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die "the version must be major.minor.patch (got '$Version')" 2 }

# ------------------------------------------------------------------------------------- 2b. the notes
if ($Notes -eq '') {
    $notesFile = Join-Path $root 'dist\release\notes.txt'
    if (Test-Path -LiteralPath $notesFile) { $Notes = (Get-Content -LiteralPath $notesFile -Raw -Encoding UTF8).Trim() }
}
if ($Notes -eq '') {
    Die "no release notes. Pass -Notes 'what changed' or write dist\release\notes.txt first (a release whose notes say nothing is worse than no release)" 2
}

$dist = Join-Path $root 'dist'
$releaseDir = Join-Path $dist 'release'
if (-not (Test-Path -LiteralPath $releaseDir)) { New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null }
$stamp = (Get-Date).ToString('yyyy-MM-dd')

# ------------------------------------------------------------------------------- 3. build both editions
# A stale resident must not be alive while the installers are drilled (the drill installs and runs
# the product; a leftover tray would answer for the new one).
Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

function Invoke-Installer([string]$Edition, [string]$Url) {
    Say "building the $Edition installer (update url: $(if ($Url) { $Url } else { 'none' }))"
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'build-installer.ps1'),
              '-Version', $Version, '-Edition', $Edition, '-UpdateUrl', $Url)
    if ($SkipDrill) { $argv += '-SkipDrill' }
    & powershell @argv
    if ($LASTEXITCODE -ne 0) { Die "build-installer.ps1 -Edition $Edition exited with $LASTEXITCODE" 3 }
}

Invoke-Installer 'Paid' $paidManifestUrl
$paidSetup = Join-Path $dist "Confern-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $paidSetup)) { Die "the paid setup is missing at $paidSetup" 3 }

Invoke-Installer 'Free' $freeManifestUrl
$freeSetup = Join-Path $dist "Confern-Free-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $freeSetup)) { Die "the free setup is missing at $freeSetup" 3 }

# ---------------------------------------------------------------------------- 4. the two manifests
function Get-Info([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        Name   = $item.Name
        Bytes  = $item.Length
        Size   = ('{0:N2} MB' -f ($item.Length / 1MB))
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}
$paid = Get-Info $paidSetup
$free = Get-Info $freeSetup

# The download url is the release asset. GitHub turns every attached file into
#   https://github.com/<owner>/<repo>/releases/download/<tag>/<file name>
# so the url is knowable BEFORE the upload -- which is what lets the manifest be written first.
$tag = "v$Version"
function Get-AssetUrl([string]$Name) { return "https://github.com/$owner/$repo/releases/download/$tag/$Name" }

function Write-Manifest([string]$Path, [string]$EditionLabel, $Info, [string]$Notes) {
    # One file per edition, each self-contained. Two files rather than one file with two sections:
    # a single URL per build means a build can only ever be pointed at the manifest that describes
    # ITSELF, and a free build can never be handed an exe that costs money.
    $escapedNotes = $Notes.Replace('\', '\\').Replace('"', '\"').Replace("`r", ' ').Replace("`n", ' ').Trim()
    $json = @'
{
  "version": "__VERSION__",
  "url": "__URL__",
  "sha256": "__SHA256__",
  "notes": "__NOTES__",
  "mandatory": false,
  "published": "__DATE__",
  "edition": "__EDITION__",
  "size": "__SIZE__"
}
'@
    $json = $json.Replace('__VERSION__', $Version)
    $json = $json.Replace('__URL__', (Get-AssetUrl $Info.Name))
    $json = $json.Replace('__SHA256__', $Info.Sha256)
    $json = $json.Replace('__NOTES__', $escapedNotes)
    $json = $json.Replace('__DATE__', $stamp)
    $json = $json.Replace('__EDITION__', $EditionLabel)
    $json = $json.Replace('__SIZE__', $Info.Size)
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
    Say "wrote $(Split-Path -Leaf $Path)"
}

Write-Manifest (Join-Path $releaseDir 'update-paid.json') 'paid' $paid $Notes
Write-Manifest (Join-Path $releaseDir 'update-free.json') 'free' $free $Notes

# A manifest that points at a file which is not in this folder is the one failure mode that would
# make every user's check fail silently. Copy the setups next to the manifests so the upload folder
# is self-contained, then prove the promise holds.
Copy-Item -LiteralPath $paidSetup -Destination $releaseDir -Force
Copy-Item -LiteralPath $freeSetup -Destination $releaseDir -Force
foreach ($m in @('update-paid.json', 'update-free.json')) {
    $obj = Get-Content -LiteralPath (Join-Path $releaseDir $m) -Raw -Encoding UTF8 | ConvertFrom-Json
    $asset = Join-Path $releaseDir (Split-Path -Leaf ([uri]$obj.url).AbsolutePath)
    if (-not (Test-Path -LiteralPath $asset)) { Die "$m points at $($obj.url) but $asset is not in the release folder" 4 }
    $real = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash
    if ($real -ne $obj.sha256) { Die "$m has sha256 $($obj.sha256) but $asset hashes to $real" 4 }
}
Say "both manifests point at files that exist in $releaseDir, and their sha256 match"

# ---------------------------------------------------------------------------------- 5. notes + sums
$notesText = @'
# Confern $Version

$Notes

## Downloads

| Edition | File | Size | sha256 |
|---|---|---|---|
| Paid (CUTOUT + 10 free uses + licence) | `$paidName` | $paidSize | `$paidSha` |
| Free / open source (no CUTOUT) | `$freeName` | $freeSize | `$freeSha` |

License: MIT. The free edition has no cutout tool at all -- it is not a paywall, there is simply one
fewer sector on the wheel.
'@
$notesText = $notesText.Replace('$Version', $Version).Replace('$Notes', $Notes)
$notesText = $notesText.Replace('$paidName', $paid.Name).Replace('$paidSize', $paid.Size).Replace('$paidSha', $paid.Sha256)
$notesText = $notesText.Replace('$freeName', $free.Name).Replace('$freeSize', $free.Size).Replace('$freeSha', $free.Sha256)
[System.IO.File]::WriteAllText((Join-Path $releaseDir 'RELEASE-NOTES.md'), $notesText + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

$sums = @(
    ('{0}  {1}' -f $paid.Sha256, $paid.Name),
    ('{0}  {1}' -f $free.Sha256, $free.Name)
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $releaseDir 'SHA256SUMS.txt'), $sums + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

# ------------------------------------------------------------------------------------- 6. next steps
Say ""
Say "================== READY: dist\release\ =================="
Say "  update-paid.json   $($paidManifestUrl)"
Say "  update-free.json   $($freeManifestUrl)"
Say "  $($paid.Name)  $($paid.Size)  $($paid.Sha256)"
Say "  $($free.Name)  $($free.Size)  $($free.Sha256)"
Say ""
Say "The two manifests must be committed to the repository root on '$branch' BEFORE the release, so"
Say "the url baked into the installers answers from the first day:"
Say "  git add update-paid.json update-free.json"
Say "  git commit -m `"release: $tag`""
Say "  git tag $tag"
Say "  git push origin $branch --follow-tags"
Say ""
Say "Then attach the installers to the tag (either of these):"
Say "  A) gh release create $tag dist\release\$($paid.Name) dist\release\$($free.Name) --title `"Confern $Version`" --notes-file dist\release\RELEASE-NOTES.md"
Say "  B) open https://github.com/$owner/$repo/releases/new , pick the tag $tag, drag the two files from dist\release\"
Say ""
Say "Verify afterwards (this is the same request every user's machine will make):"
Say "  curl -s $paidManifestUrl"

if ($Upload) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { Die "-Upload was requested but gh is not installed" 5 }
    $cmd = @('gh', 'release', 'create', $tag, (Join-Path $releaseDir $paid.Name), (Join-Path $releaseDir $free.Name),
             '--title', "Confern $Version", '--notes-file', (Join-Path $releaseDir 'RELEASE-NOTES.md'))
    Say "running: $($cmd -join ' ')"
    & $gh.Source $cmd[1..($cmd.Length - 1)]
    if ($LASTEXITCODE -ne 0) { Die "gh release create exited with $LASTEXITCODE" 5 }
    Say "the release exists; the manifests must still be committed and pushed (the urls above)"
}

Say ""
Say "done"
exit 0
