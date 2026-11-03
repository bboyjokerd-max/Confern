# verify-edition.ps1 -- acceptance for the two builds of one product (2026-11-03).
#
# WHAT THIS PROVES
#   Two builds come out of ONE source tree, and they are genuinely different products:
#     paid  -- has the CUTOUT tool, the 10-free-use meter and the offline license;
#     free  -- has no CUTOUT at all: no tool, no meter file, no license file, no key accepted.
#
#   The check that matters most is the pair, not either half on its own. A free build that quietly
#   kept the paywall and a paid build that quietly lost it both look fine if you only run one of
#   them; run together they can only both pass when the boundary is real.
#
# WHY THE FREE EXE IS A SEPARATE PARAMETER
#   The sweep drives the PAID exe (`-Exe` like every other suite). The free build is a second
#   artefact with its own output folder (bin\Free), so it arrives as -FreeExe. When it is absent the
#   free-side checks are INCONCLUSIVE, not failed: a checkout that never built the free edition is a
#   normal state, and making that red would teach everyone to ignore this suite.
#
# ISOLATION
#   CONFERN_USAGE and CONFERN_LICENSE point inside this run's temp folder for BOTH builds, so
#   neither the user's free uses nor the user's license are touched. The last check reads the real
#   files' fingerprints and proves they did not move.
#
# ENCODING: ASCII only, CRLF, no BOM (Windows PowerShell 5.1 reads a BOM-less file as ANSI), and no
#   non-ASCII anywhere -- this file is read by a tool that is itself ASCII-only.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$FreeExe = '',
    [string]$Work = ''
)

$ErrorActionPreference = 'Stop'
$script:ExpectedChecks = 12
$checks = 0
$pass = 0
$fail = 0
$inconclusive = 0
$failures = New-Object System.Collections.ArrayList

function Expect([int]$n) {
    if ($checks -ne $n) {
        Write-Host "FATAL: registered $checks checks but this suite must register exactly $n" -ForegroundColor Red
        exit 2
    }
}

function Pass([string]$name) {
    $script:checks++
    $script:pass++
    Write-Host "PASS  $name"
}

function Fail([string]$name, [string]$why) {
    $script:checks++
    $script:fail++
    [void]$script:failures.Add("$name -- $why")
    Write-Host "FAIL  $name" -ForegroundColor Red
    Write-Host "      $why" -ForegroundColor DarkGray
}

function Inconclusive([string]$name, [string]$why) {
    $script:checks++
    $script:inconclusive++
    Write-Host "SKIP  $name" -ForegroundColor Yellow
    Write-Host "      $why" -ForegroundColor DarkGray
}

function Check([string]$name, [bool]$ok, [string]$why) {
    if ($ok) { Pass $name } else { Fail $name $why }
}

# -------------------------------------------------------------------------------------------- setup
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root 'fixtures\rembg\real-512.png'

if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "FATAL: no exe at '$Exe'" -ForegroundColor Red; exit 2 }
if ($FreeExe -eq '') { $FreeExe = Join-Path $root 'src\Sidecar\bin\Free\Release\net8.0-windows\Confern.exe' }
$haveFree = Test-Path -LiteralPath $FreeExe

