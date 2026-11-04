# verify-license.ps1 -- acceptance for the offline license (2026-11-03).
#
# WHAT THIS PROVES
#   A key that tools\make-license.ps1 signs with the PRIVATE key unlocks the paid tool (CUTOUT) on
#   this machine, and a key that was edited, mistyped, or made up does NOT. Both halves matter: a
#   verifier that says yes to everything and a verifier that says no to everything look identical
#   if you only test one of them.
#
# WHY IT MINTS ITS OWN KEYS
#   The check could use a hardcoded key string, but then a change to the payload format or the
#   signature encoding would break the PRODUCT while the test kept passing against the old string.
#   Minting the key here walks the real path: sign (PS 5.1 ECDsaCng) -> verify (.NET 8 ECDsa). That
#   is the seam between two runtimes, and it is the one thing that can silently rot.
#
#   The private key lives at %LOCALAPPDATA%\Confern-keys\license-key.json -- OUTSIDE the repo, on
#   this machine only. When it is missing the checks are INCONCLUSIVE, not failed: a machine that
#   can verify but cannot sign is a normal state (that is every customer's machine).
#
# ISOLATION
#   CONFERN_LICENSE and CONFERN_USAGE both point inside this run's temp folder, so neither the
#   user's license nor the user's free uses are touched. The check that the meter is really
#   untouched runs last and reads the real paths.
#
# ENCODING: ASCII only, CRLF, no BOM (Windows PowerShell 5.1 reads a BOM-less file as ANSI).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
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

# base64url helpers -- the suite has to decode and re-encode payloads in order to build the two
# "edited" keys that must be refused. Reusing the product's own encoders is not an option (they are
# internal), so the suite carries its own copy, exactly like the product's License.FromBase64Url.
function To-B64Url([byte[]]$bytes) {
    return ([Convert]::ToBase64String($bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}

function From-B64Url([string]$text) {
    $s = $text.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) {
        2 { $s += '==' }
        3 { $s += '=' }
        1 { throw "impossible base64url length ($($text.Length))" }
    }
    return [Convert]::FromBase64String($s)
}

# -------------------------------------------------------------------------------------------- setup
$root = Split-Path -Parent $PSScriptRoot
$maker = Join-Path $PSScriptRoot 'make-license.ps1'
$keyPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern-keys') 'license-key.json'

if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "FATAL: no exe at '$Exe'" -ForegroundColor Red; exit 2 }
if (-not (Test-Path -LiteralPath $maker)) { Write-Host "FATAL: no '$maker'" -ForegroundColor Red; exit 2 }

