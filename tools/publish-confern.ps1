# publish-confern.ps1 -- turn "the model is somewhere on this machine" into "the released folder is
#                       usable out of the box", and PROVE it before claiming it.
#
# WHY THIS SCRIPT EXISTS
#   Confern's remove-bg tool runs a portrait segmentation CNN (MODNet, apache-2.0) that is a 12,984,781
#   byte binary. That binary is deliberately NOT in version control (.gitignore: models/*), so nothing
#   about it is guaranteed by a checkout. "Shipping" is therefore not "the file happens to be in some
#   folder" -- it is a sequence of steps that either all succeed or the release does not happen:
#
#     a. obtain the model, verify its sha256 AND its length (get-rembg-model.ps1 is the only way it
#        enters a build; the default store is <repo root>\models\model_fp16.onnx),
#     b. kill any running Confern (a running instance holds the exe and would make the publish fail
#        with a file-in-use error that looks like a code problem),
#     c. dotnet publish -c Release, asserting 0 warnings and 0 errors,
#     d. ASSERT the released folder really contains Confern.exe, the model AT THE PATH THE PRODUCT'S
#        DEFAULT RESOLUTION USES (<folder>\models\model_fp16.onnx), and the model's licence notice
#        (models\MODEL-LICENSE.txt -- apache-2.0 section 4(a) requires the licence to travel with the
#        product; this is asserted here, not mentioned in a README),
#     e. run the PUBLISHED exe with --remove-bg and NO --remove-bg-model, i.e. through the default
#        path, and assert exit 0 plus a real alpha PNG -- this is the only evidence of "works out of
#        the box" that is about the artefact rather than about the source tree,
#     f. NEGATIVE: do the same on a temporary copy of the released folder with the model removed and
#        assert the NAMED state ModelUnavailable (exit 3, zero files written) instead of a crash.
#
#   The default path is AppContext.BaseDirectory\models\model_fp16.onnx (RemoveBgModel.ResolvePath),
#   i.e. "beside the executable", NOT "beside the working directory": the worker process is started by
#   the UI with Process.Start(exe, "--worker", ...) and its working directory is not the exe folder.
#   That is why step (d) asserts the exact relative layout instead of "a model exists somewhere".
#
# MEASURED FACTS THIS SCRIPT PINS (measured on this machine, not copied from a page)
#   model file : model_fp16.onnx, 12,984,781 bytes
#                sha256 25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6
#                source https://huggingface.co/Xenova/modnet (onnx/model_fp16.onnx), apache-2.0
#
# USAGE
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\publish-confern.ps1
#   ... -WithOptionalModel                     ALSO build the OPTIONAL high-quality model pack
#                                              (BiRefNet, 489,666,272 bytes) as a SECOND folder.
#                                              It is NEVER part of the base release; step 6 has the
#                                              reason and the one-sentence install instruction.
#   ... -OptionalModelSource C:\path\model.onnx  offline: copy the optional model from a file you
#                                              already have instead of downloading it.
#   ... -OptionalDir D:\release\confern-birefnet  where the pack goes (default <release>-birefnet)
#   ... -OutputDir D:\release\confern          publish somewhere else (must not be a folder that
#                                              already holds unrelated files; see the guard below)
#   ... -ModelSource C:\path\model_fp16.onnx   offline: copy from a file you already have instead of
#                                              downloading (the same sha256/length are verified)
#   ... -LogDir C:\logs\confern-publish        where the logs and the temporary copies live
#   ... -SkipNegative                          skip step (f) ONLY. It defaults to running, and a
#                                              release that skipped it is not the same release.
#
# EXIT CODES
#   0 released (every assertion above passed)   1 preconditions (git/dotnet/fixture/argument)
#   2 model not obtained or not verified        3 publish failed (build error or warning)
#   4 released folder incomplete (package)      5 the published exe did not work out of the box
#   6 the negative drill did not report the named state
#   7 the optional model pack was requested and failed (obtain/verify/complete). The BASE release
#     described above is still a complete release, but this run is not a full success.
#
# THIS FILE MUST STAY PURE ASCII AND MUST NOT HAVE A BOM.
#   Windows PowerShell 5.1 reads a BOM-less script as ANSI; a CJK character in a comment silently eats
#   the following statement (measured in this repo's other tools). English comments only, ASCII only,
#   no BOM. The product's own log files ARE UTF-8 and are read with -Encoding UTF8 below.

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    # Where the released folder goes. Default: the project's own publish output (inside bin\, which is
    # git-ignored). Pass a path outside the repo for a real release.
    [string]$OutputDir = '',

    # Offline path: a file you already have, copied instead of downloaded. Verified the same way.
    [string]$ModelSource = '',

    # Logs and temporary copies. Everything this script writes that is not the release itself lives
    # here, so the repository stays clean.
    [string]$LogDir = "$env:TEMP\confern-publish",

    # The portrait used for the out-of-the-box run. Committed fixture by default.
    [string]$Fixture = '',

    [switch]$SkipNegative
,
    # ALSO build the optional high-quality model pack (BiRefNet, 489,666,272 bytes). The base release
    # NEVER carries it: that file is sixteen times the size of the whole published application, so it
    # travels as its own download that the user drops into the installed models\ folder.
    [switch]$WithOptionalModel,

    # Offline path for the optional model: a file you already have, verified exactly the same way.
    [string]$OptionalModelSource = '',

    # Where the pack goes. Default: a sibling of the release folder, named <release>-birefnet.
    [string]$OptionalDir = ''
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $repo 'src\Sidecar\Sidecar.csproj'
$fetch   = Join-Path $PSScriptRoot 'get-rembg-model.ps1'
$store   = Join-Path $repo 'models\model_fp16.onnx'
$notice  = Join-Path $repo 'models\MODEL-LICENSE.txt'
if ($Fixture -eq '') { $Fixture = Join-Path $repo 'fixtures\rembg\real-512.png' }
if ($OutputDir -eq '') { $OutputDir = Join-Path $repo "src\Sidecar\bin\$Configuration\net8.0-windows\publish" }
# CUTOUT is a paid tool with ten free uses per machine (2026-11-03). Step 4 runs the published
# exe for real, which would spend one of the USER's free uses - and on a machine that has already
# used all ten it would make this release drill fail on exit 12 for a reason that has nothing to do
# with the release. The meter is an environment variable for exactly this: it is inherited by the
# published exe and by the worker it starts, and nothing else in this script has to know.
$env:CONFERN_USAGE = Join-Path $LogDir 'usage.json'
if ($OptionalDir -eq '') { $OptionalDir = $OutputDir + '-birefnet' }

