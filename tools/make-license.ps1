# make-license.ps1 -- mint a Confern license key (the SELLER's tool, not the buyer's).
#
# WHY THIS IS A SEPARATE SCRIPT
#   The product only ever VERIFIES a key, with a public key baked into the build
#   (src\Sidecar\License\License.cs). Signing needs the PRIVATE key, and the private key must never
#   be in the repository, in the installer, or on a customer's machine. So the two halves live in
#   two different places on purpose: this script holds the signing half, and it reads the key from
#   OUTSIDE the repo.
#
#   %LOCALAPPDATA%\Confern-keys\license-key.json     <- the private key (back this up!)
#                                                    <- losing it means every sold key is dead
#
#   The public key is in License.cs, and rotating it invalidates every key already sold. Treat the
#   pair as a durable asset: back the file up somewhere off this machine.
#
# KEY FORMAT (must match License.cs exactly)
#   CN1.<base64url(payload)>.<base64url(signature)>
#   payload   : the UTF-8 JSON below, signed BYTE FOR BYTE as this script builds it
#   signature : ECDSA P-256 / SHA-256, IEEE P1363 (r||s, 32+32 = 64 bytes)
#               (measured: ECDsaCng.SignHash on Windows PowerShell 5.1 yields exactly this shape,
#                and .NET 8's ECDsa.VerifyData with no format argument expects the same)
#
# USAGE
#   powershell -File tools\make-license.ps1 -Email buyer@example.com
#   powershell -File tools\make-license.ps1 -Email buyer@example.com -UpdatesUntil 2028-11-03
#   powershell -File tools\make-license.ps1 -Verify CN1.eyJ2Ijox...      (check a key, no signing)
#
# THIS FILE MUST STAY PURE ASCII, CRLF, AND MUST NOT HAVE A BOM (Windows PowerShell 5.1).

[CmdletBinding(DefaultParameterSetName = 'Mint')]
param(
    # The buyer's address. It goes into the signed payload, so it is visible to anyone holding the
    # key -- which is the buyer themselves. Leave it empty for a key you hand out anonymously.
    [Parameter(ParameterSetName = 'Mint')]
    [string]$Email = '',

    # 'pro' or 'solo'. Anything else is refused by the product (License.TryVerify).
    [Parameter(ParameterSetName = 'Mint')]
    [ValidateSet('pro', 'solo')]
    [string]$Plan = 'pro',

    # The LAST DAY the buyer can get new versions for free. The license itself never expires:
    # the product keeps working forever, it simply stops offering versions released after this date.
    [Parameter(ParameterSetName = 'Mint')]
    [string]$UpdatesUntil = '',

    # Convenience: N years from the issue date, used when -UpdatesUntil is not given.
    [Parameter(ParameterSetName = 'Mint')]
    [int]$Years = 1,

    # The issue date written into the key. Overridable so a suite can mint a fixed, comparable key.
    [Parameter(ParameterSetName = 'Mint')]
    [string]$Issued = (Get-Date).ToString('yyyy-MM-dd'),

    # Private key file. Default: %LOCALAPPDATA%\Confern-keys\license-key.json (never in the repo).
    [string]$KeyPath = '',

    # Verify an existing key instead of signing a new one.
    [Parameter(ParameterSetName = 'Verify')]
    [string]$Verify = '',

    # Print only the key (for piping into a file or an email body).
    [switch]$KeyOnly
)

$ErrorActionPreference = 'Stop'

if ($KeyPath -eq '') {
    $KeyPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Confern-keys') 'license-key.json'
}

function Die([string]$message) { Write-Host "FATAL: $message"; exit 2 }

# ------------------------------------------------------------------------------------------------ keys
function Read-Key([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        Die "no private key at '$path'. Run this script once with -NewKey to create one, and keep the file safe."
    }
    $raw = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    foreach ($f in @('curve', 'publicKey', 'privateD')) {
        if (-not $raw.$f) { Die "'$path' is missing '$f'; it is not a key file this script wrote." }
    }
    if ($raw.curve -ne 'nistP256') { Die "'$path' says curve '$($raw.curve)'; this tool only signs P-256." }
    return $raw
}

