# fault-inject.ps1 -- prove that a check can FAIL, by breaking the product on purpose.
#
# WHY THIS EXISTS
#   The falsifiability audit ended with a table of assertions that could never go red. Fixing them
#   was manual work: somebody edited a product file, watched a suite fail, and edited it back. That
#   proof lived in a conversation, not in the repo -- so nothing stopped a later refactor from
#   quietly turning the same checks vacuous again. This script makes "the check is load-bearing"
#   re-runnable: it injects a specific, minimal fault into product source, rebuilds, runs ONE suite,
#   and demands BOTH of these:
#
#     1. the suite really went red (non-zero exit or a FAIL line), and
#     2. the failure is the check we NAMED -- a suite going red for an unrelated reason (a crash, a
#        compile-time surprise, a timeout) proves nothing about the check we care about.
#
#   Then it restores the file, rebuilds, and (unless -SkipFinalGreen) runs the affected suites again
#   to prove the restore -- because a botched restore would leave a broken product behind, and that
#   is a much worse outcome than a failed fault injection.
#
# WHAT A FAULT MUST NOT DO
#   Break the build. A compile error would make the suite fail for the wrong reason, so a fault whose
#   build fails is reported as INVALID, not as a success. Every fault below is a one-line, type-safe
#   change: the code still compiles, and the feature silently stops doing its job -- which is exactly
#   the class of defect the checks are supposed to catch.
#
# SAFETY
#   * Refuses to run on a dirty target file (the user's own edits must never be overwritten).
#   * Writes the original content to a state file BEFORE touching anything, and restores from that
#     file at startup if a previous run was interrupted. The working tree is the deliverable; a
#     half-applied fault left behind by a crash would be a real defect.
#   * Kills Confern first: a resident process locks the exe and the copy fails silently (MSB3021),
#     which is how a "0 errors" build can still be the old binary.
#
# Usage
#   powershell -File tools\fault-inject.ps1 -List
#   powershell -File tools\fault-inject.ps1                    # every fault
#   powershell -File tools\fault-inject.ps1 -Only preset-quality-ignored
#   powershell -File tools\fault-inject.ps1 -SkipFinalGreen    # faster: no restore proof

