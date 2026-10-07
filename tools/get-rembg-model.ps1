# get-rembg-model.ps1 -- fetch the portrait model used by the remove-bg tool (M3, 7th sector).
#
# WHY THIS SCRIPT EXISTS
#   The remove-bg tool runs a portrait segmentation CNN (MODNet). The model is a 12.98 MB
#   binary, so it is NOT in version control (see .gitignore: models/). The product is
#   written so that a missing model is a NAMED STATE, not a crash:
#
#       ModelUnavailable = "the model was never fetched"   -> run THIS script
#       ModelCorrupt     = "the file is there but unreadable (half download / not ONNX)"
#                                                          -> fetch it again
#
#   Those two states have their own user-facing messages on purpose, and this script is the
#   step that turns the first one into a working tool.
#
# WHAT IT DOWNLOADS (measured on this machine, not copied from a page)
#   repo     : Xenova/modnet  (Hugging Face)
#   file     : onnx/model_fp16.onnx
#   license  : apache-2.0 (the model repo README states it covers code, models and demos)
#   length   : 12984781 bytes
#   sha256   : 25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6
#   The sha256 above was measured locally with Get-FileHash -Algorithm SHA256 on the file
#   this URL serves. It is the authority this script enforces: a mismatch deletes the
#   download and fails, because a truncated model would otherwise be reported by the tool as
#   "corrupt" with no hint about why.
#
# WHERE IT GOES
#   Next to the executable, in models\model_fp16.onnx -- that is exactly where
#   RemoveBgModel.ResolvePath looks by default (AppContext.BaseDirectory + models + file).
#   The default -Destination below is the Release output folder of this repo; pass
#   -Destination <exe folder>\models\model_fp16.onnx for any other build.
#
# OFFLINE USE
#   -ExistingPath <a file you already have> copies that file instead of downloading it and
#   verifies the same sha256. That is how this script is exercised without network access.
#
# THIS FILE MUST STAY PURE ASCII AND MUST NOT HAVE A BOM.
#   Windows PowerShell 5.1 reads a BOM-less script as ANSI; a CJK character in a comment
#   silently eats the following statement (measured in this repo's other tools). So: English
#   comments only, ASCII only, no BOM. The .cs and .md files may use Chinese; this may not.

[CmdletBinding()]
param(
    # Where the model file must end up. Default: the Release build's exe folder.
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Sidecar\bin\Release\net8.0-windows\models\model_fp16.onnx'),

    # Download source. Overridable so the suite can point at a local file:// URL.
    [string]$Url = 'https://huggingface.co/Xenova/modnet/resolve/main/onnx/model_fp16.onnx',

    # Expected sha256 (lowercase or uppercase; compared case-insensitively).
    [string]$Sha256 = '25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6',

    # Expected length in bytes. Verified as well as the hash: the length is what the tool's
    # own log line prints ("model length=... (expected ...)"), so a wrong size is visible in
    # two places instead of one.
    [long]$Bytes = 12984781,

    # Copy a file you already have instead of downloading (offline / air-gapped installs).
    [string]$ExistingPath,

    # Re-fetch even when the destination is already present and correct.
    [switch]$Force,

    # Do not print anything except on failure (used by verify-m3d.ps1).
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$Text) {
    if (-not $Quiet) { Write-Host $Text }
}

function Get-Sha256Of([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Fail([string]$Message, [int]$Code) {
    # Failures ALWAYS print, even under -Quiet: a silent failure is the one thing this script
    # must never do (the tool would report "model missing" and the user would have no idea why).
    Write-Host "get-rembg-model: FAILED: $Message"
    exit $Code
}

$destFull = [System.IO.Path]::GetFullPath($Destination)
$expected = $Sha256.Trim().ToUpperInvariant()

# ------------------------------------------------------------------ already there and correct?
if ((Test-Path -LiteralPath $destFull) -and -not $Force) {
    $info = Get-Item -LiteralPath $destFull
    $hash = Get-Sha256Of $destFull
    if (($hash.ToUpperInvariant() -eq $expected) -and ($info.Length -eq $Bytes)) {
        Write-Line "get-rembg-model: already present and verified: $destFull"
        Write-Line ("get-rembg-model: length={0} sha256={1}" -f $info.Length, $hash)
        exit 0
    }
    # There IS a file but it is not the right one. Say exactly which way it is wrong: that is
    # the difference between "you never downloaded it" and "you downloaded it badly".
    Write-Line ("get-rembg-model: the file at that path is NOT the expected model " +
                "(length={0} expected={1}; sha256={2} expected={3}) -- re-fetching" -f `
                $info.Length, $Bytes, $hash, $expected)
}

$destDir = Split-Path -Parent $destFull
if (-not (Test-Path -LiteralPath $destDir)) {
    try { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    catch { Fail "could not create '$destDir': $($_.Exception.Message)" 4 }
}

# ------------------------------------------------------------------ obtain the bytes
# Always into a temp file NEXT TO the destination (same volume => File.Move is atomic), and
# always cleaned up. A half-written model_fp16.onnx would be reported by the tool as
# ModelCorrupt -- so this script must never leave one behind.
$stamp = [System.Guid]::NewGuid().ToString('N')
$temp = Join-Path $destDir ("model_fp16.tmp-fetch-" + $stamp)

try {
    if ($ExistingPath) {
        $src = [System.IO.Path]::GetFullPath($ExistingPath)
        if (-not (Test-Path -LiteralPath $src)) { Fail "ExistingPath not found: $src" 2 }
        Write-Line "get-rembg-model: copying from $src"
        Copy-Item -LiteralPath $src -Destination $temp -Force
    }
    else {
        Write-Line "get-rembg-model: downloading $Url"
        # ProgressPreference=SilentlyContinue: the progress bar makes Invoke-WebRequest roughly
        # an order of magnitude slower on large files (a well known PowerShell 5.1 issue).
        $old = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing -TimeoutSec 900
        }
        catch {
            Fail "download failed: $($_.Exception.Message)" 3
        }
        finally {
            $ProgressPreference = $old
        }
    }

    # ------------------------------------------------------------------ verify BEFORE moving
    $info = Get-Item -LiteralPath $temp
    $hash = Get-Sha256Of $temp
    if ($info.Length -ne $Bytes) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        # NOTE: format FIRST into a variable, then pass it. Writing "Fail ('a' + 'b' -f $x) $code"
        # does NOT format: `-f` loses to the comma operator, so $code is swallowed by the format
        # operand list and the message prints its placeholders verbatim (measured twice).
        $msg = "wrong length: got {0} bytes, expected {1} -- the download was truncated or the source changed; nothing was installed" -f $info.Length, $Bytes
        Fail $msg 5
    }
    if ($hash.ToUpperInvariant() -ne $expected) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        $msg = "wrong sha256: got {0}, expected {1} -- the bytes are not the model this build was measured against; nothing was installed" -f $hash, $expected
        Fail $msg 6
    }

    if (Test-Path -LiteralPath $destFull) { Remove-Item -LiteralPath $destFull -Force }
    Move-Item -LiteralPath $temp -Destination $destFull
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
}

$final = Get-Item -LiteralPath $destFull
Write-Line "get-rembg-model: OK"
Write-Line ("get-rembg-model: path={0}" -f $final.FullName)
Write-Line ("get-rembg-model: length={0} (expected {1})" -f $final.Length, $Bytes)
Write-Line ("get-rembg-model: sha256={0}" -f (Get-Sha256Of $destFull))
Write-Line "get-rembg-model: the remove-bg tool will now load this file (no restart needed for a one-shot run)"
exit 0