if ($Work -eq '') { $Work = Join-Path (Join-Path $env:TEMP 'confern-license') ([guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# Isolation: the suite must not touch the user's license or the user's free uses.
$licensePath = Join-Path $Work 'license.txt'
$usagePath = Join-Path $Work 'usage.json'
$env:CONFERN_LICENSE = $licensePath
$env:CONFERN_USAGE = $usagePath

# The state of the real files BEFORE anything runs, so S1.12 can prove they were not touched.
# A fingerprint rather than an existence test: the machine may legitimately have a real license.
function Get-FileFingerprint([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 'absent' }
    $i = Get-Item -LiteralPath $path
    return ("{0}|{1}|{2}" -f $i.Length, $i.LastWriteTimeUtc.Ticks,
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
}
$script:realLicenseBefore = Get-FileFingerprint (Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'license.txt')
$script:realUsageBefore = Get-FileFingerprint (Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'usage.json')

$stamp = (Get-Item -LiteralPath $Exe).LastWriteTime
Write-Host "exe   : $Exe"
Write-Host "stamp : $stamp"
Write-Host "license store: $licensePath (the user's own license is not touched)"
Write-Host "usage meter  : $usagePath (the user's own free uses are not touched)"
if (Test-Path -LiteralPath $keyPath) { Write-Host "signing key  : present" }
else { Write-Host "signing key  : MISSING at $keyPath (signing checks will be inconclusive)" }
Write-Host ""

# The product is a WinExe: `& $exe` returns immediately, so every launch goes through Start-Process -Wait.
function Invoke-Confern {
    param([string[]]$Arguments, [string]$Tag = 'run')
    $log = Join-Path $Work ("$Tag.log")
    $out = Join-Path $Work ("$Tag.out")
    $err = Join-Path $Work ("$Tag.err")
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $text = ''
    foreach ($f in @($out, $err, $log)) {
        if (Test-Path -LiteralPath $f) { $text += (Get-Content -LiteralPath $f -Raw -Encoding UTF8) + "`n" }
    }
    return [pscustomobject]@{ Exit = $p.ExitCode; Text = $text; Log = $log; StdOut = $out }
}

function New-Key {
    param([string]$Email = 'buyer@example.com', [string]$Extra = '')
    # NOT `$args`: that is a PowerShell automatic variable, and assigning to it inside a function
    # is a trap that works until the day it silently does not.
    $mkArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $maker, '-KeyOnly')
    if ($Email -ne '') { $mkArgs += @('-Email', $Email) }
    if ($Extra -ne '') { $mkArgs += $Extra.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) }
    $raw = & powershell @mkArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $script:keyError = "make-license.ps1 exited $LASTEXITCODE`: " + ($raw -join ' ')
        return $null
    }
    $key = ($raw | Where-Object { $_ -match '^CN1\.' } | Select-Object -First 1)
    if (-not $key) {
        $script:keyError = 'no CN1. line in the seller tool output: ' + ($raw -join ' ')
        return $null
    }
    return $key.Trim()
}

# -------------------------------------------------------------------------------------------- S1: nothing to start with
$r = Invoke-Confern @('license') 'S1-show-none'
Check 'S1.1 with no license, `license` says none and the trial applies' `
    ($r.Exit -eq 0 -and $r.Text -match 'license state: none' -and $r.Text -match 'usage feature: cutout used=0 limit=10 remaining=10') `
    ("exit=$($r.Exit); expected 'license state: none' and remaining=10`n" + $r.Text)

# -------------------------------------------------------------------------------------------- S1.2: the seller tool
# Always registers exactly one check: a machine that can verify but cannot sign is a normal state
# (it is every customer's machine), so a missing private key is INCONCLUSIVE, not a failure.
$key = $null
$script:keyError = ''
if (Test-Path -LiteralPath $keyPath) {
    $key = New-Key
    if ($key) { Pass 'S1.2 the seller tool signs a key' }
    else { Fail 'S1.2 the seller tool signs a key' $script:keyError }
} else {
    Inconclusive 'S1.2 the seller tool signs a key' "no signing key on this machine ($keyPath)"
}

# -------------------------------------------------------------------------------------------- S1.3: activation
if ($key) {
    $r = Invoke-Confern @('license', '--activate', $key) 'S1-activate'
    $onDisk = if (Test-Path -LiteralPath $licensePath) { (Get-Content -LiteralPath $licensePath -Raw).Trim() } else { '' }
    Check 'S1.3 a signed key activates, and the file holds exactly that key' `
        ($r.Exit -eq 0 -and $r.Text -match 'license state: active plan=pro' -and $onDisk -eq $key) `
        ("exit=$($r.Exit); on disk matches the key: $($onDisk -eq $key)`n" + $r.Text)
} else {
    Inconclusive 'S1.3 a signed key activates, and the file holds exactly that key' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.4: it says unlimited
if ($key) {
    $r = Invoke-Confern @('license') 'S1-show-active'
    Check 'S1.4 an active license turns "remaining" into unlimited' `
        ($r.Text -match 'usage feature: cutout used=0 limit=10 remaining=unlimited') `
        ("expected remaining=unlimited`n" + $r.Text)
} else {
    Inconclusive 'S1.4 an active license turns "remaining" into unlimited' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.5: the tool actually runs
if ($key) {
    # Take the meter to 10/10 first: this is the case the license exists for.
    [void](Invoke-Confern @('--usage-set', 'cutout=10', '--quiet') 'S1-exhaust')
    $shot = Join-Path $Work 'portrait.png'
    Copy-Item -LiteralPath (Join-Path $root 'fixtures\rembg\real-512.png') -Destination $shot -Force
    $before = @(Get-ChildItem -LiteralPath $Work -Filter '*.png').Count
    $r = Invoke-Confern @('--remove-bg', $shot) 'S1-run-licensed'
    $after = @(Get-ChildItem -LiteralPath $Work -Filter '*.png').Count
    $summary = ($r.Text -match 'remove-bg summary:') -and ($r.Text -match 'allowed by the license')
    Check 'S1.5 a license lets CUTOUT run even at 10/10, and spends nothing' `
        ($r.Exit -eq 0 -and $summary -and $after -gt $before -and $r.Text -match 'a license is active, so nothing is counted') `
        ("exit=$($r.Exit); png before=$before after=$after; summary=$summary`n" + $r.Text)
} else {
    Inconclusive 'S1.5 a license lets CUTOUT run even at 10/10, and spends nothing' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.6: edited keys are refused
$edited = if ($key) { $key.Substring(0, $key.Length - 6) + 'AAAAAA' } else { '' }
if ($key) {
    $r = Invoke-Confern @('license', '--activate', $edited) 'S1-activate-edited'
    $stillGood = (Test-Path -LiteralPath $licensePath) -and ((Get-Content -LiteralPath $licensePath -Raw).Trim() -eq $key)
    Check 'S1.6 a key whose signature was edited is refused, and the stored key is left alone' `
        ($r.Exit -eq 2 -and $r.Text -match 'signature does not match' -and $stillGood) `
        ("exit=$($r.Exit) (expected 2); the good key is still stored: $stillGood`n" + $r.Text)
} else {
    Inconclusive 'S1.6 a key whose signature was edited is refused, and the stored key is left alone' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.7: the payload is covered
# Same signature, one field changed inside the payload: this is what a buyer would try in order to
# turn 'solo' into 'pro', or to move updatesUntil out a decade.
if ($key) {
    $parts = $key.Split('.')
    $json = [System.Text.Encoding]::UTF8.GetString((From-B64Url $parts[1]))
    $patched = $json.Replace('"plan":"pro"', '"plan":"solo"')
    $forged = 'CN1.' + (To-B64Url ([System.Text.Encoding]::UTF8.GetBytes($patched))) + '.' + $parts[2]
    $r = Invoke-Confern @('license', '--activate', $forged) 'S1-activate-forged'
    Check 'S1.7 a payload edited under a valid signature is refused (the signature covers the contents)' `
        ($r.Exit -eq 2 -and $r.Text -match 'signature does not match') `
        ("exit=$($r.Exit) (expected 2)`n" + $r.Text)
} else {
    Inconclusive 'S1.7 a payload edited under a valid signature is refused (the signature covers the contents)' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.8: junk, and no partial state
# Three parts, so it reaches the base64url decoder instead of stopping at the shape check -- the
# reason has to say WHERE it failed, otherwise "invalid key" is all the buyer ever learns.
$r = Invoke-Confern @('license', '--activate', 'CN1.!!!!not-base64!!!!.@@@@') 'S1-activate-junk'
Check 'S1.8 a key that is not even base64url is refused with a readable reason' `
    ($r.Exit -eq 2 -and $r.Text -match 'not valid base64url') `
    ("exit=$($r.Exit) (expected 2), and the reason should name base64url`n" + $r.Text)

# -------------------------------------------------------------------------------------------- S1.9: a hand-edited store fails closed
if ($key) {
    # Rewrite the stored file so that it holds a valid-looking but unsigned key. The product must
    # not trust the file just because it is there.
    $parts = $key.Split('.')
    $json = [System.Text.Encoding]::UTF8.GetString((From-B64Url $parts[1]))
    # The date to widen is READ FROM THE PAYLOAD, not hardcoded: a literal here is a time bomb.
    # This exact line said '2027-11-03' and stopped matching on 2026-11-04 (the suite mints its key
    # one year from today), so the "tamper" became a no-op, the file still verified, and the check
    # went red for a reason that had nothing to do with the product.
    $updField = [regex]::Match($json, '"updatesUntil"\s*:\s*"([^"]+)"')
    if (-not $updField.Success) {
        Write-Host "FATAL: the minted key has no expiry field to widen (~$json)" -ForegroundColor Red
        exit 2
    }
    $widened = $json.Replace($updField.Groups[1].Value, '2099-11-03')
    $forged = 'CN1.' + (To-B64Url ([System.Text.Encoding]::UTF8.GetBytes($widened))) + '.' + $parts[2]
    [System.IO.File]::WriteAllText($licensePath, $forged + "`n")
    $r = Invoke-Confern @('license') 'S1-show-hand-edited'
    Check 'S1.9 a hand-edited store is not trusted (verification happens on every read)' `
        ($r.Text -match 'license state: none' -and $r.Text -match 'not valid') `
        ("expected the state to fall back to none`n" + $r.Text)
} else {
    Inconclusive 'S1.9 a hand-edited store is not trusted (verification happens on every read)' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.10: clearing re-locks the tool
if ($key) {
    [System.IO.File]::WriteAllText($licensePath, $key + "`n")
    $r = Invoke-Confern @('license', '--clear') 'S1-clear'
    $gone = -not (Test-Path -LiteralPath $licensePath)
    $r2 = Invoke-Confern @('--remove-bg', (Join-Path $Work 'portrait.png')) 'S1-run-after-clear'
    Check 'S1.10 clearing the license deletes the file and CUTOUT is refused again at 10/10' `
        ($r.Exit -eq 0 -and $gone -and $r2.Exit -eq 12 -and $r2.Text -match 'trial is used up') `
        ("clear exit=$($r.Exit); file gone=$gone; remove-bg exit=$($r2.Exit) (expected 12)`n" + $r.Text + $r2.Text)
} else {
    Inconclusive 'S1.10 clearing the license deletes the file and CUTOUT is refused again at 10/10' 'no key could be minted'
}

# -------------------------------------------------------------------------------------------- S1.11: bare words are refused
$r = Invoke-Confern @('license', 'stray-word') 'S1-bare-word'
Check 'S1.11 `license` refuses bare words instead of quietly activating something' `
    ($r.Exit -eq 4 -and $r.Text -match 'license takes no bare words') `
    ("exit=$($r.Exit) (expected 4)`n" + $r.Text)

# -------------------------------------------------------------------------------------------- S1.12: isolation held
# NOT "the real file does not exist": whoever runs this suite may well have a real license on this
# machine. What matters is that this run did not CHANGE it, so compare the state recorded before
# the first activation check with the state now (existence, size, write time, and contents).
$realLicense = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'license.txt'
$realUsage = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern') 'usage.json'
$realLicenseNow = Get-FileFingerprint $realLicense
$realUsageNow = Get-FileFingerprint $realUsage
Check 'S1.12 the run never wrote the user''s own license or usage file' `
    ($env:CONFERN_LICENSE -eq $licensePath -and $env:CONFERN_USAGE -eq $usagePath -and
     $realLicenseNow -eq $script:realLicenseBefore -and $realUsageNow -eq $script:realUsageBefore) `
    ("CONFERN_LICENSE=$($env:CONFERN_LICENSE); CONFERN_USAGE=$($env:CONFERN_USAGE); " +
     "the real license before=$($script:realLicenseBefore) now=$realLicenseNow; " +
     "the real usage before=$($script:realUsageBefore) now=$realUsageNow")

# -------------------------------------------------------------------------------------------- summary
Expect $script:ExpectedChecks
Write-Host ""
Write-Host ("checks={0} pass={1} fail={2} inconclusive={3} expected={4}" -f $checks, $pass, $fail, $inconclusive, $script:ExpectedChecks)
if ($fail -gt 0) {
    Write-Host ""
    Write-Host "FAILURES:"
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    Write-Host ""
    Write-Host "License summary: $pass/$checks checks passed (FAILED)"
    exit 1
}
Write-Host "License summary: $pass/$checks checks passed"
if ($inconclusive -gt 0) { exit 3 }
exit 0