# Rebuild the ECDsa object from the stored halves. ExportParameters gave us X||Y (64 bytes) and D.
function New-Ecdsa([string]$curve, [byte[]]$publicRaw, [byte[]]$privateD) {
    if ($publicRaw.Length -ne 64) { Die "the public key is $($publicRaw.Length) bytes, not 64 (P-256 X||Y)." }
    $ec = [System.Security.Cryptography.ECDsa]::Create(
              [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName($curve))
    $par = New-Object System.Security.Cryptography.ECParameters
    $par.Curve = [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName($curve)
    if ($null -ne $privateD) { $par.D = $privateD }
    $q = New-Object System.Security.Cryptography.ECPoint
    $q.X = [byte[]]$publicRaw[0..31]
    $q.Y = [byte[]]$publicRaw[32..63]
    $par.Q = $q
    $ec.ImportParameters($par)
    return $ec
}

function To-Base64Url([byte[]]$bytes) {
    return ([Convert]::ToBase64String($bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
}

function From-Base64Url([string]$text) {
    $s = $text.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) {
        2 { $s += '==' }
        3 { $s += '=' }
        1 { throw 'impossible base64url length' }
    }
    return [Convert]::FromBase64String($s)
}

# ------------------------------------------------------------------------------------------------ verify
if ($Verify -ne '') {
    $key = Read-Key $KeyPath
    $parts = $Verify.Trim().Split('.')
    if ($parts.Count -ne 3) { Die "not a license key (expected 3 dot-separated parts, got $($parts.Count))" }
    if ($parts[0] -ne 'CN1') { Die "not a Confern key (prefix '$($parts[0])')" }
    $payload = From-Base64Url $parts[1]
    $signature = From-Base64Url $parts[2]
    $ec = New-Ecdsa $key.curve ([Convert]::FromBase64String($key.publicKey)) $null
    $hash = ([System.Security.Cryptography.SHA256]::Create()).ComputeHash($payload)
    $ok = $ec.VerifyHash($hash, $signature)
    $json = [System.Text.Encoding]::UTF8.GetString($payload)
    if ($KeyOnly) { if ($ok) { 'ok' } else { 'bad' } ; exit $(if ($ok) { 0 } else { 1 }) }
    Write-Host "payload : $json"
    Write-Host "signature: $($signature.Length) bytes"
    Write-Host "verify  : $ok"
    exit $(if ($ok) { 0 } else { 1 })
}

# ------------------------------------------------------------------------------------------------ mint
$key = Read-Key $KeyPath

# Dates: yyyy-MM-dd, invariant, and refused rather than guessed.
function Normalize-Date([string]$text, [string]$what) {
    $parsed = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::None
    if (-not [datetime]::TryParseExact($text, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        Die "$what '$text' is not yyyy-MM-dd (e.g. 2027-11-03)."
    }
    return $parsed.ToString('yyyy-MM-dd')
}

$issueDate = Normalize-Date $Issued '-Issued'
if ($UpdatesUntil -eq '') {
    $UpdatesUntil = ([datetime]::ParseExact($issueDate, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)).AddYears($Years).ToString('yyyy-MM-dd')
}
$updates = Normalize-Date $UpdatesUntil '-UpdatesUntil'

# The payload. Field order and spacing are part of the SIGNATURE, so this string is built here and
# nowhere else -- and License.cs never re-serializes it, it parses what was signed.
$payload = '{"v":1,"plan":"' + $Plan + '","issued":"' + $issueDate + '","updatesUntil":"' + $updates + '"'
if ($Email -ne '') { $payload += ',"email":"' + $Email.Trim() + '"' }
$payload += '}'
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

$ec = New-Ecdsa $key.curve ([Convert]::FromBase64String($key.publicKey)) ([Convert]::FromBase64String($key.privateD))
$hash = ([System.Security.Cryptography.SHA256]::Create()).ComputeHash($payloadBytes)
$signature = $ec.SignHash($hash)
if ($signature.Length -ne 64) { Die "the signature came out $($signature.Length) bytes, not 64; the key is not P-256." }

# Self-check before printing: a key that the seller cannot verify is worse than no key at all.
$pubOnly = New-Ecdsa $key.curve ([Convert]::FromBase64String($key.publicKey)) $null
if (-not $pubOnly.VerifyHash($hash, $signature)) { Die "the signature does not verify with its own public key; refusing to print it." }

$licenseKey = 'CN1.' + (To-Base64Url $payloadBytes) + '.' + (To-Base64Url $signature)

if ($KeyOnly) { Write-Output $licenseKey; exit 0 }

Write-Host "payload  : $payload"
Write-Host "key file : $KeyPath"
Write-Host "signature: $($signature.Length) bytes, verified with the public key before printing"
Write-Host ""
Write-Host "license key:"
Write-Host ""
Write-Output $licenseKey
Write-Host ""
Write-Host "The buyer runs:  confern license --activate <that key>"
Write-Host "Check it here:   powershell -File tools\make-license.ps1 -Verify <that key>"
