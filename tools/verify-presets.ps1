# verify-presets.ps1 -- per-source-format presets (2026-10-23): "when I drop a HEIC, start the wheel
# on JPG".
#
# WHAT THIS SUITE PINS DOWN
#   Two halves, both reachable without a mouse:
#     1. the store and its CLI (`confern preset`): what is accepted, what is REFUSED and why, what the
#        file looks like, what happens when the file is corrupt;
#     2. the behaviour: the wheel really starts on the preset sector, and the preset quality really
#        reaches the encoder -- proven by the SIZE of two output files, not by a log line. A log line
#        saying "quality 60" can be right while the bytes are wrong; bytes cannot lie.
#
# WHY THE REFUSALS MATTER MORE THAN THE ACCEPTANCES
#   A preset that is stored but never used is worse than no preset: the user believes he configured
#   something and the wheel keeps starting on PNG. So: the source must be a format this build opens,
#   the target must be one this build really offers for that source, and a quality is only accepted
#   for a format whose encoder has a quality knob. Each rule has its own check below.
#
# TEST ISOLATION
#   The suite points CONFERN_PRESETS at its own throwaway file and then asserts that the REAL
#   %LOCALAPPDATA%\Confern\presets.json is byte-identical afterwards. That one check covers the
#   env-var override, and "acceptance never eats the user's preferences" at the same time. The real
#   file is also backed up and restored, so a bug cannot cost the user his settings.
#
# PROCESS HANDLING (learned the hard way, twice)
#   * Start-Process -PassThru without -Wait leaves $p.ExitCode NULL in PowerShell 5.1, and -Wait cannot
#     be given a timeout. So the child is started through .NET: WaitForExit(ms) both bounds it and
#     fills ExitCode.
#   * A hung child must never stall the sweep: on 2026-10-23 a command that fell through to the
#     resident path (tray + message loop) cost ten minutes. Every launch here has a hard deadline.
#
# ASCII ONLY on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1 and turns into fake
# syntax errors. Nothing here asserts a Chinese string.
#
# Usage: powershell -File tools\verify-presets.ps1 -Exe <path to Confern.exe> [-Work <dir>]

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-presets')
)

$ErrorActionPreference = 'Continue'
$script:checks = 0
$script:pass = 0
$script:fail = 0
$script:outFormats = @()
# Pinning the count makes a silently-missing check a loud FATAL instead of a smaller total.
$script:ExpectedChecks = 40

Add-Type -AssemblyName System.Drawing

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) { $script:pass++; "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name  [$detail]" }
}

if (-not (Test-Path $Work)) { New-Item -ItemType Directory -Path $Work -Force | Out-Null }

# ------------------------------------------------------------------------------------------------
# Running the product
# ------------------------------------------------------------------------------------------------
function Quote-Arg([string]$a) {
    if ($a -ne '' -and $a -notmatch '[\s"]') { return $a }
    return '"' + ($a -replace '(\\*)"', '$1$1\"') + '"'
}