if ($Work -eq '') { $Work = Join-Path (Join-Path $env:TEMP 'confern-edition') ([guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$usagePath = Join-Path $Work 'usage.json'
$licensePath = Join-Path $Work 'license.txt'
$env:CONFERN_USAGE = $usagePath
$env:CONFERN_LICENSE = $licensePath

function Get-FileFingerprint([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 'absent' }
    $i = Get-Item -LiteralPath $path
    return ("{0}|{1}|{2}" -f $i.Length, $i.LastWriteTimeUtc.Ticks,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
}

# The REAL files (not the overridden ones) before anything runs.
$realUsagePath = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'usage.json'
$realLicensePath = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'license.txt'
$realUsageBefore = Get-FileFingerprint $realUsagePath
$realLicenseBefore = Get-FileFingerprint $realLicensePath

Write-Host "paid exe : $Exe"
Write-Host "           $((Get-Item -LiteralPath $Exe).LastWriteTime)"
if ($haveFree) {
    Write-Host "free exe : $FreeExe"
    Write-Host "           $((Get-Item -LiteralPath $FreeExe).LastWriteTime)"
} else {
    Write-Host "free exe : MISSING at $FreeExe (the free-side checks will be inconclusive)" -ForegroundColor Yellow
}
Write-Host "usage meter (this run): $usagePath"
Write-Host "license     (this run): $licensePath"
Write-Host ""

# The product is a WinExe: `& $exe` returns immediately, so every launch goes through
# Start-Process -Wait. Logs are read as well as stdout/stderr, because the product writes its
# evidence lines to its own log.
function Invoke-Build {
    param([string]$Path, [string[]]$Arguments, [string]$Tag = 'run')
    $log = Join-Path $Work ("$Tag.log")
    $out = Join-Path $Work ("$Tag.out")
    $err = Join-Path $Work ("$Tag.err")
    $p = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $text = ''
    foreach ($f in @($out, $err, $log)) {
        if (Test-Path -LiteralPath $f) { $text += (Get-Content -LiteralPath $f -Raw -Encoding UTF8) + "`n" }
    }
    return [pscustomobject]@{ Exit = $p.ExitCode; Text = $text; Log = $log; StdOut = $out }
}

# -------------------------------------------------------------------------------------------- E1: each build says what it is
$paidSay = Invoke-Build $Exe @('license') 'E1-paid-says'
Check 'E1.1 the paid build reports edition: paid' `
    ($paidSay.Exit -eq 0 -and $paidSay.Text -match 'edition: paid') `
    ("exit=$($paidSay.Exit); expected 'edition: paid'`n" + $paidSay.Text)

if ($haveFree) {
    $freeSay = Invoke-Build $FreeExe @('license') 'E1-free-says'
    Check 'E1.2 the free build reports edition: free and no quota or license line' `
        ($freeSay.Exit -eq 0 -and $freeSay.Text -match 'edition: free' -and
         $freeSay.Text -notmatch 'usage feature:') `
        ("exit=$($freeSay.Exit); expected 'edition: free' and no 'usage feature:'`n" + $freeSay.Text)
} else {
    Inconclusive 'E1.2 the free build reports edition: free and no quota or license line' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E2: the tool list is the boundary
$paidTools = Invoke-Build $Exe @('--tools-list', '--wheel-source', 'png') 'E2-paid-tools'
Check 'E2.1 the paid build offers the CUTOUT sector (remove-bg) for an image' `
    ($paidTools.Text -match "id=remove-bg" -and $paidTools.Text -match 'count=\d+') `
    ("expected an 'id=remove-bg' tool line`n" + $paidTools.Text)

if ($haveFree) {
    $freeTools = Invoke-Build $FreeExe @('--tools-list', '--wheel-source', 'png') 'E2-free-tools'
    $freeCount = 0
    $m = [regex]::Match($freeTools.Text, 'tool list: count=(\d+)')
    if ($m.Success) { $freeCount = [int]$m.Groups[1].Value }
    $paidCount = 0
    $pm = [regex]::Match($paidTools.Text, 'tool list: count=(\d+)')
    if ($pm.Success) { $paidCount = [int]$pm.Groups[1].Value }
    # "One sector fewer" rather than a hardcoded 6: for a PNG the PDF-only tool is filtered out too,
    # so the absolute number depends on the source format while the DIFFERENCE does not.
    Check 'E2.2 the free build offers exactly one sector less than the paid build, and it is remove-bg' `
        ($freeTools.Exit -eq 0 -and $paidCount -gt 0 -and $freeCount -eq ($paidCount - 1) -and
         $freeTools.Text -notmatch 'remove-bg') `
        ("exit=$($freeTools.Exit); free count=$freeCount paid count=$paidCount (want paid-1); " +
         "mentions remove-bg: " + ($freeTools.Text -match 'remove-bg') + "`n" + $freeTools.Text)
} else {
    Inconclusive 'E2.2 the free build has no CUTOUT sector: 6 tools, no remove-bg' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E3: the free build refuses the paid tool
if ($haveFree) {
    $outDir = Join-Path $Work 'free-rembg'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $freeCut = Invoke-Build $FreeExe @('--remove-bg', $fixture) 'E3-free-rembg'
    $made = @(Get-ChildItem -LiteralPath $outDir -File -ErrorAction SilentlyContinue)
    Check 'E3.1 the free build refuses --remove-bg with exit 2, and says which build this is' `
        ($freeCut.Exit -eq 2 -and $freeCut.Text -match 'not in this build') `
        ("exit=$($freeCut.Exit) (want 2); expected 'not in this build'`n" + $freeCut.Text)
} else {
    Inconclusive 'E3.1 the free build refuses --remove-bg with exit 2, and says which build this is' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E4: the free build meters nothing
if ($haveFree) {
    # A meter that is already at 10/10 must be irrelevant: the free build never reads it, and it must
    # not touch it either (a free user should not find a quota file in their AppData at all).
    Remove-Item -LiteralPath $usagePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $licensePath -Force -ErrorAction SilentlyContinue
    $before = Get-FileFingerprint $usagePath
    $freeRun = Invoke-Build $FreeExe @('license') 'E4-free-meter'
    $after = Get-FileFingerprint $usagePath
    Check 'E4.1 the free build writes no usage file' `
        ($freeRun.Exit -eq 0 -and $before -eq 'absent' -and $after -eq 'absent') `
        ("usage file before=$before after=$after (both must be 'absent'); exit=$($freeRun.Exit)")
} else {
    Inconclusive 'E4.1 the free build writes no usage file' 'no free build on this machine'
}

if ($haveFree) {
    Remove-Item -LiteralPath $licensePath -Force -ErrorAction SilentlyContinue
    # A key-shaped string with a signature that cannot be valid. The free build must refuse on the
    # EDITION, before it ever looks at the signature -- and must not leave a file behind.
    $fakeKey = 'CN1.eyJ2IjoxLCJwbGFuIjoicHJvIn0.AAAA'
    $freeAct = Invoke-Build $FreeExe @('license', '--activate', $fakeKey) 'E4-free-activate'
    Check 'E4.2 the free build refuses to activate a key (exit 2) and writes no license file' `
        ($freeAct.Exit -eq 2 -and -not (Test-Path -LiteralPath $licensePath) -and
         $freeAct.Text -match 'edition') `
        ("exit=$($freeAct.Exit) (want 2); license file exists: $(Test-Path -LiteralPath $licensePath)`n" + $freeAct.Text)
} else {
    Inconclusive 'E4.2 the free build refuses to activate a key (exit 2) and writes no license file' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E5: the free build is a real product
if ($haveFree) {
    $geo = Invoke-Build $FreeExe @('--wheel-geometry-selftest') 'E5-free-geometry'
    Check 'E5.1 the free build passes the wheel geometry self-test (its wheel has one sector fewer)' `
        ($geo.Exit -eq 0 -and $geo.Text -notmatch 'FAIL') `
        ("exit=$($geo.Exit); a FAIL line means the 6-sector wheel is broken`n" + $geo.Text)
} else {
    Inconclusive 'E5.1 the free build passes the wheel geometry self-test (its wheel has one sector fewer)' 'no free build on this machine'
}

if ($haveFree) {
    # The free edition must still DO the work it claims, otherwise "no cutout" would be a polite way
    # of saying "nothing works". Convert with the real engine, and check the file really appeared.
    $srcPng = Join-Path $Work 'free-convert.png'
    Copy-Item -LiteralPath $fixture -Destination $srcPng -Force
    $conv = Invoke-Build $FreeExe @('convert', $srcPng, '--to', 'jpg') 'E5-free-convert'
    $jpg = [System.IO.Path]::ChangeExtension($srcPng, '.jpg')
    $jpgSize = 0
    if (Test-Path -LiteralPath $jpg) { $jpgSize = (Get-Item -LiteralPath $jpg).Length }
    Check 'E5.2 the free build really converts a PNG to JPG (not a stub)' `
        ($conv.Exit -eq 0 -and $jpgSize -gt 1000) `
        ("exit=$($conv.Exit); jpg=$jpgSize bytes (want > 1000)`n" + $conv.Text)
} else {
    Inconclusive 'E5.2 the free build really converts a PNG to JPG (not a stub)' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E6: the paid build still gates
# The edition switch is one #if away from silently deleting the paywall from the paid build. This is
# the check that notices: a full meter plus the paid exe must produce exit 12.
$seed = Invoke-Build $Exe @('--usage-set', 'cutout=10') 'E6-seed'
$seedOk = ($seed.Exit -eq 0 -and $seed.Text -match 'usage feature: cutout used=10 limit=10 remaining=0')
$paidCut = Invoke-Build $Exe @('--remove-bg', $fixture) 'E6-paid-cut'
Check 'E6.1 the paid build still refuses the cutout at 10/10 (exit 12)' `
    ($seedOk -and $paidCut.Exit -eq 12 -and $paidCut.Text -match 'REFUSED') `
    ("seed exit=$($seed.Exit) ok=$seedOk; cutout exit=$($paidCut.Exit) (want 12)`n" + $paidCut.Text)

# -------------------------------------------------------------------------------------------- E7: two builds, two binaries
if ($haveFree) {
    # The .exe is only the apphost shim, identical in both builds (measured 2026-11-03: same sha256).
    # The compiled product is Confern.dll, so that is what "two different builds" has to be compared on.
    $paidDll = Join-Path (Split-Path -Parent $Exe) 'Confern.dll'
    $freeDll = Join-Path (Split-Path -Parent $FreeExe) 'Confern.dll'
    $paidSha = (Get-FileHash -LiteralPath $paidDll -Algorithm SHA256).Hash
    $freeSha = (Get-FileHash -LiteralPath $freeDll -Algorithm SHA256).Hash
    Check 'E7.1 the two builds are different binaries (Confern.dll differs)' `
        ($paidSha -ne $freeSha) `
        ("both Confern.dll files hash to $paidSha -- the free build was not actually rebuilt with -p:Edition=Free")
} else {
    Inconclusive 'E7.1 the two executables are different binaries' 'no free build on this machine'
}

# -------------------------------------------------------------------------------------------- E8: nothing of the user's moved
$realUsageAfter = Get-FileFingerprint $realUsagePath
$realLicenseAfter = Get-FileFingerprint $realLicensePath
Check "E8.1 the user's own usage.json and license.txt are untouched" `
    ($realUsageBefore -eq $realUsageAfter -and $realLicenseBefore -eq $realLicenseAfter) `
    ("usage before=$realUsageBefore after=$realUsageAfter; " +
     "license before=$realLicenseBefore after=$realLicenseAfter")

# -------------------------------------------------------------------------------------------- summary
Write-Host ""
Write-Host ("summary: checks={0} pass={1} fail={2} inconclusive={3}" -f $checks, $pass, $fail, $inconclusive)
if ($fail -gt 0) {
    Write-Host "failures:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    Expect $script:ExpectedChecks
    exit 1
}
Expect $script:ExpectedChecks
exit 0