param(
    [string]$Exe = 'src\Sidecar\bin\Release\net8.0-windows\Confern.exe',
    [string]$Only = '',
    [switch]$SkipFinalGreen,
    [switch]$List
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $env:TEMP 'confern-fault-inject.state.json'

function Say([string]$m) { Write-Host "  $m" }
function Die([string]$m, [int]$code) { Write-Host "FATAL: $m"; exit $code }

# ------------------------------------------------------------------------------------------------
# The fault table. One line of product code each; the feature stops working, the build still passes.
# ------------------------------------------------------------------------------------------------
$faults = @(
    [pscustomobject]@{
        Name     = 'preset-quality-ignored'
        File     = 'src\Sidecar\Presets\PresetStore.cs'
        Old      = '        Preset? preset = For(sourceFormat);'
        New      = '        Preset? preset = null;  // FAULT: reads the store, then ignores it'
        Suite    = 'verify-presets.ps1'
        Expected = 'FAIL  S5\.1 '
        Why      = 'the stored quality must reach the encoder; the suite proves it by file SIZE'
    }
    [pscustomobject]@{
        Name     = 'preset-wheel-sector-ignored'
        File     = 'src\Sidecar\Program.cs'
        Old      = '        for (int i = 0; i < targets.Count; i++)' +
                   "`n        {`n" +
                   '            if (!string.Equals(targets[i], preset.Target, StringComparison.OrdinalIgnoreCase)) continue;'
        New      = '        if (preset != null) return 0;  // FAULT: the preset is read but never used' +
                   "`n        for (int i = 0; i < targets.Count; i++)" +
                   "`n        {`n" +
                   '            if (!string.Equals(targets[i], preset.Target, StringComparison.OrdinalIgnoreCase)) continue;'
        Suite    = 'verify-presets.ps1'
        Expected = 'FAIL  S4\.2 '
        Why      = 'the wheel must START on the preset sector, not merely know about it'
    }
    [pscustomobject]@{
        Name     = 'strings-table-misses-alt'
        File     = 'src\Sidecar\Strings\UiStrings.cs'
        Old      = '        ["vk.Shift"] = "Shift",' + "`n" +
                   '        ["vk.Alt"] = "Alt",' + "`n" +
                   '        ["vk.Ctrl"] = "Ctrl",'
        New      = '        ["vk.Shift"] = "Shift",' + "`n" +
                   '        // FAULT: the English table lost the label the settings page asks for'
        Suite    = 'verify-m1f.ps1'
        Expected = 'FAIL  S14.37e '
        Why      = 'table-to-table alignment is green even when a key is missing from ALL tables'
    }
    [pscustomobject]@{
        Name     = 'trigger-key-not-normalized'
        File     = 'src\Sidecar\Interaction\DragDetector.cs'
        Old      = '    public void OnKey(uint vk, bool isDown)' +
                   "`n    {`n" +
                   '        vk = Native.NormalizeModifierVk(vk);'
        New      = '    public void OnKey(uint vk, bool isDown)' +
                   "`n    {`n" +
                   '        // FAULT: the raw VK is compared again (the behaviour before 2026-10-23)'
        Suite    = 'verify-m1f.ps1'
        Expected = 'FAIL  S14.38 '
        Why      = 'the hook reports the left/right modifier VK, so Alt/Ctrl/Shift must still match'
    }
    [pscustomobject]@{
        Name     = 'settings-page-drops-alt'
        File     = 'src\Sidecar\Settings\SettingsWindow.xaml.cs'
        Old      = '    private static readonly string[] PresetTriggerKeys = { "Space", "Tab", "Alt", "CapsLock", "Escape", "Shift" };'
        New      = '    private static readonly string[] PresetTriggerKeys = { "Space", "Tab", "CapsLock", "Escape", "Shift" };  // FAULT: Alt removed again'
        Suite    = 'verify-m1f.ps1'
        Expected = 'FAIL  S14.38d '
        Why      = 'the page must offer the key it can actually drive (the user asked for Alt here)'
    }
    [pscustomobject]@{
        Name     = 'recent-never-recorded'
        File     = 'src\Sidecar\Recent\RecentStore.cs'
        Old      = '            _entries.Insert(0, new RecentEntry(sourcePath ?? string.Empty, outputPath, tool ?? "convert", DateTime.Now));'
        New      = '            // FAULT: the entry is dropped on the floor'
        Suite    = 'verify-recent.ps1'
        Expected = 'FAIL  S2\.3 '
        Why      = 'a successful conversion must be remembered (the tray submenu depends on it)'
    }
)

if ($List) {
    Say "faults: $($faults.Count)"
    foreach ($f in $faults) {
        Say ''
        Say $f.Name
        Say "  file : $($f.File)"
        Say "  suite: $($f.Suite)   expects: $($f.Expected)"
        Say "  why  : $($f.Why)"
    }
    exit 0
}

# ------------------------------------------------------------------------------------------------
# Recovery from an interrupted run
# ------------------------------------------------------------------------------------------------
function Restore-FromState([string]$why) {
    if (-not (Test-Path -LiteralPath $statePath)) { return $false }
    $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $target = Join-Path $repo $state.File
    $bytes = [Convert]::FromBase64String($state.Content)
    [System.IO.File]::WriteAllBytes($target, $bytes)
    Remove-Item -LiteralPath $statePath -Force
    Say "recovered '$($state.Name)' left behind by an interrupted run ($why): $($state.File) restored"
    return $true
}

if (Restore-FromState 'startup') {
    Say 'rebuilding the restored tree before doing anything else'
    & dotnet build (Join-Path $repo 'src\Sidecar\Sidecar.csproj') -c Release -t:Rebuild --nologo 2>&1 |
        Select-String -Pattern 'error' | Select-Object -First 6 | ForEach-Object { Say $_.Line.Trim() }
}

# ------------------------------------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------------------------------------
# A rebuild replaces the file under a possibly still-running child. The first version of this script
# ran the suite immediately after a build and the suite came back with 32 empty results: every launch
# produced nothing, which reads as "the product is broken" when the truth was "the harness was too
# fast". So every build now ends by PROVING the binary runs, and retries while it settles.
function Wait-ExeReady {
    $last = ''
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $Exe
            $psi.Arguments = 'formats'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.RedirectStandardInput = $true
            $proc = [System.Diagnostics.Process]::Start($psi)
            $proc.StandardInput.Close()
            $o = $proc.StandardOutput.ReadToEndAsync()
            $e = $proc.StandardError.ReadToEndAsync()
            if (-not $proc.WaitForExit(30000)) { try { $proc.Kill() } catch { }; $last = 'formats timed out'; Start-Sleep -Milliseconds 500; continue }
            $null = $proc.WaitForExit()
            if ($proc.ExitCode -eq 0 -and ([string]$o.Result) -match 'IN  \(what you can drag in') {
                return [pscustomobject]@{ Ok = $true; Detail = "formats exit 0 after $($i * 500) ms of settling" }
            }
            $last = "formats exit=$($proc.ExitCode) err=$([string]$e.Result)"
        }
        catch {
            $last = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject]@{ Ok = $false; Detail = $last }
}

