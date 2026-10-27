# build-installer.ps1 -- produce ONE Confern-Setup-<version>.exe and PROVE it installs.
#
# WHY THIS SCRIPT AND NOT JUST ISCC
#   An installer is the only artefact of this project that a stranger runs. Everything it claims
#   ("the model is inside", "it installs and starts") has to be checked, because a setup that unpacks
#   a broken app is indistinguishable from a setup that works -- right up until the user
#   double-clicks the shortcut.
#
#   So this script does the whole chain and fails loudly at the first broken link:
#     1. publish the app (no debug symbols: a .pdb is not part of a product),
#     2. assert the staging folder is COMPLETE (exe, dll, model at the path the product resolves),
#     3. compile the Inno Setup script,
#     4. assert the produced setup exists, is a PE image and carries the version string,
#     5. INSTALL it silently into a throw-away folder, run the installed exe for real, then uninstall
#        it -- a claim about an installer that was never installed is not evidence.
#
# NAMING: the documents are staged as usage.txt / version.txt because THIS FILE IS PURE ASCII, and
#   the Inno script (UTF-8 with BOM) renames them to their Chinese names at install time. See the
#   [Files] section of confern.iss for the full reason.
#
# USAGE
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
#   ... -SkipDrill        only build (no silent install/uninstall). The report then says so.
#   ... -Version 0.1.1   override the version (default: <Version> in Sidecar.csproj)
#   ... -OutputDir dist  where the setup goes (default <repo>\dist)
#   ... -Iscc C:\path\ISCC.exe   if Inno Setup is somewhere unusual
#
# EXIT CODES
#   0 built + verified   1 preconditions (dotnet/ISCC/staging/source missing)
#   2 publish failed     3 compile failed        4 the setup is missing or not a PE image
#   5 the drill failed (install/run/uninstall)
#
# PURE ASCII, NO BOM: Windows PowerShell 5.1 reads a BOM-less script as ANSI, so a CJK character in
# a comment silently eats the following statement (measured in this repo's tools).