function Start-Captured([string]$exePath, [string[]]$argv, [int]$timeoutMs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.Arguments = (($argv | ForEach-Object { Quote-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Close()
    # Both pipes are drained asynchronously: a full pipe buffer would deadlock the child.
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($timeoutMs)) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{
            Exit = -1; Out = ''; Err = "TIMEOUT after $timeoutMs ms: $($argv -join ' ')"; TimedOut = $true
        }
    }
    $null = $proc.WaitForExit()
    return [pscustomobject]@{
        Exit = $proc.ExitCode; Out = [string]$outTask.Result; Err = [string]$errTask.Result; TimedOut = $false
    }
}

function Invoke-Cli([string[]]$argv, [string]$tag) {
    $log = Join-Path $Work "$tag.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    # --log on purpose: under --cli-subcommand the evidence lines ("which preset decided the quality")
    # go to the log file, not to stdout. Asserting stdout alone would assert nothing.
    # EXCEPT for `formats`, which refuses any extra argument on purpose -- asking it for a log would
    # turn a working command into exit 4.
    $logArg = if ($argv.Count -ge 1 -and $argv[0] -eq 'formats') { @() } else { @('--log', $log) }
    $r = Start-Captured $Exe ($argv + $logArg) 180000
    $r | Add-Member -NotePropertyName Log -NotePropertyValue (
        [string](@(if (Test-Path $log) { Get-Content $log -Encoding UTF8 }) -join "`n"))
    return $r
}

# One wheel run through the SAME seam m1b uses: it really commits, so a conversion really happens.
function Invoke-Wheel([string]$payload, [string]$tag) {
    $log = Join-Path $Work "wheel-$tag.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $argv = @('--wheel-payload', $payload, '--no-autostart', '--watchdog-seconds', '300',
              '--quiet', '--log', $log)
    $r = Start-Captured $Exe $argv 240000
    return [pscustomobject]@{
        Exit     = $r.Exit
        TimedOut = $r.TimedOut
        Log      = [string](@(if (Test-Path $log) { Get-Content $log -Encoding UTF8 }) -join "`n")
    }
}

# ------------------------------------------------------------------------------------------------
# The store and the test files
# ------------------------------------------------------------------------------------------------
function Get-StoreText() {
    if (-not (Test-Path $store)) { return '' }
    return [string](@(Get-Content $store -Encoding UTF8) -join "`n")
}

function Set-StoreText([string]$text) {
    $dir = Split-Path -Parent $store
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $store -Value $text -Encoding UTF8 -NoNewline
}

function New-Png([string]$path, [int]$size, [int]$argb) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb($argb))
    # Structure on purpose: two qualities of the SAME picture must produce different file sizes,
    # otherwise check S5.2 could not tell a real quality change from a no-op.
    for ($i = 0; $i -lt $size; $i += 7) {
        $c = [System.Drawing.Color]::FromArgb(255, ($i * 3) % 255, ($i * 5) % 255, ($i * 7) % 255)
        $brush = New-Object System.Drawing.SolidBrush -ArgumentList $c
        $g.FillRectangle($brush, $i, 0, 6, $size)
        $brush.Dispose()
    }
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# The files the USER would have. Never written by this suite -- only watched.
$realStore = Join-Path $env:LOCALAPPDATA 'Confern\presets.json'
$realBackup = Join-Path $Work 'presets.json.backup'
$realHad = Test-Path $realStore
$realBefore = if ($realHad) { (Get-FileHash $realStore -Algorithm SHA256).Hash } else { '(absent)' }
if ($realHad) { Copy-Item $realStore $realBackup -Force }

# The `convert` calls below are real conversions, and "recent conversions" is global: without this
# the user's own list would collect a dozen %TEMP% paths (that is exactly what happened on the first
# run of this suite). Same override the recent suite uses.
$realRecent = Join-Path $env:LOCALAPPDATA 'Confern\recent.json'
$realRecentBackup = Join-Path $Work 'recent.json.backup'
$realRecentHad = Test-Path $realRecent
$realRecentBefore = if ($realRecentHad) { (Get-FileHash $realRecent -Algorithm SHA256).Hash } else { '(absent)' }
if ($realRecentHad) { Copy-Item $realRecent $realRecentBackup -Force }

# From here on, every Confern this suite starts writes its presets and its recent list HERE.
$store = Join-Path $Work 'presets.json'
$env:CONFERN_PRESETS = $store
$env:CONFERN_RECENT = Join-Path $Work 'recent.json'
Remove-Item $store -Force -ErrorAction SilentlyContinue
Remove-Item $env:CONFERN_RECENT -Force -ErrorAction SilentlyContinue