# The model's identity, as measured. Both numbers are asserted against the file that is published.
$modelName      = 'model_fp16.onnx'
$noticeName     = 'MODEL-LICENSE.txt'
$modelSha       = '25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6'
$modelBytes     = 12984781
$modelRepoUrl   = 'https://huggingface.co/Xenova/modnet'
$modelLicenseId = 'apache-2.0'
# The OPTIONAL model's identity. Measured on this machine on 2026-10-14: downloaded, length and sha256
# verified, and its interface read out of the model itself by RemoveBgModel.DescribeIo (input
# 'input_image' float32 [1,3,1024,1024], output 'output_image' float32 [1,1,1024,1024], logits that
# need a sigmoid). None of this is part of the base release.
$optModelName    = 'ModelBiRefNet_1024x1024_fp16.onnx'
$optNoticeName   = 'MODEL-BIREFNET-LICENSE.txt'
$optModelSha     = '3654C741EB80BD926ADA8FED1713B506CCF8D30EB1F6487E87EB9F234F33DF09'
$optModelBytes   = 489666272
$optModelRepoUrl = 'https://huggingface.co/onnx-community/BiRefNet-ONNX'
$optLicenseId    = 'MIT'
$fetchOpt        = Join-Path $PSScriptRoot 'get-rembg-model-birefnet.ps1'
$optStore        = Join-Path $repo 'models\ModelBiRefNet_1024x1024_fp16.onnx'
$optNotice       = Join-Path $repo 'models\MODEL-BIREFNET-LICENSE.txt'

$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Say([string]$Text) { Write-Host "publish: $Text" }
function Note([string]$Text) { Write-Host "         $Text" }

function Die([string]$Message, [int]$Code) {
    Write-Host ''
    Write-Host "publish: FAILED: $Message"
    Write-Host "publish: this run is NOT a release. logs: $LogDir"
    exit $Code
}

function Get-Sha([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'MISSING' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-Len([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return -1 }
    return (Get-Item -LiteralPath $Path).Length
}

# Start-Process does NOT quote ArgumentList elements (measured in this repo's verify suites): a single
# argument containing a space is split into several. Only elements with a space get quotes, so every
# other argument reaches the process byte-identical.
function Quote-Argv([string[]]$Argv) {
    $q = @()
    foreach ($a in $Argv) {
        if ([string]$a -match ' ') { $q += ('"' + $a + '"') } else { $q += $a }
    }
    return $q
}

# One headless run of the product. The product's --log file is UTF-8; stdout would be CP950 on this
# machine, which is why every assertion below reads the log file and never the console.
function Invoke-Product([string]$Exe, [string[]]$Argv, [string]$LogPath) {
    if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force }
    $all = @() + (Quote-Argv $Argv) + @('--log', $LogPath, '--quiet', '--no-autostart')
    $p = Start-Process -FilePath $Exe -ArgumentList $all -Wait -PassThru -WindowStyle Hidden
    $lines = @()
    if (Test-Path -LiteralPath $LogPath) { $lines = @(Get-Content -LiteralPath $LogPath -Encoding UTF8) }
    return [pscustomobject]@{ Exit = $p.ExitCode; Lines = $lines; Log = $LogPath }
}

function Get-Line([string[]]$Lines, [string]$Pattern) {
    $hit = @($Lines | Select-String -Pattern $Pattern)
    if ($hit.Count -eq 0) { return '' }
    return [string]$hit[$hit.Count - 1].Line
}

function Get-Files([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $Dir -File)
}

$report = New-Object System.Collections.ArrayList
function Record([string]$Text) {
    [void]$report.Add($Text)
    Write-Host "publish: $Text"
}

# ========================================================================================
# 0. preconditions -- a release script that cannot tell whether it may proceed must not proceed
# ========================================================================================
Say "repo=$repo"
Say "configuration=$Configuration publishDir=$OutputDir"

if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
Say "logdir=$LogDir"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { Die 'dotnet is not on PATH -- cannot publish' 1 }
$dotnetExe = $dotnet.Source

if (-not (Test-Path -LiteralPath $proj)) { Die "project not found: $proj" 1 }
if (-not (Test-Path -LiteralPath $fetch)) { Die "the model fetch script is missing: $fetch" 1 }
if (-not (Test-Path -LiteralPath $Fixture)) { Die "the portrait fixture is missing: $Fixture (it is committed)" 1 }

$isRepo = $false
try {
    $null = & git -C $repo rev-parse --is-inside-work-tree 2>&1
    $isRepo = ($LASTEXITCODE -eq 0)
} catch { $isRepo = $false }
if (-not $isRepo) { Die "not a git work tree: $repo" 1 }

# The model must NOT be in version control, and the licence notice MUST be. Both are asserted with
# git rather than with a promise in a comment: the first keeps a 12.4 MB binary out of every clone,
# the second is what apache-2.0 section 4(a) requires us to hand to whoever receives the product.
$trackedModel = @(& git -C $repo ls-files -- 'models/model_fp16.onnx' 2>&1)
if ($LASTEXITCODE -ne 0) { Die "git ls-files failed for the model path" 1 }
if ($trackedModel.Count -ne 0) {
    Die "models/model_fp16.onnx IS in version control ($($trackedModel -join ',')) -- it must not be (12.4 MB binary)" 1
}
$trackedNotice = @(& git -C $repo ls-files -- 'models/MODEL-LICENSE.txt' 2>&1)
if (-not (Test-Path -LiteralPath $notice)) {
    Die "the licence notice is missing from the working tree: $notice -- nothing to ship beside the model" 1
}
if ($trackedNotice.Count -eq 0) {
    Die "models/MODEL-LICENSE.txt is NOT in version control -- the licence notice has to be part of the source tree" 1
}
Say "git: model untracked=OK, notice tracked=$($trackedNotice.Count) file(s)"

# The output folder is deleted before publishing so that the assertions in step 3 cannot be satisfied
# by leftovers. That is only safe when the folder is a previous output of THIS product: refuse
# anything else instead of deleting a directory the user pointed us at by accident.
if (Test-Path -LiteralPath $OutputDir) {
    $looksLikeOurs = (Test-Path -LiteralPath (Join-Path $OutputDir 'Confern.exe')) -or
                     (Test-Path -LiteralPath (Join-Path $OutputDir 'Confern.dll'))
    if (-not $looksLikeOurs) {
        Die "the output folder exists and holds no Confern.exe/Confern.dll: $OutputDir -- refusing to delete it" 1
    }
    Say "clearing the previous publish output: $OutputDir"
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

# ========================================================================================
# 1. the model: obtain it, then verify the bytes that are about to be shipped
# ========================================================================================
Say '--- step 1/5: the model ---'
$fetchArgv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fetch, '-Destination', $store)
if ($ModelSource -ne '') {
    if (-not (Test-Path -LiteralPath $ModelSource)) { Die "ModelSource not found: $ModelSource" 1 }
    $fetchArgv += @('-ExistingPath', $ModelSource)
    Say "obtaining the model from a local file (offline path): $ModelSource"
} else {
    Say "obtaining the model (downloads only when it is not already present and correct)"
}
$fetchLines = @()
$fetchExit = 1
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $fetchLines = @(& powershell.exe @fetchArgv 2>&1 | ForEach-Object { [string]$_ })
    $fetchExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $prev }