function Invoke-Build([string]$tag) {
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $before = if (Test-Path $Exe) { (Get-Item $Exe).LastWriteTimeUtc } else { [datetime]::MinValue }
    $out = & dotnet build (Join-Path $repo 'src\Sidecar\Sidecar.csproj') -c Release -t:Rebuild --nologo 2>&1
    # The exit code, not a localized summary line: this script is ASCII-only on purpose, so it must
    # never depend on the word for "error" in whatever language the SDK prints.
    $code = $LASTEXITCODE
    $text = ($out | ForEach-Object { "$_" }) -join "`n"
    if ($code -ne 0) {
        $first = ($text -split "`n" | Where-Object { $_ -match 'error' } | Select-Object -First 4) -join ' / '
        if (-not $first) { $first = "dotnet build exited $code" }
        return [pscustomobject]@{ Ok = $false; Stamp = $null; Detail = $first }
    }
    $after = (Get-Item $Exe).LastWriteTimeUtc
    if ($after -le $before) {
        return [pscustomobject]@{ Ok = $false; Stamp = $after; Detail = "the exe did not change ($after <= $before) -- the build probably copied into a locked file" }
    }
    $ready = Wait-ExeReady
    if (-not $ready.Ok) {
        return [pscustomobject]@{ Ok = $false; Stamp = $after; Detail = "the build finished but the exe does not run: $($ready.Detail)" }
    }
    return [pscustomobject]@{ Ok = $true; Stamp = $after; Detail = "$tag built at $after (probe: $($ready.Detail))" }
}

function Invoke-Suite([string]$script, [int]$timeoutMs) {
    # Build the command line from already-resolved strings. `"$($x).Path"` inside a double-quoted
    # string appends the literal text '.Path' -- the first version of this script did exactly that,
    # every launch failed, and the "went red" verdicts were still recorded as PROVEN.
    $scriptPath = Join-Path $repo ("tools\" + $script)
    $exePath = (Resolve-Path $Exe).Path
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell'
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '" -Exe "' + $exePath + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Close()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($timeoutMs)) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{ Exit = -1; Text = "TIMEOUT after $timeoutMs ms"; FailLines = @(); Ran = $false; Detail = "the suite timed out after $timeoutMs ms" }
    }
    $null = $proc.WaitForExit()
    $text = [string]$outTask.Result + "`n" + [string]$errTask.Result
    # A suite that printed no summary line did not run; that is a harness fault, and reporting it as
    # "stayed green" or as a verdict would be exactly the kind of lie this project forbids.
    $ran = ($text -match 'checks=|summary:|FATAL')
    if (-not $ran) {
        $head = ($text -split "`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 3) -join ' / '
        if (-not $head) { $head = 'no output at all' }
        return [pscustomobject]@{ Exit = -1; Text = $text; FailLines = @(); Ran = $false; Detail = "the suite produced no summary: $head" }
    }
    $fails = @($text -split "`n" | Where-Object { $_ -match '^\s*FAIL  ' } | ForEach-Object { $_.Trim() })
    return [pscustomobject]@{ Exit = $proc.ExitCode; Text = $text; FailLines = $fails; Ran = $true; Detail = '' }
}