# The sectors this machine really offers for PNG. Read from the product, never hardcoded: hardcoding
# would make this suite red on a machine whose build has a WebP encoder (this one does not).
$formatsRun = Invoke-Cli @('formats') 'formats'
$outBlock = $false
foreach ($line in ($formatsRun.Out -split "`n")) {
    if ($line -match 'OUT \(what --to accepts\)') { $outBlock = $true; continue }
    if ($outBlock -and $line -match '^\s*examples') { break }
    if (-not $outBlock) { continue }
    if ($line -match '^\s+([A-Z]+)\s') { $script:outFormats += $matches[1] }
}
$pngTargets = @($script:outFormats | Where-Object { $_ -ne 'PNG' })

"### $Exe"
"### work: $Work"
"### store: $store"
"### real store: $realStore (pre-existing: $realHad)"
"### PNG sectors on this machine: [$($pngTargets -join ',')]"

# --------------------------------------------------------------------------------------------
"=== S1 the CLI surface and the refusals ==="
$r = Invoke-Cli @('preset') 'S1-empty'
Check 'S1.1 `confern preset` exits 0' ($r.Exit -eq 0) "exit=$($r.Exit) err=$($r.Err)"
Check 'S1.2 it prints a header that names the feature' (
    $r.Out -match 'preset conversions \(\d+ source format\(s\)') $r.Out
Check 'S1.3 it says which file it is reading' ($r.Out -match 'file: .*presets\.json') $r.Out
Check 'S1.4 an empty list says so and how to add one' (
    $r.Out -match '\(none\) - add one with: confern preset --set' -and $r.Out -match 'total=0') $r.Out

$r = Invoke-Cli @('frobnicate') 'S1-unknown'
Check 'S1.5 the unknown-command message lists preset as a real command' (
    $r.Exit -eq 4 -and $r.Err -match 'confern preset') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG') 'S1-noshape'
Check 'S1.6 --set without "=" is refused with the expected shape' (
    $r.Exit -eq 4 -and $r.Err -match 'needs <SOURCE=TARGET>') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG=') 'S1-emptytarget'
Check 'S1.7 --set with an empty target is refused' (
    $r.Exit -eq 4 -and $r.Err -match 'needs <SOURCE=TARGET>') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'XCF=PNG') 'S1-badsource'
Check 'S1.8 a source this build cannot open is refused, and the openable ones are listed' (
    $r.Exit -eq 4 -and $r.Err -match "'XCF' is not a format this build opens" -and
    $r.Err -match 'opens=\[JPG,PNG') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG=ZIP') 'S1-badtarget'
Check 'S1.9 a target this source cannot be converted to is refused, with what it can be' (
    $r.Exit -eq 4 -and $r.Err -match "PNG cannot be converted to 'ZIP'" -and
    $r.Err -match 'can be: \[') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG=JPG:40') 'S1-badquality'
Check 'S1.10 a quality outside 60-100 is refused (not silently clamped)' (
    $r.Exit -eq 4 -and $r.Err -match 'quality must be 60-100 \(got 40\)') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG=BMP:80') 'S1-noquality'
Check 'S1.11 a quality on a format without a quality knob is refused (a stored-but-ignored number)' (
    $r.Exit -eq 4 -and $r.Err -match 'BMP has no quality setting') "exit=$($r.Exit) err=$($r.Err)"

$r = Invoke-Cli @('preset', 'PNG=JPG') 'S1-bareword'
Check 'S1.12 a bare word is refused instead of being read as a flag value' (
    $r.Exit -eq 4 -and $r.Err -match 'preset takes no bare words') "exit=$($r.Exit) err=$($r.Err)"
Check 'S1.13 none of the refusals left a file behind' (-not (Test-Path $store)) "store exists=$(Test-Path $store)"

