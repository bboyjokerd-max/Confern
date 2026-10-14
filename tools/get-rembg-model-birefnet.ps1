# get-rembg-model-birefnet.ps1 -- fetch the BiRefNet portrait model for the remove-bg tool.
#
# WHY THIS SCRIPT EXISTS
#   get-rembg-model.ps1 fetches MODNet, the original 12.98 MB portrait model. This script
#   fetches BiRefNet, which is 489.7 MB and measurably cleaner on the same photograph:
#
#       portrait fixture, 512x410, 2026-10-14, both measured on this machine:
#           MODNet   opaque=105464  partial=7552  clear=95064
#           BiRefNet opaque=102891  partial=3121  clear=102068
#
#   That is 59% fewer semi-transparent pixels (a narrower soft edge) and 7004 more pixels of
#   background actually removed. The cost is honest and large: 489.7 MB on disk, and about
#   4.6-5.0 s to load the session (MODNet loads in 139-190 ms), so the session cache matters
#   much more here than it did before.
#
#   This model is NOT the default. Nothing selects it unless the caller passes
#           --remove-bg-model <path to this file>
#   so MODNet keeps behaving exactly as it did for every existing user and for the suites.
#
# WHAT IT DOWNLOADS (measured on this machine, not copied from a page)
#   repo     : onnx-community/BiRefNet-ONNX  (Hugging Face)
#   file     : onnx/model_fp16.onnx
#   license  : MIT -- the BiRefNet code AND its weights are MIT (the upstream project is
#              ZhengPeng7/BiRefNet). That is why this model was chosen over RMBG-2.0, whose
#              weights are non-commercial.
#   length   : 489666272 bytes
#   sha256   : 3654C741EB80BD926ADA8FED1713B506CCF8D30EB1F6487E87EB9F234F33DF09
#   interface (read out of the model by RemoveBgModel.DescribeIo, 2026-10-14):
#              input  'input_image'  float32 [1,3,1024,1024]
#              output 'output_image' float32 [1,1,1024,1024]   <- LOGITS, needs sigmoid
#   preprocessing (from the same repo's preprocessor_config.json, fetched 2026-10-14):
#              resize to 1024x1024, rescale by 1/255, then (x - mean) / std with
#              mean=[0.485,0.456,0.406], std=[0.229,0.224,0.225]
#
# WHERE IT GOES
#   Next to the executable, in models\ModelBiRefNet_1024x1024_fp16.onnx. The product resolves
#   its DEFAULT model next to the exe (AppContext.BaseDirectory + models + model_fp16.onnx);
#   this one is never the default, so it only has to exist wherever --remove-bg-model points.
#   The default -Destination below is this repo's Release output folder.
#
# OFFLINE USE
#   -ExistingPath <a file you already have> copies that file instead of downloading it and
#   verifies the same sha256.
#
# THIS FILE MUST STAY PURE ASCII AND MUST NOT HAVE A BOM.
#   Windows PowerShell 5.1 reads a BOM-less script as ANSI; a CJK character in a comment
#   silently eats the following statement (measured in this repo). English comments only.

[CmdletBinding()]
param(
    # Where the model file must end up. Default: the Release build's exe folder.
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Sidecar\bin\Release\net8.0-windows\models\ModelBiRefNet_1024x1024_fp16.onnx'),

    # Download source. Overridable so a test can point at a local file:// URL.
    [string]$Url = 'https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/main/onnx/model_fp16.onnx',

    # Expected sha256 (compared case-insensitively).
    [string]$Sha256 = '3654C741EB80BD926ADA8FED1713B506CCF8D30EB1F6487E87EB9F234F33DF09',

    # Expected length in bytes. Checked as well as the hash, because a download that silently
    # stopped at 95% would otherwise only surface as "the model is corrupt" much later.
    [long]$Bytes = 489666272,

    # Copy a file you already have instead of downloading.
    [string]$ExistingPath,

    # Re-fetch even when the destination is already present and correct.
    [switch]$Force,

    # Do not print anything except on failure.
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
    Write-Host "get-rembg-model-birefnet: FAILED: $Message"
    exit $Code
}

$destFull = [System.IO.Path]::GetFullPath($Destination)
$expected = $Sha256.Trim().ToUpperInvariant()

# ------------------------------------------------------------------ already there and correct?
if ((Test-Path -LiteralPath $destFull) -and -not $Force) {
    $info = Get-Item -LiteralPath $destFull
    $hash = Get-Sha256Of $destFull
    if (($hash.ToUpperInvariant() -eq $expected) -and ($info.Length -eq $Bytes)) {
        Write-Line "get-rembg-model-birefnet: already present and verified: $destFull"
        Write-Line ("get-rembg-model-birefnet: length={0} sha256={1}" -f $info.Length, $hash)
        exit 0
    }
    Write-Line ("get-rembg-model-birefnet: the file at that path is NOT the expected model " +
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
# always cleaned up: a half-written 489 MB file must never be left where the tool would find it.
$stamp = [System.Guid]::NewGuid().ToString('N')
$temp = Join-Path $destDir ("birefnet.tmp-fetch-" + $stamp)

try {
    if ($ExistingPath) {
        $src = [System.IO.Path]::GetFullPath($ExistingPath)
        if (-not (Test-Path -LiteralPath $src)) { Fail "ExistingPath not found: $src" 2 }
        Write-Line "get-rembg-model-birefnet: copying from $src"
        Copy-Item -LiteralPath $src -Destination $temp -Force
    }
    else {
        Write-Line "get-rembg-model-birefnet: downloading $Url (489.7 MB, this takes a while)"
        # curl.exe rather than Invoke-WebRequest: measured 21.6 s for the whole file on this
        # machine, and it resumes (--retry + -C -) instead of restarting a half-finished 490 MB
        # download. ProgressPreference is irrelevant for curl, but is kept silent anyway.
        $old = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            & curl.exe -L --fail --retry 5 --retry-delay 3 --retry-all-errors --no-progress-meter -o $temp $Url
            if ($LASTEXITCODE -ne 0) { Fail "curl exited with $LASTEXITCODE" 3 }
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
Write-Line "get-rembg-model-birefnet: OK"
Write-Line ("get-rembg-model-birefnet: path={0}" -f $final.FullName)
Write-Line ("get-rembg-model-birefnet: length={0} (expected {1})" -f $final.Length, $Bytes)
Write-Line ("get-rembg-model-birefnet: sha256={0}" -f (Get-Sha256Of $destFull))
Write-Line "get-rembg-model-birefnet: use it with --remove-bg-model <path>; MODNet stays the default"
exit 0