function Read-SourceRaw([string]$abs) {
    $bytes = [System.IO.File]::ReadAllBytes($abs)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = if ($hasBom) { [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3) }
            else { [System.Text.Encoding]::UTF8.GetString($bytes) }
    return [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Write-SourceRaw([string]$abs, [string]$text, [bool]$bom) {
    $enc = New-Object System.Text.UTF8Encoding($bom)
    [System.IO.File]::WriteAllText($abs, $text, $enc)
}

function Test-Clean([string]$rel) {
    $out = & git -C $repo status --porcelain -- $rel
    return (@($out | Where-Object { $_ -ne '' }).Count -eq 0)
}

# ------------------------------------------------------------------------------------------------
# Preconditions
# ------------------------------------------------------------------------------------------------
$exeAbs = if (Test-Path $Exe) { (Resolve-Path $Exe).Path } else { Join-Path $repo $Exe }
if (-not (Test-Path $exeAbs)) { Die "exe not found: $Exe" 2 }
$Exe = $exeAbs

$selected = if ($Only) { @($faults | Where-Object { $_.Name -eq $Only }) } else { $faults }
if ($selected.Count -eq 0) { Die "no fault named '$Only' (try -List)" 2 }

foreach ($f in $selected) {
    if (-not (Test-Clean $f.File)) { Die "'$($f.File)' is dirty -- commit or stash it first, this script must never overwrite your work" 3 }
}

Say "exe   : $Exe"
Say "stamp : $((Get-Item $Exe).LastWriteTimeUtc)  (healthy build)"
Say "faults: $($selected.Count) -> $(($selected | ForEach-Object { $_.Name }) -join ', ')"
Say ''

# ------------------------------------------------------------------------------------------------
# The loop. Each fault: inject -> build -> run -> judge -> restore -> build.
# ------------------------------------------------------------------------------------------------
$results = @()
foreach ($f in $selected) {
    $abs = Join-Path $repo $f.File
    Say "=== $($f.Name) ==="
    Say "  $($f.Why)"

    $original = Read-SourceRaw $abs
    $norm = ($original.Text -replace "`r`n", "`n")
    $oldNorm = $f.Old -replace "`r`n", "`n"
    $newNorm = $f.New -replace "`r`n", "`n"
    $hits = ([regex]::Matches($norm, [regex]::Escape($oldNorm))).Count
    if ($hits -ne 1) {
        Say "  SKIP: the anchor occurs $hits time(s) in $($f.File) (the code moved; fix the table)"
        $results += [pscustomobject]@{ Name = $f.Name; Verdict = 'SKIP'; Detail = "anchor x$hits" }
        continue
    }

    # State file first: if this process dies right after the write below, the next run restores.
    $state = [pscustomobject]@{
        Name = $f.Name; File = $f.File
        Content = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($abs))
    }
    [System.IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

    $injected = $false
    $verdict = 'NOT-PROVEN'
    $detail = ''
    try {
        Write-SourceRaw $abs (($norm.Replace($oldNorm, $newNorm)) -replace "`n", "`r`n") $original.HasBom
        $injected = $true
        Say '  injected'

        $build = Invoke-Build $f.Name
        # No `continue` here: an INVALID fault must still land in the totals table, otherwise the
        # summary quietly reports one fewer case than the table lists.
        if (-not $build.Ok) {
            $verdict = 'INVALID'
            $detail = "the fault did not build: $($build.Detail)"
            Say '  BUILD FAILED -- an unbuildable fault proves nothing about the check'
        }
        else {
        Say '  built'

        $run = Invoke-Suite $f.Suite 600000
        if (-not $run.Ran) {
            $verdict = 'INVALID'
            $detail = "the suite did not run: $($run.Detail)"
            Say "  HARNESS PROBLEM: $($run.Detail)"
        }
        else {
        if ($run.Text -match 'START FAILED|produced no output at all') {
            $verdict = 'INVALID'
            $detail = 'the suite could not launch the exe -- that is a harness fault, not a verdict'
            Say "  HARNESS PROBLEM: $detail"
        }
        else {
        $named = ($run.Text -match $f.Expected)
        $anyFail = ($run.FailLines.Count -gt 0) -or ($run.Exit -ne 0)
        $detail = "suite exit=$($run.Exit) fails=$($run.FailLines.Count) namedMatch=$named"
        if ($named -and $anyFail) {
            $verdict = 'PROVEN'
            Say "  RED as predicted: $(($run.FailLines | Where-Object { $_ -match $f.Expected } | Select-Object -First 1))"
        } elseif ($anyFail) {
            $verdict = 'WRONG-CHECK'
            $detail += '; it went red, but not at the check this fault is supposed to feed'
            Say "  red, but NOT at '$($f.Expected)':"
            $run.FailLines | Select-Object -First 5 | ForEach-Object { Say "     $_" }
        } else {
            Say '  STAYED GREEN -- the check does not depend on what the fault broke'
        }
        }
        }
        }
    }
    finally {
        if ($injected) {
            [System.IO.File]::WriteAllBytes($abs, [Convert]::FromBase64String($state.Content))
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            Say '  restored'
        } else {
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
        }
        if ($injected) {
            $back = Invoke-Build "$($f.Name) (restored)"
            if (-not $back.Ok) { Die "the restore build failed: $($back.Detail) -- the working tree holds a fault right now" 5 }
            Say '  rebuilt from the restored source'
        }
    }
    $results += [pscustomobject]@{ Name = $f.Name; Verdict = $verdict; Detail = $detail }
    Say ''
}

# ------------------------------------------------------------------------------------------------
# Proving the restore: the affected suites must be green again
# ------------------------------------------------------------------------------------------------
if (-not $SkipFinalGreen) {
    $suites = @($selected | ForEach-Object { $_.Suite } | Sort-Object -Unique)
    Say "=== the restore is real: re-running $($suites.Count) suite(s) on the restored build ==="
    foreach ($s in $suites) {
        $run = Invoke-Suite $s 900000
        if (-not $run.Ran) { Die "the restored build could not even run $s ($($run.Detail)) -- harness fault, nothing here is trustworthy" 6 }
        $ok = ($run.Exit -eq 0)
        Say ("  {0,-24} exit={1} fails={2}" -f $s, $run.Exit, $run.FailLines.Count)
        if (-not $ok) {
            $run.FailLines | Select-Object -First 5 | ForEach-Object { Say "     $_" }
            Die "the restored build is not green ($s) -- do not trust anything above until this is fixed" 6
        }
    }
}

# ------------------------------------------------------------------------------------------------
Say ''
Say '== totals =='
foreach ($r in $results) { Say ("  {0,-32} {1,-12} {2}" -f $r.Name, $r.Verdict, $r.Detail) }
$proven = @($results | Where-Object { $_.Verdict -eq 'PROVEN' }).Count
$bad = @($results | Where-Object { $_.Verdict -in @('NOT-PROVEN', 'WRONG-CHECK', 'INVALID', 'SKIP') }).Count
Say ''
Say "faults=$($results.Count) proven=$proven notProven=$bad"
if (Test-Path -LiteralPath $statePath) { Die 'a state file survived the run: a fault may still be applied' 7 }
if ($bad -gt 0) { exit 1 }
exit 0