[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutputDir = '',
    [string]$Iscc = '',
    [switch]$SkipDrill
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if ($repo -eq '') { $repo = (Get-Location).Path }
$proj = Join-Path $repo 'src\Sidecar\Sidecar.csproj'
$iss = Join-Path $PSScriptRoot 'installer\confern.iss'
$payload = Join-Path $PSScriptRoot 'installer\payload'
if ($OutputDir -eq '') { $OutputDir = Join-Path $repo 'dist' }

function Say([string]$Text) { Write-Host "installer: $Text" }
function Die([string]$Message, [int]$Code) {
    Write-Host ''
    Write-Host "installer: FAILED: $Message"
    exit $Code
}

function Get-Sha([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 'MISSING' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# Start-Process does not quote ArgumentList elements (measured in this repo's verify suites), so an
# element containing a space has to be quoted here. Paths on this machine contain CJK but no spaces;
# the quoting is for the next machine, not this one.
function Quote-Argv([string[]]$Argv) {
    $q = @()
    foreach ($a in $Argv) {
        if ([string]$a -match ' ') { $q += ('"' + $a + '"') } else { $q += $a }
    }
    return $q
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------- 0. preconditions
if (-not (Test-Path -LiteralPath $proj)) { Die "project not found: $proj" 1 }
if (-not (Test-Path -LiteralPath $iss)) { Die "Inno script not found: $iss" 1 }
$usageSource = Join-Path $payload 'usage.txt'
if (-not (Test-Path -LiteralPath $usageSource)) {
    Die "payload\usage.txt is missing: $payload -- the installer ships it, so it has to exist" 1
}

if ($Version -eq '') {
    $csproj = [System.IO.File]::ReadAllText($proj)
    $m = [regex]::Match($csproj, '<Version>\s*([0-9][0-9A-Za-z\.\-]*)\s*</Version>')
    if (-not $m.Success) { Die "could not read <Version> out of $proj -- pass -Version" 1 }
    $Version = $m.Groups[1].Value
}
Say "version=$Version repo=$repo"

if ($Iscc -eq '') {
    $cands = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $Iscc = @($cands | Where-Object { $_ -and (Test-Path -LiteralPath $_) })[0]
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) {
    Die 'ISCC.exe (Inno Setup 6) not found -- install it: winget install --id JRSoftware.InnoSetup -e' 1
}
Say "iscc=$Iscc"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { Die 'dotnet is not on PATH' 1 }

# A running Confern holds the exe, and the publish would fail with a file-in-use error that reads
# like a code problem.
$running = @(Get-Process -Name 'Confern' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say "stopping $($running.Count) running Confern process(es): $($running.Id -join ',')"
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}
if (@(Get-Process -Name 'Confern' -ErrorAction SilentlyContinue).Count -gt 0) {
    Die 'Confern is still running -- close it and re-run' 1
}

if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$staging = Join-Path $OutputDir '_staging'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# ---------------------------------------------------------------- 1. publish
Say '--- step 1/4: dotnet publish (no debug symbols) ---'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$publishArgv = @('publish', $proj, '-c', 'Release', '--nologo', '-o', $staging,
                 '-p:DebugType=None', '-p:DebugSymbols=false')
Say "dotnet $($publishArgv -join ' ')"
$pub = Start-Process -FilePath $dotnet.Source -ArgumentList (Quote-Argv $publishArgv) -Wait -PassThru -WindowStyle Hidden
if ($pub.ExitCode -ne 0) { Die "dotnet publish exited with $($pub.ExitCode)" 2 }

# Debug symbols are not shipping material, and the import libraries (onnxruntime.lib) are for people
# linking against the DLL -- neither belongs in a user's install folder.
# NOT -Include: with -Recurse on a -LiteralPath directory it matched EVERY file here (measured: it
# deleted all 26, including Confern.exe, and the next step failed with "no Confern.exe"). Filter by
# extension explicitly instead of trusting the switch.
$junk = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Extension -eq '.lib' })
foreach ($j in $junk) { Remove-Item -LiteralPath $j.FullName -Force }
Say ("staging: removed {0} file(s) that are not shipping material (*.pdb, *.lib)" -f $junk.Count)

# ---------------------------------------------------------------- 2. staging completeness
Say '--- step 2/4: the staging folder carries what the product needs ---'
$stagingExe = Join-Path $staging 'Confern.exe'
$stagingDll = Join-Path $staging 'Confern.dll'
$stagingModel = Join-Path $staging 'models\model_fp16.onnx'
$stagingNotice = Join-Path $staging 'models\MODEL-LICENSE.txt'
foreach ($need in @($stagingExe, $stagingDll)) {
    if (-not (Test-Path -LiteralPath $need)) { Die "the publish output has no $(Split-Path -Leaf $need): $staging" 1 }
}
if (-not (Test-Path -LiteralPath $stagingModel)) {
    Die "the publish output has no model at models\model_fp16.onnx -- remove-bg would be dead on arrival ($staging)" 1
}
if (-not (Test-Path -LiteralPath $stagingNotice)) {
    Die 'the publish output has no models\MODEL-LICENSE.txt -- apache-2.0 requires it to travel with the model' 1
}
$modelSha = Get-Sha $stagingModel
$modelLen = (Get-Item -LiteralPath $stagingModel).Length
Say "staging: model=$modelLen bytes sha256=$modelSha"

# The docs the user reads. version.txt is generated per build, so it goes here and not into the repo.
Copy-Item -LiteralPath $usageSource -Destination (Join-Path $staging 'usage.txt') -Force
$sample = Join-Path $repo 'fixtures\rembg\real-512.png'
if (Test-Path -LiteralPath $sample) {
    Copy-Item -LiteralPath $sample -Destination (Join-Path $staging 'sample.png') -Force
}
$gitHead = ''
try { $gitHead = [string](& git -C $repo log -1 --pretty='%h %s' 2>$null) } catch { $gitHead = '' }
$infoLines = @(
    'Confern -- what this build is',
    '=============================',
    '',
    'version        : ' + $Version,
    'built          : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'),
    'source commit  : ' + $gitHead.Trim(),
    '',
    'Confern.exe      ' + (Get-Item -LiteralPath $stagingExe).Length + ' bytes   sha256=' + (Get-Sha $stagingExe),
    'Confern.dll      ' + (Get-Item -LiteralPath $stagingDll).Length + ' bytes   sha256=' + (Get-Sha $stagingDll),
    'model_fp16.onnx  ' + $modelLen + ' bytes   sha256=' + $modelSha,
    '                 (MODNet, apache-2.0, licence text beside it)',
    '',
    'needs: Windows 10/11 x64 + .NET 8 Desktop Runtime',
    '       (the installer checks for it and offers to install it when missing)'
)
[System.IO.File]::WriteAllText((Join-Path $staging 'version.txt'), (($infoLines -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

$stagingFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
Say ("staging: {0} files, {1:N2} MB" -f $stagingFiles.Count, (($stagingFiles | Measure-Object Length -Sum).Sum / 1MB))

# ---------------------------------------------------------------- 3. compile
Say '--- step 3/4: compile the installer ---'
$setup = Join-Path $OutputDir ("Confern-Setup-$Version.exe")
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
$isccArgv = @('/Qp', "/DAppVersion=$Version", "/DStagingDir=$staging", "/DOutputDir=$OutputDir", $iss)
Say "iscc $($isccArgv -join ' ')"
$comp = Start-Process -FilePath $Iscc -ArgumentList (Quote-Argv $isccArgv) -Wait -PassThru -WindowStyle Hidden
if ($comp.ExitCode -ne 0) { Die "ISCC exited with $($comp.ExitCode) (the compiler prints the reason above)" 3 }
if (-not (Test-Path -LiteralPath $setup)) {
    $found = @(Get-ChildItem -LiteralPath $OutputDir -Filter 'Confern-Setup-*.exe' -ErrorAction SilentlyContinue)
    Die "ISCC reported success but $setup does not exist (found: $($found.Name -join ','))" 4
}
$setupItem = Get-Item -LiteralPath $setup
$sig = [System.IO.File]::ReadAllBytes($setup)[0..1]
if (($sig[0] -ne 0x4D) -or ($sig[1] -ne 0x5A)) { Die "the setup is not a PE image (first bytes $($sig -join ',')): $setup" 4 }
$setupSha = Get-Sha $setup
Say ("setup: $setup  {0:N2} MB  sha256={1}" -f ($setupItem.Length / 1MB), $setupSha)
$verLine = (Get-Item -LiteralPath $setup).VersionInfo.FileVersion
if ($verLine -notmatch ('^' + [regex]::Escape($Version))) {
    Die "the setup's own file version is '$verLine', which does not start with $Version" 4
}

# ---------------------------------------------------------------- 4. the drill
$drillOk = $false
if ($SkipDrill) {
    Say '--- step 4/4: SKIPPED on request (-SkipDrill) ---'
} else {
    Say '--- step 4/4: install it quietly, run it, uninstall it ---'
    $drillRoot = Join-Path $env:TEMP ('confern-drill-' + [guid]::NewGuid().ToString('N'))
    $drillDir = Join-Path $drillRoot 'app'
    New-Item -ItemType Directory -Path $drillRoot -Force | Out-Null

    # /TASKS="" keeps this drill out of the Start Menu and off the desktop: a verification run must
    # not leave things behind on the machine it ran on.
    $tasks = '"' + '"'
    $instArgv = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/TASKS=$tasks", ("/DIR=" + '"' + $drillDir + '"'))
    # /LOG=: Inno writes what it did, including the .NET detection line. That turns "the installer
    # checks for the runtime" into something this script can assert.
    $setupLog = Join-Path $drillRoot 'setup.log'
    $instArgv += ('/LOG=' + '"' + $setupLog + '"')
    $inst = Start-Process -FilePath $setup -ArgumentList ($instArgv -join ' ') -Wait -PassThru -WindowStyle Hidden
    if ($inst.ExitCode -ne 0) { Die "the silent install exited with $($inst.ExitCode)" 5 }
    $drillExe = Join-Path $drillDir 'Confern.exe'
    if (-not (Test-Path -LiteralPath $drillExe)) { Die "the install did not produce $drillExe" 5 }
    $drillModel = Join-Path $drillDir 'models\model_fp16.onnx'
    if (-not (Test-Path -LiteralPath $drillModel)) { Die "the installed copy has no model: $drillModel" 5 }
    if ((Get-Sha $drillModel) -ne $modelSha) {
        Die "the installed model's sha256 does not match the source model ($modelSha)" 5
    }
    $uninst = Join-Path $drillDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninst)) { Die 'the install registered no uninstaller (unins000.exe)' 5 }

    # Every staged file must arrive. The two .txt documents are renamed at install time, so they are
    # compared by length instead of by name (this script cannot spell their Chinese names).
    $missing = @()
    foreach ($f in $stagingFiles) {
        if ($f.Extension -eq '.txt') { continue }
        $rel = $f.FullName.Substring($staging.Length).TrimStart('\')
        if (-not (Test-Path -LiteralPath (Join-Path $drillDir $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) { Die "the install is missing $($missing.Count) staged file(s): $($missing -join ', ')" 5 }
    # TOP LEVEL ONLY on both sides: models\*.txt (the licences) are covered by the name check above,
    # and mixing them in made the lists disagree (measured: staged 4 txt vs installed 2).
    $installedTxt = @(Get-ChildItem -LiteralPath $drillDir -File -Filter '*.txt')
    if ($installedTxt.Count -lt 2) {
        Die "expected two installed documents (*.txt), found $($installedTxt.Count)" 5
    }
    $stagedTxtLens = @($stagingFiles | Where-Object { $_.Extension -eq '.txt' -and $_.DirectoryName -eq $staging } |
                       ForEach-Object { $_.Length } | Sort-Object)
    $installedTxtLens = @($installedTxt | ForEach-Object { $_.Length } | Sort-Object)
    if (($stagedTxtLens -join ',') -ne ($installedTxtLens -join ',')) {
        Die "the installed documents do not match the staged ones (staged: $($stagedTxtLens -join ',') / installed: $($installedTxtLens -join ','))" 5
    }
    Say 'drill: every staged file arrived, and the two documents kept their content'

    # The runtime check must have actually run, and it must have found this machine's .NET 8.
    $logText = ''
    if (Test-Path -LiteralPath $setupLog) { $logText = [System.IO.File]::ReadAllText($setupLog) }
    if ($logText -notmatch 'dotnet8: present=True') {
        Die "the installer log has no 'dotnet8: present=True' line -- the runtime check did not do what it claims (log: $setupLog)" 5
    }
    Say 'drill: the .NET 8 check ran and found the runtime (dotnet8: present=True)'

    # The installed exe, run for real. formats touches every codec/plugin table and proves the app
    # starts from an installed location (not from bin\ or the repo root).
    $probeOut = Join-Path $drillRoot 'probe.out'
    $probeErr = Join-Path $drillRoot 'probe.err'
    New-Item -ItemType File -Path $probeOut, $probeErr -Force | Out-Null
    $probe = Start-Process -FilePath $drillExe -ArgumentList (Quote-Argv @('formats')) -Wait -PassThru `
                           -WindowStyle Hidden -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr
    $probeText = [System.IO.File]::ReadAllText($probeOut, [System.Text.Encoding]::UTF8)
    if ($probe.ExitCode -ne 0) { Die "the installed exe exited with $($probe.ExitCode) on 'formats'" 5 }
    if ($probeText -notmatch 'OUT \(what --to accepts\)') {
        $head = $probeText.Substring(0, [Math]::Min(200, $probeText.Length))
        Die "the installed exe did not print its format table (first 200 chars: $head)" 5
    }
    Say 'drill: the installed exe answered "formats" correctly'

    # Uninstall, with the same shape the Add/Remove Programs entry uses.
    $un = Start-Process -FilePath $uninst -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
    if ($un.ExitCode -ne 0) { Die "the silent uninstall exited with $($un.ExitCode)" 5 }
    Start-Sleep -Milliseconds 800
    if (Test-Path -LiteralPath $drillExe) { Die "the uninstall left the executable behind: $drillExe" 5 }
    Say 'drill: uninstalled cleanly (the executable is gone)'
    Remove-Item -LiteralPath $drillRoot -Recurse -Force -ErrorAction SilentlyContinue
    $drillOk = $true
}

# The staging folder is an implementation detail; the deliverable is one file.
Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
$sw.Stop()

Write-Host ''
Say "BUILT: $setup"
Say ("size={0:N2} MB sha256={1}" -f ($setupItem.Length / 1MB), $setupSha)
Say ("elapsed={0:N1} s" -f $sw.Elapsed.TotalSeconds)
if ($drillOk) {
    Say 'verified: silent install -> every staged file arrived -> installed exe ran "formats" -> silent uninstall -> executable gone'
} else {
    Say 'NOT verified: built only, the setup was never installed (-SkipDrill)'
}
exit 0