foreach ($l in $fetchLines) { Note $l }
if ($fetchExit -ne 0) {
    Die "get-rembg-model.ps1 failed (exit $fetchExit): the model was NOT obtained, so there is nothing to ship" 2
}

$storeSha = Get-Sha $store
$storeLen = Get-Len $store
if ($storeSha -eq 'MISSING') { Die "the fetch script returned 0 but there is no file at $store" 2 }
if ($storeLen -ne $modelBytes) {
    Die "the model store has the wrong length: got $storeLen, expected $modelBytes ($store)" 2
}
if ($storeSha.ToUpperInvariant() -ne $modelSha) {
    Die "the model store has the wrong sha256: got $storeSha, expected $modelSha ($store)" 2
}
Record "model verified: path=$store length=$storeLen sha256=$storeSha"

# ========================================================================================
# 2. kill Confern, then publish
# ========================================================================================
Say '--- step 2/5: stop Confern, then dotnet publish ---'
# A running instance holds Confern.exe open, and the publish would then fail with a file-in-use error
# that reads like a code problem. The user's own running copy is the normal case, not an exception.
$running = @(Get-Process -Name 'Confern' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say "stopping $($running.Count) running Confern process(es): $($running.Id -join ',')"
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    $limit = 40
    while ($limit -gt 0 -and @(Get-Process -Name 'Confern' -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 100
        $limit--
    }
}
$still = @(Get-Process -Name 'Confern' -ErrorAction SilentlyContinue)
if ($still.Count -gt 0) { Die "Confern is still running (pid $($still.Id -join ',')) -- kill it and re-run" 1 }
Say 'no Confern process is running'

$publishLog = Join-Path $LogDir 'dotnet-publish.log'
$publishErr = Join-Path $LogDir 'dotnet-publish.err.log'
$msbuildLog = Join-Path $LogDir 'msbuild.log'
# DOTNET_CLI_UI_LANGUAGE=en: on a non-English machine the build summary is printed in the machine's
# own language (this machine's summary line is not the English one), and this script asserts on the
# SUMMARY line. Asking for English keeps that assertion independent of the UI language.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# !! WHY THE MSBUILD FILE LOGGER IS THE AUTHORITY HERE (measured 2026-10-08) !!
# `dotnet publish`'s "Build succeeded / 0 Warning(s) / 0 Error(s)" summary is NOT in the redirected
# stdout: with stdout going to a file, the captured text stops after the "Project -> path" lines
# (measured with Start-Process redirection AND with `cmd /c ... > file 2>&1`; `dotnet build` in the
# same shape DOES print its summary, so this is about publish, not about redirection alone). A script
# that parses stdout would therefore read "no warnings found" from an EMPTY summary -- a green light
# that means nothing. The file logger always writes the summary into a file we name, so that is what
# gets parsed, and a missing summary is a FAILURE rather than a pass.
if (Test-Path -LiteralPath $msbuildLog) { Remove-Item -LiteralPath $msbuildLog -Force }
$publishArgv = @('publish', $proj, '-c', $Configuration, '--nologo', '-o', $OutputDir,
                 "-flp:LogFile=$msbuildLog;Verbosity=normal;Encoding=UTF-8")
Say "dotnet $($publishArgv -join ' ')"
$pub = Start-Process -FilePath $dotnetExe -ArgumentList (Quote-Argv $publishArgv) -Wait -PassThru `
                     -WindowStyle Hidden -RedirectStandardOutput $publishLog -RedirectStandardError $publishErr
$pubExit = $pub.ExitCode
$pubText = ''
if (Test-Path -LiteralPath $publishLog) { $pubText = [System.IO.File]::ReadAllText($publishLog) }
$pubErrText = ''
if (Test-Path -LiteralPath $publishErr) { $pubErrText = [System.IO.File]::ReadAllText($publishErr) }
$pubAll = $pubText + "`n" + $pubErrText

if ($pubExit -ne 0) {
    Note (($pubAll -split "`n" | Select-Object -Last 25) -join "`n")
    Die "dotnet publish exited with $pubExit -- see $publishLog" 3
}
if (-not (Test-Path -LiteralPath $msbuildLog)) {
    Die "the MSBuild file log was not written ($msbuildLog) -- cannot verify '0 warnings, 0 errors'" 3
}
$msbuildText = [System.IO.File]::ReadAllText($msbuildLog, [System.Text.Encoding]::UTF8)
$succeeded = $msbuildText -match 'Build succeeded\.'
$warnLine = [regex]::Match($msbuildText, '(?m)^\s*(\d+)\s+Warning\(s\)')
$errLine = [regex]::Match($msbuildText, '(?m)^\s*(\d+)\s+Error\(s\)')
if (-not $succeeded -or -not $warnLine.Success -or -not $errLine.Success) {
    Note ((($msbuildText -split "`n") | Select-Object -Last 15) -join "`n")
    Die "the MSBuild log has no 'Build succeeded' summary with Warning(s)/Error(s) counts -- refusing to report 0/0" 3
}
$warnings = [int]$warnLine.Groups[1].Value
$errors = [int]$errLine.Groups[1].Value
if ($warnings -ne 0 -or $errors -ne 0) {
    Note ((($msbuildText -split "`n") | Where-Object { $_ -match ': warning |: error ' }) -join "`n")
    Die "the publish reported $warnings warning(s) and $errors error(s); the rule is 0 and 0" 3
}
Record "publish: exit=0 warnings=$warnings errors=$errors (Build succeeded; MSBuild file log $msbuildLog)"

# ========================================================================================
# 3. the released folder really carries the three things, at the paths the product uses
# ========================================================================================
Say '--- step 3/5: what is actually in the released folder ---'
$pubExe    = Join-Path $OutputDir 'Confern.exe'
$pubDll    = Join-Path $OutputDir 'Confern.dll'
$pubModel  = Join-Path $OutputDir ("models\" + $modelName)
$pubNotice = Join-Path $OutputDir ("models\" + $noticeName)

if (-not (Test-Path -LiteralPath $pubExe)) { Die "the published folder has no Confern.exe: $pubExe" 4 }
if (-not (Test-Path -LiteralPath $pubDll)) { Die "the published folder has no Confern.dll: $pubDll" 4 }

# The default resolution is AppContext.BaseDirectory\models\model_fp16.onnx. If the model is not at
# exactly this path, the released product reports ModelUnavailable the first time a user presses the
# remove-bg sector -- which is the failure this whole script exists to catch before the user sees it.
if (-not (Test-Path -LiteralPath $pubModel)) {
    Die "the published folder has no model at the DEFAULT path ($pubModel); the product resolves AppContext.BaseDirectory\models\$modelName" 4
}
$pubModelSha = Get-Sha $pubModel
$pubModelLen = Get-Len $pubModel
if ($pubModelLen -ne $modelBytes) {
    Die "the published model has the wrong length: got $pubModelLen, expected $modelBytes ($pubModel)" 4
}
if ($pubModelSha.ToUpperInvariant() -ne $modelSha) {
    Die "the published model has the wrong sha256: got $pubModelSha, expected $modelSha ($pubModel)" 4
}
Record "package: model=$pubModel length=$pubModelLen sha256=$pubModelSha"

# The licence notice. apache-2.0 section 4(a): whoever receives the product must receive a copy of
# the licence. A model that ships without it is not shippable, so this is an assertion and not a note.
if (-not (Test-Path -LiteralPath $pubNotice)) {
    Die "the published folder has no model licence notice at $pubNotice -- apache-2.0 requires it to travel with the product" 4
}
$noticeText = [System.IO.File]::ReadAllText($pubNotice)
$noticeNeeds = @(
    @{ What = 'the licence id (apache-2.0)';        Ok = ($noticeText -match '(?i)apache-2\.0') },
    @{ What = 'the full Apache-2.0 text';           Ok = ($noticeText -match 'Apache License' -and $noticeText -match 'Version 2\.0, January 2004') },
    @{ What = 'the model source (Xenova/modnet)';   Ok = ($noticeText -match [regex]::Escape('Xenova/modnet')) },
    @{ What = 'the model file name';                Ok = ($noticeText -match [regex]::Escape($modelName)) },
    @{ What = "the model length ($modelBytes)";     Ok = ($noticeText -match [regex]::Escape([string]$modelBytes)) },
    @{ What = "the model sha256 ($modelSha)";       Ok = ($noticeText -match [regex]::Escape($modelSha)) }
)
foreach ($n in $noticeNeeds) {
    if (-not $n.Ok) { Die "the licence notice does not name $($n.What): $pubNotice" 4 }
}
Record "package: notice=$pubNotice length=$((Get-Item -LiteralPath $pubNotice).Length) sha256=$((Get-Sha $pubNotice))"
# THE BASE RELEASE MUST NOT CARRY THE OPTIONAL MODEL, and that is ASSERTED rather than assumed. The
# release folder was deleted and republished from scratch a few lines above, so the only way that file
# can be here is that somebody added it to the project's Content items. A release that quietly grew by
# 490 MB would be a silent change to what every single user downloads -- the kind of change nobody
# notices until the download page does.
$pubOptional = Join-Path $OutputDir ("models\" + $optModelName)
if (Test-Path -LiteralPath $pubOptional) {
    Die "the base release carries the OPTIONAL model ($pubOptional, $((Get-Len $pubOptional)) bytes). It must travel as its own pack (-WithOptionalModel): that file is 16x the size of the whole application and every user would pay for it." 4
}
Record "package: optional model absent from the base release=OK ($optModelName travels as its own pack)"

# The released bytes are the freshly built bytes: if the publish had quietly reused something older,
# the model/notice assertions above could still pass while the code under test is stale.
$buildDll = Join-Path $repo "src\Sidecar\bin\$Configuration\net8.0-windows\Confern.dll"
if ($Configuration -eq 'Release' -and (Test-Path -LiteralPath $buildDll)) {
    $bd = Get-Sha $buildDll
    $pd = Get-Sha $pubDll
    if ($bd -ne $pd) {
        Die "the published Confern.dll ($pd) is not the one that was just built ($bd) -- refusing to call this a release" 4
    }
    Record "package: Confern.dll=$pubDll length=$((Get-Len $pubDll)) sha256=$pd (equals the build output)"
} else {
    Record "package: Confern.dll=$pubDll length=$((Get-Len $pubDll)) sha256=$((Get-Sha $pubDll))"
}
$pubFiles = Get-Files $OutputDir
$pubTotal = ($pubFiles | Measure-Object -Property Length -Sum).Sum
Record "package: exe=$pubExe length=$((Get-Len $pubExe)); released folder files=$($pubFiles.Count) bytes=$pubTotal"

# ========================================================================================
# 4. OUT OF THE BOX: run the PUBLISHED exe through the DEFAULT model path
# ========================================================================================
Say '--- step 4/5: the published exe, remove-bg, default model path (no --remove-bg-model) ---'
$e2eDir = Join-Path $LogDir 'e2e'
if (Test-Path -LiteralPath $e2eDir) { Remove-Item -LiteralPath $e2eDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $e2eDir | Out-Null
# The product's own logs go in a SIBLING folder, never beside the source: "how many files did the run
# write next to the source" is one of the assertions, and a log written into that folder would count
# as a written file (measured: it did, on the first run of this script).
$e2eLogDir = Join-Path $LogDir 'e2e-logs'
if (Test-Path -LiteralPath $e2eLogDir) { Remove-Item -LiteralPath $e2eLogDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $e2eLogDir | Out-Null
$e2eSrc = Join-Path $e2eDir (Split-Path -Leaf $Fixture)
Copy-Item -LiteralPath $Fixture -Destination $e2eSrc -Force
$srcSha = Get-Sha $e2eSrc
$srcLen = Get-Len $e2eSrc
$srcMtime = (Get-Item -LiteralPath $e2eSrc).LastWriteTimeUtc

$e2e = Invoke-Product $pubExe @('--remove-bg', $e2eSrc) (Join-Path $e2eLogDir 'remove-bg.log')
$modelLine = Get-Line $e2e.Lines 'tool remove-bg: model path='
$wroteLine = Get-Line $e2e.Lines 'remove-bg wrote: '
$outputLine = Get-Line $e2e.Lines 'remove-bg output: '
$failLine = Get-Line $e2e.Lines 'remove-bg failure: '

if ($e2e.Exit -ne 0) {
    Note "model line   : $modelLine"
    Note "failure line : $failLine"
    Die "the published exe exited with $($e2e.Exit) on a plain --remove-bg (default model path); expected 0" 5
}
$defaultOk = ($modelLine -match [regex]::Escape($pubModel)) -and
             ($modelLine -match 'exists=True') -and
             ($modelLine -match '\(default beside the exe\)')
if (-not $defaultOk) {
    Note "model line: $modelLine"
    Die "the run did not go through the default path '$pubModel' (see the model line above)" 5
}

$newFiles = @(Get-Files $e2eDir | Where-Object { $_.FullName -ne $e2eSrc })
if ($newFiles.Count -ne 1) {
    Die "expected exactly one written PNG beside the source, found $($newFiles.Count) extra file(s): $($newFiles.Name -join ',')" 5
}
$outPng = $newFiles[0].FullName
$magic = [System.IO.File]::ReadAllBytes($outPng)[0..7]
$sig = ($magic -join ',') -eq '137,80,78,71,13,10,26,10'
if (-not $sig) { Die "the written file is not a PNG (signature bytes $($magic -join ',')): $outPng" 5 }

# The alpha is measured by the PRODUCT'S OWN inspector, so the assertion is about the bytes on disk
# and not about the producer's opinion of them.
$inspect = Invoke-Product $pubExe @('--remove-bg-inspect', $outPng) (Join-Path $e2eLogDir 'inspect.log')
$alphaLine = Get-Line $inspect.Lines 'remove-bg inspect: alpha '
$headLine = Get-Line $inspect.Lines 'remove-bg inspect: file='
$m = [regex]::Match([string]$alphaLine, 'min=([\d.]+) max=([\d.]+) mean=([\d.]+) opaque=([\d.]+) clear=([\d.]+)')
if (-not $m.Success) {
    Note "head : $headLine"
    Note "alpha: $alphaLine"
    Die 'could not read the alpha statistics out of the inspector (expected min=/max=/mean=/opaque=/clear=)' 5
}
$minA = [double]$m.Groups[1].Value
$maxA = [double]$m.Groups[2].Value
$clearA = [double]$m.Groups[5].Value
$hasAlpha = ($headLine -match 'hasAlpha=True')
if (-not $hasAlpha) { Die "the written PNG carries no alpha channel: $headLine" 5 }
if (-not (($minA -eq 0.0) -and ($maxA -eq 1.0))) {
    Die "the alpha channel is not the full range a real cutout needs: min=$minA max=$maxA (expected 0.0000 and 1.0000)" 5
}
if ($clearA -lt 0.20) {
    Die "only $clearA of the pixels are fully transparent (expected >= 0.20): the background was not removed" 5
}
$sizeOk = ($outputLine -match 'size=512x410') -and ($headLine -match 'size=512x410')
if (-not $sizeOk) { Die "the output is not the source's own size: outputLine='$outputLine' head='$headLine'" 5 }

# Spec 5: the tool never modifies its source, and the default path does not change that.
$srcUntouched = ((Get-Sha $e2eSrc) -eq $srcSha) -and ((Get-Len $e2eSrc) -eq $srcLen) -and
                ((Get-Item -LiteralPath $e2eSrc).LastWriteTimeUtc -eq $srcMtime)
if (-not $srcUntouched) { Die 'the source file was modified by the run (spec 5 violation)' 5 }

Record "out of the box: exit=0 defaultPath='$pubModel' wrote='$wroteLine'"
Record "out of the box: output=$(Split-Path -Leaf $outPng) bytes=$((Get-Len $outPng)) $headLine"
Record "out of the box: alpha min=$minA max=$maxA clear=$clearA (measured by --remove-bg-inspect on the artefact)"
Record "out of the box: source untouched=$srcUntouched"

# ========================================================================================
# 5. NEGATIVE: the same command on a copy WITHOUT the model must be a NAMED state, not a crash
# ========================================================================================
if ($SkipNegative) {
    Say '--- step 5/5: SKIPPED on request (-SkipNegative) ---'
    Record 'negative drill: SKIPPED (-SkipNegative) -- this run did NOT prove the ModelUnavailable behaviour'
} else {
    Say '--- step 5/5: negative drill on a temporary copy (repo untouched) ---'
    # Done on a COPY on purpose: the released folder is the artefact under test and must not be
    # modified, and the repository must not be touched at all.
    $negRoot = Join-Path $LogDir 'negative'
    if (Test-Path -LiteralPath $negRoot) { Remove-Item -LiteralPath $negRoot -Recurse -Force }
    $negApp = Join-Path $negRoot 'app'
    New-Item -ItemType Directory -Force -Path $negRoot | Out-Null
    # robocopy: /XD skips the publish subfolder a build output can contain (it is another full copy of
    # the same app and would only double the copy cost). Exit codes 0-7 mean "copied something".
    $rc = Start-Process -FilePath 'robocopy' -ArgumentList (Quote-Argv @($OutputDir, $negApp, '/E', '/XD', (Join-Path $OutputDir 'publish'), '/NFL', '/NDL', '/NJH', '/NJS', '/NP')) -Wait -PassThru -WindowStyle Hidden
    if ($rc.ExitCode -gt 7) { Die "could not copy the released folder for the negative drill (robocopy exit $($rc.ExitCode))" 6 }
    $negExe = Join-Path $negApp 'Confern.exe'
    $negModel = Join-Path $negApp ("models\" + $modelName)
    if (-not (Test-Path -LiteralPath $negExe)) { Die "the negative copy has no Confern.exe: $negApp" 6 }
    if (-not (Test-Path -LiteralPath $negModel)) {
        Die "the negative copy has no model to remove: $negModel (the copy is not a copy of the released folder)" 6
    }
    Remove-Item -LiteralPath $negModel -Force

    $negSrcDir = Join-Path $negRoot 'src'
    New-Item -ItemType Directory -Force -Path $negSrcDir | Out-Null
    $negSrc = Join-Path $negSrcDir (Split-Path -Leaf $Fixture)
    Copy-Item -LiteralPath $Fixture -Destination $negSrc -Force
    $negSrcSha = Get-Sha $negSrc

    $neg = Invoke-Product $negExe @('--remove-bg', $negSrc) (Join-Path $negRoot 'remove-bg-nomodel.log')
    $negModelLine = Get-Line $neg.Lines 'tool remove-bg: model path='
    $negFailLine = Get-Line $neg.Lines 'remove-bg failure: '
    $negWhyLine = Get-Line $neg.Lines 'remove-bg why: '
    $negWroteLine = Get-Line $neg.Lines 'remove-bg wrote: '
    $negNamed = (Get-Line $neg.Lines 'error=ModelUnavailable') -ne ''
    $negExtras = @(Get-Files $negSrcDir | Where-Object { $_.FullName -ne $negSrc })

    Note "exit=$($neg.Exit) (0x$('{0:X8}' -f $neg.Exit)) model='$negModelLine'"
    Note "failure='$negFailLine'"
    Note "why='$negWhyLine' wrote='$negWroteLine' extraFiles=$($negExtras.Count)"

    # A crash would show up as a CLR exit code (0xE0434352 = -532462766 in .NET terms) or as a missing
    # worker result line, so the exit code is asserted exactly and the NAMED state is asserted by name.
    if ($neg.Exit -ne 3) { Die "the negative drill exited with $($neg.Exit), expected exactly 3 (the named refusal code)" 6 }
    if (-not $negNamed) { Die 'the negative drill did not report the named state ModelUnavailable (no "error=ModelUnavailable" line)' 6 }
    if ($negExtras.Count -ne 0) { Die "the negative drill wrote $($negExtras.Count) file(s): $($negExtras.Name -join ',')" 6 }
    if ($negWroteLine -notmatch 'count=0') { Die "the negative drill's own directory report is not count=0: '$negWroteLine'" 6 }
    if ($negModelLine -notmatch [regex]::Escape($negModel)) {
        Die "the default path in the copy is not the COPY's own folder: '$negModelLine' (expected $negModel)" 6
    }
    if ($negModelLine -notmatch 'exists=False') { Die "the copy's model line does not say exists=False: '$negModelLine'" 6 }
    if ((Get-Sha $negSrc) -ne $negSrcSha) { Die 'the negative drill modified its source file (spec 5 violation)' 6 }

    # The released folder itself must be exactly as it was: the drill ran on a copy.
    $afterSha = Get-Sha $pubModel
    if ($afterSha -ne $pubModelSha) {
        Die "the released model changed during the negative drill ($pubModelSha -> $afterSha)" 6
    }
    Record "negative: exit=$($neg.Exit) error=ModelUnavailable defaultPath='$negModel' wrote='$negWroteLine' extraFiles=$($negExtras.Count)"
    Record "negative: the released folder was not touched by the drill (model sha256 still $afterSha)"
}

# ========================================================================================
# ========================================================================================
# 6. OPTIONAL: the high-quality model pack (BiRefNet), on request only
# ========================================================================================
# WHY IT IS A SEPARATE PACK, AND WHY NO PRODUCT CODE HAD TO CHANGE
#   BiRefNet is measurably better than MODNet on the same photograph, and it is also 489,666,272
#   bytes against 12,984,781 -- sixteen times the size of the whole published application. Putting it
#   into the base download would multiply what every single user downloads, for a feature they may
#   never press. It travels as its own folder instead.
#
#   RemoveBgModel.ResolvePath resolves the default model as a PREFERENCE ORDER: if
#   <exe folder>\models\ModelBiRefNet_1024x1024_fp16.onnx exists AND its length is exactly the measured
#   value, it is used; otherwise MODNet. So "copy the pack's files into the installed models\ folder"
#   is the ENTIRE integration: no code change, no setting, no installer logic. The pack is therefore
#   deliberately the same shape as the release's own models\ folder.
#
#   This step does not take that on faith. It copies the RELEASE to a temporary folder, drops the pack
#   into it, and runs the released exe through its default path -- asserting that the run really went
#   through the OPTIONAL model and that it did NOT produce the same bytes the base release produced.
#   "The pack exists" and "the pack upgrades an installed copy" are different claims, and only the
#   second one is worth shipping.
if (-not $WithOptionalModel) {
    Say '--- step 6/6: SKIPPED (no -WithOptionalModel) ---'
    Record 'optional pack: SKIPPED -- the base release above is complete on its own, and this run did not build the second folder'
} else {
    Say '--- step 6/6: the optional high-quality model pack ---'
    if (-not (Test-Path -LiteralPath $fetchOpt)) {
        Die "the optional model fetch script is missing: $fetchOpt" 7
    }
    if (-not (Test-Path -LiteralPath $optNotice)) {
        Die "the optional model's licence notice is missing from the working tree: $optNotice -- nothing to ship beside the model" 7
    }
    $trackedOpt = @(& git -C $repo ls-files -- 'models/ModelBiRefNet_1024x1024_fp16.onnx' 2>&1)
    if ($LASTEXITCODE -ne 0) { Die 'git ls-files failed for the optional model path' 7 }
    if ($trackedOpt.Count -ne 0) {
        Die "the optional model IS in version control ($($trackedOpt -join ',')) -- a 490 MB binary must not be" 7
    }
    $trackedOptNotice = @(& git -C $repo ls-files -- 'models/MODEL-BIREFNET-LICENSE.txt' 2>&1)
    if ($trackedOptNotice.Count -eq 0) {
        Die 'models/MODEL-BIREFNET-LICENSE.txt is NOT in version control -- the licence notice has to be part of the source tree' 7
    }
    Record "optional pack: git model untracked=OK, notice tracked=$($trackedOptNotice.Count) file(s)"

    $fetchOptArgv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fetchOpt, '-Destination', $optStore)
    if ($OptionalModelSource -ne '') {
        if (-not (Test-Path -LiteralPath $OptionalModelSource)) { Die "OptionalModelSource not found: $OptionalModelSource" 7 }
        $fetchOptArgv += @('-ExistingPath', $OptionalModelSource)
        Say "obtaining the optional model from a local file (offline path): $OptionalModelSource"
    } else {
        Say 'obtaining the optional model (downloads only when it is not already present and correct)'
    }
    $optExit = 1
    $prevOpt = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $optLines = @(& powershell.exe @fetchOptArgv 2>&1 | ForEach-Object { [string]$_ })
        $optExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prevOpt }
    foreach ($l in $optLines) { Note $l }
    if ($optExit -ne 0) { Die "the optional model was NOT obtained (fetch script exit $optExit)" 7 }

    $optStoreLen = Get-Len $optStore
    $optStoreSha = Get-Sha $optStore
    if ($optStoreLen -ne $optModelBytes) {
        Die "the optional model store has the wrong length: got $optStoreLen, expected $optModelBytes ($optStore)" 7
    }
    if ($optStoreSha.ToUpperInvariant() -ne $optModelSha) {
        Die "the optional model store has the wrong sha256: got $optStoreSha, expected $optModelSha ($optStore)" 7
    }

    # The pack folder. Same guard as the release folder: only delete something that is a previous pack.
    if (Test-Path -LiteralPath $OptionalDir) {
        $packLooksOurs = (Test-Path -LiteralPath (Join-Path $OptionalDir 'models\README.txt')) -or
                         (Test-Path -LiteralPath (Join-Path $OptionalDir 'models\' + $optModelName))
        if (-not $packLooksOurs) {
            Die "the pack folder exists and holds no previous pack: $OptionalDir -- refusing to delete it" 7
        }
        Say "clearing the previous pack: $OptionalDir"
        Remove-Item -LiteralPath $OptionalDir -Recurse -Force
    }
    $packModels = Join-Path $OptionalDir 'models'
    New-Item -ItemType Directory -Force -Path $packModels | Out-Null
    Copy-Item -LiteralPath $optStore -Destination (Join-Path $packModels $optModelName) -Force
    Copy-Item -LiteralPath $optNotice -Destination (Join-Path $packModels $optNoticeName) -Force

    # The instruction the user follows. Plain ASCII on purpose: this file travels outside the repository
    # and must survive any editor on any machine (see the note at the top of this script about BOMs).
    $readmeLines = @(
        'Confern -- optional high-quality background removal model',
        '==========================================================',
        '',
        'WHAT THIS IS',
        '  BiRefNet, a portrait segmentation model that is visibly cleaner than the one',
        '  built into Confern. Measured on one 512x410 photograph, for the same input:',
        '    semi-transparent pixels (the soft edge)      7,624  ->  3,144',
        '    background actually removed                96,347 px -> 103,410 px',
        '    a 5,272 px strip of background left along one edge  ->  removed',
        '    subject pixels wrongly removed                --   ->  292 px',
        '',
        '  It is a separate download because it is 489,666,272 bytes -- sixteen times the',
        '  size of the whole application.',
        '',
        'HOW TO INSTALL',
        '  1. Close Confern if it is running.',
        '  2. Copy the file inside this folder''s models directory into the models',
        '     directory next to Confern.exe (create it if it is not there yet).',
        '  3. Start Confern again. Background removal now uses this model.',
        '',
        '  Nothing else changes and no setting has to be touched: the product looks for a',
        '  model beside the executable and prefers this one whenever it is present at its',
        '  full length. To go back to the model that came with Confern, delete the file',
        '  again -- CUTOUT keeps working, with the older model.',
        '',
        'FILE',
        '  models\ModelBiRefNet_1024x1024_fp16.onnx',
        '  length : 489666272 bytes',
        '  sha256 : 3654C741EB80BD926ADA8FED1713B506CCF8D30EB1F6487E87EB9F234F33DF09',
        '  source : https://huggingface.co/onnx-community/BiRefNet-ONNX  (onnx/model_fp16.onnx)',
        '  licence: MIT -- the full text is in models\MODEL-BIREFNET-LICENSE.txt in this folder',
        '',
        'TO VERIFY THE DOWNLOAD',
        '  Get-FileHash .\models\ModelBiRefNet_1024x1024_fp16.onnx -Algorithm SHA256',
        '  The hash printed must equal the sha256 above. If it does not, the file is not',
        '  the model this pack was built and measured against.'
    )
    $readmePath = Join-Path $packModels 'README.txt'
    [System.IO.File]::WriteAllText($readmePath, ($readmeLines -join "`r`n"), (New-Object System.Text.ASCIIEncoding))

    # ---- the pack itself, asserted ----
    $packModel  = Join-Path $packModels $optModelName
    $packNotice = Join-Path $packModels $optNoticeName
    foreach ($need in @($packModel, $packNotice, $readmePath)) {
        if (-not (Test-Path -LiteralPath $need)) { Die "the pack is incomplete, missing $need" 7 }
    }
    $packLen = Get-Len $packModel
    $packSha = Get-Sha $packModel
    if ($packLen -ne $optModelBytes) { Die "the packed model has the wrong length: got $packLen, expected $optModelBytes" 7 }
    if ($packSha.ToUpperInvariant() -ne $optModelSha) { Die "the packed model has the wrong sha256: got $packSha, expected $optModelSha" 7 }
    $packNoticeText = [System.IO.File]::ReadAllText($packNotice)
    foreach ($n in @(
        @{ What = 'the licence id (MIT)';            Ok = ($packNoticeText -match '(?i)\bMIT\b') },
        @{ What = 'the MIT text itself';             Ok = ($packNoticeText -match 'Permission is hereby granted') },
        @{ What = 'the model source repository';     Ok = ($packNoticeText -match [regex]::Escape('onnx-community/BiRefNet-ONNX')) },
        @{ What = 'the upstream repository';         Ok = ($packNoticeText -match [regex]::Escape('ZhengPeng7/BiRefNet')) },
        @{ What = 'the model file name';             Ok = ($packNoticeText -match [regex]::Escape($optModelName)) },
        @{ What = "the model length ($optModelBytes)"; Ok = ($packNoticeText -match [regex]::Escape([string]$optModelBytes)) },
        @{ What = "the model sha256 ($optModelSha)"; Ok = ($packNoticeText -match [regex]::Escape($optModelSha)) },
        @{ What = 'the README it travels with';      Ok = ([System.IO.File]::ReadAllText($readmePath) -match [regex]::Escape($optModelSha)) })) {
        if (-not $n.Ok) { Die "the pack does not name $($n.What)" 7 }
    }
    Record "optional pack: model=$packModel length=$packLen sha256=$packSha notice=$packNotice readme=$readmePath"

    # ---- and now the claim that matters: the pack UPGRADES a copy of the release ----
    $packRoot = Join-Path $LogDir 'packtest'
    if (Test-Path -LiteralPath $packRoot) { Remove-Item -LiteralPath $packRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $packRoot | Out-Null
    $packApp = Join-Path $packRoot 'app'
    $rcPack = Start-Process -FilePath 'robocopy' -ArgumentList (Quote-Argv @($OutputDir, $packApp, '/E', '/XD', (Join-Path $OutputDir 'publish'), '/NFL', '/NDL', '/NJH', '/NJS', '/NP')) -Wait -PassThru -WindowStyle Hidden
    if ($rcPack.ExitCode -gt 7) { Die "could not copy the release for the pack drill (robocopy exit $($rcPack.ExitCode))" 7 }
    $packAppExe = Join-Path $packApp 'Confern.exe'
    if (-not (Test-Path -LiteralPath $packAppExe)) { Die "the pack drill copy has no Confern.exe: $packApp" 7 }
    # Installing the pack = copying its models\ contents in. That is the whole procedure, and doing it
    # this way means the drill tests the documented instruction rather than a shortcut of it.
    Copy-Item -LiteralPath $packModel  -Destination (Join-Path $packApp ('models\' + $optModelName))  -Force
    Copy-Item -LiteralPath $packNotice -Destination (Join-Path $packApp ('models\' + $optNoticeName)) -Force

    $packSrcDir = Join-Path $packRoot 'src'
    New-Item -ItemType Directory -Force -Path $packSrcDir | Out-Null
    $packSrc = Join-Path $packSrcDir (Split-Path -Leaf $Fixture)
    Copy-Item -LiteralPath $Fixture -Destination $packSrc -Force
    $packSrcSha = Get-Sha $packSrc

    $packRun = Invoke-Product $packAppExe @('--remove-bg', $packSrc) (Join-Path $packRoot 'remove-bg-pack.log')
    $packModelLine = Get-Line $packRun.Lines 'tool remove-bg: model path='
    $packFailLine  = Get-Line $packRun.Lines 'remove-bg failure: '
    $packExtras = @(Get-Files $packSrcDir | Where-Object { $_.FullName -ne $packSrc })

    Note "exit=$($packRun.Exit) model='$packModelLine'"
    Note "failure='$packFailLine' written=$($packExtras.Count)"
    if ($packRun.Exit -ne 0) {
        Die "the released exe with the pack installed exited with $($packRun.Exit); a pack that breaks the product is not a pack" 7
    }
    $expectedPackModel = Join-Path $packApp ('models\' + $optModelName)
    if ($packModelLine -notmatch [regex]::Escape($expectedPackModel)) {
        Die "with the pack installed the run did not go through the OPTIONAL model: '$packModelLine' (expected $expectedPackModel)" 7
    }
    if ($packModelLine -match 'exists=False') { Die "the pack's model was not found even though it was just copied in: '$packModelLine'" 7 }
    if ($packExtras.Count -ne 1) {
        Die "expected exactly one written PNG beside the source in the pack drill, found $($packExtras.Count)" 7
    }
    $packOutSha = Get-Sha $packExtras[0].FullName
    $baseOutSha = Get-Sha $outPng
    if ($packOutSha -eq $baseOutSha) {
        Die "the pack's output is byte-identical to the base release's output ($baseOutSha). That would mean the preference did NOT change which engine ran, and this pack would be 490 MB of nothing." 7
    }
    $packInspect = Invoke-Product $packAppExe @('--remove-bg-inspect', $packExtras[0].FullName) (Join-Path $packRoot 'inspect-pack.log')
    $packAlpha = Get-Line $packInspect.Lines 'remove-bg inspect: alpha '
    if ($packAlpha -notmatch 'min=0\.0000 max=1\.0000') {
        Die "the pack's output is not a full-range cutout: '$packAlpha'" 7
    }
    if ((Get-Sha $packSrc) -ne $packSrcSha) { Die 'the pack drill modified its source file (spec 5 violation)' 7 }
    Record "optional pack: INSTALLED-PROOF exit=0 defaultPath='$expectedPackModel' output=$((Split-Path -Leaf $packExtras[0].FullName)) sha256=$packOutSha"
    Record "optional pack: that output differs from the base release's ($baseOutSha) -- the preference really changed the engine that ran"
    Record "optional pack: alpha '$packAlpha'"
    Record "optional pack: folder=$OptionalDir (give the user that folder; models\ is what they copy)"
}
# summary
# ========================================================================================
$sw.Stop()
$reportPath = Join-Path $LogDir 'publish-report.txt'
$header = @(
    "publish-confern.ps1 report -- $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "configuration=$Configuration output=$OutputDir",
    "model source=$modelRepoUrl licence=$modelLicenseId",
    "elapsed=$([math]::Round($sw.Elapsed.TotalSeconds,1)) s",
    ''
)
($header + $report) | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ''
Say "RELEASED: $OutputDir"
Say "report: $reportPath"
Write-Host ''
foreach ($line in $report) { Write-Host "  $line" }
exit 0