# --------------------------------------------------------------------------------------------
"=== S2 storing, listing, removing ==="
$r = Invoke-Cli @('preset', '--set', 'HEIC=JPG:82') 'S2-set'
Check 'S2.1 a valid preset is accepted and says what it will do' (
    $r.Exit -eq 0 -and $r.Out -match 'set HEIC -> JPG \(quality 82\)' -and
    $r.Out -match 'the wheel will start on JPG when you drop a HEIC file') "exit=$($r.Exit) out=$($r.Out)"

$r = Invoke-Cli @('preset') 'S2-list'
Check 'S2.2 the list shows the entry with its quality and total=1' (
    $r.Out -match 'HEIC\s+-> JPG\s+\(quality 82\)' -and $r.Out -match 'total=1') $r.Out

$json = $null
try { $json = Get-Content $store -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
Check 'S2.3 the file on disk really holds the three fields' (
    $json -ne $null -and $json.SourceFormat -eq 'HEIC' -and $json.Target -eq 'JPG' -and
    [int]$json.Quality -eq 82) (Get-StoreText)

$r = Invoke-Cli @('preset', '--set', 'HEIC=PNG') 'S2-overwrite'
$r2 = Invoke-Cli @('preset') 'S2-list2'
Check 'S2.4 setting the same source again replaces it (total stays 1)' (
    $r.Exit -eq 0 -and $r2.Out -match 'total=1' -and $r2.Out -match 'HEIC\s+-> PNG') `
    "exit=$($r.Exit) out=$($r2.Out)"

$r = Invoke-Cli @('preset', '--remove', 'HEIC') 'S2-remove'
Check 'S2.5 --remove drops it and says what remains' (
    $r.Exit -eq 0 -and $r.Out -match 'removed HEIC\s+\(remaining 0\)') "exit=$($r.Exit) out=$($r.Out)"

$r = Invoke-Cli @('preset', '--remove', 'HEIC') 'S2-remove-again'
Check 'S2.6 removing something that is not there is exit 4, not a silent success' (
    $r.Exit -eq 4 -and $r.Err -match 'has no preset') "exit=$($r.Exit) err=$($r.Err)"

Invoke-Cli @('preset', '--set', 'HEIC=JPG') 'S2-again' | Out-Null
$r = Invoke-Cli @('preset', '--clear') 'S2-clear'
Check 'S2.7 --clear empties the file (its content is an empty JSON array)' (
    $r.Exit -eq 0 -and (Get-StoreText).Trim() -eq '[]') "exit=$($r.Exit) content=$(Get-StoreText)"

# --------------------------------------------------------------------------------------------
"=== S3 a corrupt file is survivable AND visible ==="
Set-StoreText '{ this is not json'
$r = Invoke-Cli @('preset') 'S3-corrupt'
Check 'S3.1 a corrupt file still exits 0 with an empty list' (
    $r.Exit -eq 0 -and $r.Out -match 'total=0') "exit=$($r.Exit) out=$($r.Out)"
Check 'S3.2 and it says on stderr WHY the list is empty (not just "0 entries")' (
    $r.Err -match 'presets: .*is unreadable') "err=$($r.Err)"

$r = Invoke-Cli @('preset', '--set', 'PNG=TIF') 'S3-heal'
$healed = $null
try { $healed = Get-Content $store -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
Check 'S3.3 a write heals the file (it becomes valid JSON again)' (
    $r.Exit -eq 0 -and $healed -ne $null -and @($healed).Count -eq 1) `
    "exit=$($r.Exit) content=$(Get-StoreText)"

# --------------------------------------------------------------------------------------------
"=== S4 the wheel starts on the preset sector (real payload, real commit) ==="
$firstTarget = 'JPG'
$otherTarget = 'TIF'
$otherIndex = 0
if ($pngTargets.Count -ge 2) {
    $firstTarget = $pngTargets[0]
    $otherTarget = $pngTargets[$pngTargets.Count - 1]
    $otherIndex = [array]::IndexOf($pngTargets, $otherTarget)
}

# (a) no preset: sector 0 -- the behaviour before this feature existed
Invoke-Cli @('preset', '--clear') 'S4-clear' | Out-Null
$dirA = Join-Path $Work 'w-a'
Remove-Item $dirA -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirA -Force | Out-Null
$a = Join-Path $dirA 'a.png'
New-Png $a 96 ([int]0xFF3366CC)
$w = Invoke-Wheel $a 'nopreset'
$outA = Get-ChildItem $dirA -Filter ("a." + $firstTarget.ToLowerInvariant()) -ErrorAction SilentlyContinue
Check 'S4.1 with no preset the wheel starts on sector 0 and converts to the first offered target' (
    $w.Log -match 'wheel shown .*selected=0 ' -and $w.Log -match 'wheel from OLE payload: ' -and
    ($outA -ne $null)) "exit=$($w.Exit) target=$firstTarget exists=$($outA -ne $null)"

# (b) a preset pointing at a LATER sector: the wheel must start there and commit that target
Invoke-Cli @('preset', '--set', "PNG=$otherTarget") 'S4-set' | Out-Null
$dirB = Join-Path $Work 'w-b'
Remove-Item $dirB -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirB -Force | Out-Null
$b = Join-Path $dirB 'b.png'
New-Png $b 96 ([int]0xFFCC6633)
$w = Invoke-Wheel $b 'preset'
$outB = Get-ChildItem $dirB -Filter ("b." + $otherTarget.ToLowerInvariant()) -ErrorAction SilentlyContinue
Check "S4.2 with PNG -> $otherTarget the wheel starts on sector $otherIndex and says why" (
    $w.Log -match ("wheel preset: source=PNG -> " + $otherTarget + " starts on sector " + $otherIndex + " ") -and
    $w.Log -match ("wheel shown .*selected=" + $otherIndex + " ")) `
    (($w.Log -split "`n" | Where-Object { $_ -match 'wheel (preset|shown)' }) -join ' / ')

Check "S4.3 the commit really produced the preset target ($otherTarget), not the default" (
    $outB -ne $null) "dir=$((Get-ChildItem $dirB | ForEach-Object { $_.Name }) -join ',')"

# (c) a preset whose target this build does not offer must fall back to 0 and SAY SO
Set-StoreText '[{"SourceFormat":"PNG","Target":"AVIF","Quality":0}]'
$dirC = Join-Path $Work 'w-c'
Remove-Item $dirC -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirC -Force | Out-Null
$c = Join-Path $dirC 'c.png'
New-Png $c 96 ([int]0xFF22AA55)
$w = Invoke-Wheel $c 'unoffered'
Check 'S4.4 a preset this build cannot offer falls back to sector 0 and says why' (
    $w.Log -match 'wheel preset: source=PNG -> AVIF is not offered by this build' -and
    $w.Log -match 'wheel shown .*selected=0 ') `
    (($w.Log -split "`n" | Where-Object { $_ -match 'wheel preset' }) -join ' / ')

# (d) a preset for a DIFFERENT source must not touch this payload
Set-StoreText '[{"SourceFormat":"HEIC","Target":"JPG","Quality":0}]'
$dirD = Join-Path $Work 'w-d'
Remove-Item $dirD -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirD -Force | Out-Null
$d = Join-Path $dirD 'd.png'
New-Png $d 96 ([int]0xFF884488)
$w = Invoke-Wheel $d 'othersource'
Check 'S4.5 a preset for another source format leaves this payload alone (sector 0)' (
    $w.Log -notmatch 'wheel preset:' -and $w.Log -match 'wheel shown .*selected=0 ') "exit=$($w.Exit)"

# --------------------------------------------------------------------------------------------
"=== S5 the preset quality really reaches the encoder ==="
$dirQ = Join-Path $Work 'w-q'
Remove-Item $dirQ -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirQ -Force | Out-Null
$srcQ = Join-Path $dirQ 'q.png'
New-Png $srcQ 220 ([int]0xFFAA5522)

Invoke-Cli @('preset', '--set', 'PNG=JPG:60') 'S5-set60' | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dirQ 'q60') -Force | Out-Null
$r60 = Invoke-Cli @('convert', $srcQ, '--to', 'JPG', '-o', (Join-Path $dirQ 'q60')) 'S5-q60'
$f60 = Get-ChildItem (Join-Path $dirQ 'q60') -Filter '*.jpg' -ErrorAction SilentlyContinue | Select-Object -First 1
Check 'S5.1 the CLI says which preset decided the quality' (
    $r60.Exit -eq 0 -and $r60.Log -match 'convert preset: PNG -> JPG uses quality 60') `
    "exit=$($r60.Exit) log=$(($r60.Log -split "`n" | Where-Object { $_ -match 'convert preset' }) -join ' / ')"

Invoke-Cli @('preset', '--set', 'PNG=JPG:100') 'S5-set100' | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dirQ 'q100') -Force | Out-Null
$r100 = Invoke-Cli @('convert', $srcQ, '--to', 'JPG', '-o', (Join-Path $dirQ 'q100')) 'S5-q100'
$f100 = Get-ChildItem (Join-Path $dirQ 'q100') -Filter '*.jpg' -ErrorAction SilentlyContinue | Select-Object -First 1
Check 'S5.2 quality 60 really produces a SMALLER file than quality 100 (bytes, not logs)' (
    $f60 -ne $null -and $f100 -ne $null -and $f60.Length -lt $f100.Length) `
    ("q60=" + $(if ($f60) { $f60.Length } else { 'missing' }) + " q100=" + $(if ($f100) { $f100.Length } else { 'missing' }))

# An explicit --quality must win over the preset: the user said 90 for THIS run.
New-Item -ItemType Directory -Path (Join-Path $dirQ 'q90') -Force | Out-Null
$rExplicit = Invoke-Cli @('convert', $srcQ, '--to', 'JPG', '--quality', '90', '-o', (Join-Path $dirQ 'q90')) 'S5-explicit'
Check 'S5.3 an explicit --quality overrides the preset (and no preset line appears)' (
    $rExplicit.Exit -eq 0 -and $rExplicit.Log -notmatch 'convert preset:') `
    "exit=$($rExplicit.Exit) log=$(($rExplicit.Log -split "`n" | Where-Object { $_ -match 'convert preset|quality=' }) -join ' / ')"

# The wheel path takes the same decision through the same policy.
$dirW = Join-Path $Work 'w-qual'
Remove-Item $dirW -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dirW -Force | Out-Null
$wq = Join-Path $dirW 'wq.png'
New-Png $wq 160 ([int]0xFF3388AA)
Invoke-Cli @('preset', '--set', 'PNG=JPG:72') 'S5-set72' | Out-Null
$w = Invoke-Wheel $wq 'quality'
$wqOut = Get-ChildItem $dirW -Filter 'wq.jpg' -ErrorAction SilentlyContinue
Check 'S5.4 the wheel path takes the quality from the preset too (and logs it)' (
    $w.Log -match 'preset quality: PNG -> JPG uses 72' -and $wqOut -ne $null) `
    (($w.Log -split "`n" | Where-Object { $_ -match 'preset quality|convert result' }) -join ' / ')

# A preset for a DIFFERENT target must not silently change the quality of this conversion.
Set-StoreText '[{"SourceFormat":"PNG","Target":"TIF","Quality":60}]'
New-Item -ItemType Directory -Path (Join-Path $dirQ 'qm') -Force | Out-Null
$rMismatch = Invoke-Cli @('convert', $srcQ, '--to', 'JPG', '-o', (Join-Path $dirQ 'qm')) 'S5-mismatch'
Check 'S5.5 a preset for a different target leaves the quality alone' (
    $rMismatch.Exit -eq 0 -and $rMismatch.Log -notmatch 'convert preset:') `
    "log=$(($rMismatch.Log -split "`n" | Where-Object { $_ -match 'convert preset|quality=' }) -join ' / ')"

# --------------------------------------------------------------------------------------------
"=== S6 source guards (the parts the behaviour checks cannot see) ==="
$root = Split-Path -Parent (Split-Path -Parent $Script:MyInvocation.MyCommand.Path)
$program = [string](@(Get-Content (Join-Path $root 'src\Sidecar\Program.cs') -Encoding UTF8) -join "`n")
$presetSrc = [string](@(Get-Content (Join-Path $root 'src\Sidecar\Presets\PresetStore.cs') -Encoding UTF8) -join "`n")
$cliSrc = [string](@(Get-Content (Join-Path $root 'src\Sidecar\Cli\CliArgs.cs') -Encoding UTF8) -join "`n")

Check 'S6.1 the format wheel gets its initial sector from PresetSelection (not a hardcoded 0)' (
    $program -match 'int selected = PresetSelection\(_wheelSourceFormat, _wheelFormatTargets, evidence\)' -and
    $program -match 'presenter\.Show\(x, y, _wheelSourceFormat, _wheelFormatTargets, selected,') 'source guard'

Check 'S6.2 the UI commit site takes its quality from PresetQuality (one policy, not two)' (
    $program -match 'toolOp == null \? PresetQuality\(settings\.QualityFor\(target\), target, evidence\) : 100') `
    'source guard'

Check 'S6.3 the store writes through AtomicWriter with overwrite AND leaves the stream open' (
    $presetSrc -match 'AtomicWriter\.Write\(' -and $presetSrc -match 'overwrite: true' -and
    $presetSrc -match 'leaveOpen: true') 'source guard'

Check 'S6.4 --quality is recorded as EXPLICIT (the flag-vs-preset precedence needs that bit)' (
    $program -match 'o\.ConvertQualityGiven = true;') 'source guard'

Check 'S6.5 both preset flags are registered as value flags (--set and --remove eat a value)' (
    $cliSrc -match '"--preset-set"' -and $cliSrc -match '"--preset-remove"') 'source guard'

# --------------------------------------------------------------------------------------------
"=== S7 the acceptance run did not touch the user's own presets ==="
$realAfter = if (Test-Path $realStore) { (Get-FileHash $realStore -Algorithm SHA256).Hash } else { '(absent)' }
Check 'S7.1 the real presets.json under %LOCALAPPDATA% is byte-identical (the env override works)' (
    $realAfter -eq $realBefore) "before=$realBefore after=$realAfter"

$realRecentAfter = if (Test-Path $realRecent) { (Get-FileHash $realRecent -Algorithm SHA256).Hash } else { '(absent)' }
Check 'S7.2 and so is the real recent.json (these conversions are acceptance noise, not the user history)' (
    $realRecentAfter -eq $realRecentBefore) "before=$realRecentBefore after=$realRecentAfter"

# --------------------------------------------------------------------------------------------
"### restoring"
Remove-Item $store -Force -ErrorAction SilentlyContinue
if ($realHad -and (Test-Path $realBackup)) { Copy-Item $realBackup $realStore -Force }
if ($realRecentHad -and (Test-Path $realRecentBackup)) { Copy-Item $realRecentBackup $realRecent -Force }
Remove-Item Env:CONFERN_PRESETS -ErrorAction SilentlyContinue
Remove-Item Env:CONFERN_RECENT -ErrorAction SilentlyContinue
"### done"

if ($script:checks -ne $script:ExpectedChecks) {
    "FATAL: $($script:checks) checks ran, expected $($script:ExpectedChecks)"
    exit 3
}
"checks=$($script:checks) pass=$($script:pass) fail=$($script:fail)"
if ($script:fail -gt 0) { exit 1 }
exit 0
