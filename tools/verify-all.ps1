# verify-all.ps1 -- run every verification suite against ONE exe and report a totals table
#
# Why this exists: the full sweep used to live in %TEMP%\sweep-i18n.ps1, which is to say it
# lived nowhere. Two consequences happened for real:
#   * one run of verify-m1b reported "13/13 all green" because the sweep forgot `-Section All`,
#     so only the first section ran -- a smaller total that still exited 0;
#   * a suite that stopped registering checks (a regex around one block stopped matching) just
#     produced a smaller number and was read as "all green".
# Both are the same shape: a total that got smaller without anyone noticing. This runner pins
# nothing itself (each suite pins its own ExpectedChecks); what it refuses to accept is a suite
# that does not print a summary line at all, or one that exits non-zero.
#
# It also owns the two rituals that must happen before EVERY suite (the user's standing rule):
#   1. kill Confern first -- otherwise the suite measures a stale resident process;
#   2. print the exe's timestamp once at the top, so a run of an old binary is visible.
#
# ASCII only on purpose: a BOM-less CJK .ps1 is read as CP950 by PowerShell 5.1.
#
# Usage:
#   powershell -File tools\verify-all.ps1 -Exe <path to Confern.exe>
#   powershell -File tools\verify-all.ps1 -Exe <path> -Only cli,m1d
#   powershell -File tools\verify-all.ps1 -Exe <path> -Skip m1a,m1h

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Work = (Join-Path $env:TEMP 'confern-sweep'),
    [string[]]$Only = @(),
    [string[]]$Skip = @()
)

$ErrorActionPreference = 'Continue'
# $PSCommandPath is tools\verify-all.ps1 itself, so the repository root is one level further up.
# (Getting this backwards makes every suite "MISSING", which is what the first run of this
# runner reported -- a total of 0 that still printed a neat table.)
$tools = Split-Path -Parent $PSCommandPath
$repo = Split-Path -Parent $tools

# With -File, PowerShell hands a comma-separated list over as ONE string, so -contains never
# matches. Split it here, or -Skip silently skips nothing.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })

# `args` here is the suite's own command line after the common ones -- m1b MUST get
# `-Section All`, because its default only runs the first section and reports a smaller,
# still-green total (that is exactly how a false green was produced on 2026-10-22).
$suites = @(
    [pscustomobject]@{ Name = 'cli';        Script = 'verify-cli.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'watch';      Script = 'verify-watch.ps1';           Extra = @() }
    [pscustomobject]@{ Name = 'm1a';        Script = 'verify-m1a.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm1b';        Script = 'verify-m1b.ps1';             Extra = @('-Section', 'All') }
    [pscustomobject]@{ Name = 'm1c';        Script = 'verify-m1c.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm1d';        Script = 'verify-m1d.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm1f';        Script = 'verify-m1f.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm1g';        Script = 'verify-m1g.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm1h';        Script = 'verify-m1h.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm3c';        Script = 'verify-m3c.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'm3d';        Script = 'verify-m3d.ps1';             Extra = @() }
    [pscustomobject]@{ Name = 'orientation';Script = 'verify-orientation-matrix.ps1'; Extra = @(); Kind = 'matrix' }
    [pscustomobject]@{ Name = 'merge';      Script = 'verify-merge-images.ps1';    Extra = @() }
    [pscustomobject]@{ Name = 'folder-drop';Script = 'verify-folder-drop.ps1';     Extra = @() }
    # 2026-10-23: the "recent conversions" list (store + `confern recent` + the tray half that needs
    # no mouse). It moves the user's own %LOCALAPPDATA%\Confern\recent.json aside and puts it back.
    [pscustomobject]@{ Name = 'recent';     Script = 'verify-recent.ps1';          Extra = @() }
    # 2026-10-23: per-source-format presets (the wheel's initial sector, plus the preset quality).
    # It points CONFERN_PRESETS at its own file and asserts the real one is byte-identical afterwards.
    [pscustomobject]@{ Name = 'presets';    Script = 'verify-presets.ps1';         Extra = @() }
    # 2026-11-03: the free-trial meter for the paid tool (CUTOUT, 10 uses). Both doors are covered -
    # `--remove-bg` and the wheel's own commit - plus "a failed run spends nothing" and "editing
    # the file fails closed". The sweep points CONFERN_USAGE at its own file (below), so a suite that
    # runs the paid tool dozens of times can never eat the user's ten free uses.
    [pscustomobject]@{ Name = 'usage';      Script = 'verify-usage.ps1';           Extra = @() }
)

if (-not (Test-Path $Exe)) { "FATAL: exe not found: $Exe"; exit 2 }
$exePath = (Resolve-Path $Exe).Path
"exe : $exePath"
"stamp: $((Get-Item $exePath).LastWriteTime)   (if this is not the build you just made, stop)"
""
New-Item -ItemType Directory -Path $Work -Force | Out-Null

# The suites really convert files (dozens of times each). Their outputs are temp paths and have no
# business in the user's own "recent conversions" list -- which is exactly what happened on
# 2026-10-23: after one sweep the tray submenu offered 11 leftover entries from
# %TEMP%\confern-folder-drop. The override is an ENVIRONMENT VARIABLE on purpose: it is inherited by
# every suite and by every Confern they start, so there is no per-call argument to forget.
$env:CONFERN_RECENT = Join-Path $Work 'recent.json'
"recent store: $env:CONFERN_RECENT (the user list is not touched)"
Remove-Item $env:CONFERN_RECENT -Force -ErrorAction SilentlyContinue

# Same reasoning for the per-format presets: the wheel's initial sector now depends on presets.json,
# so a user who has a preset would turn every other suite's "starts on sector 0" check red. An absent
# file here means "no presets", i.e. exactly the behaviour before the feature existed.
$env:CONFERN_PRESETS = Join-Path $Work 'presets.json'
"preset store: $env:CONFERN_PRESETS (no presets, the user file is not touched)"
Remove-Item $env:CONFERN_PRESETS -Force -ErrorAction SilentlyContinue

# And for the trial meter (2026-11-03): CUTOUT is a paid tool with 10 free uses, so every suite that
# runs it would otherwise spend the user's own uses and, after ten, start failing on exit 12.
# The sweep starts from a full meter; verify-usage moves it around inside its own run.
$env:CONFERN_USAGE = Join-Path $Work 'usage.json'
"usage meter: $env:CONFERN_USAGE (the user's own free uses are not touched)"
Remove-Item $env:CONFERN_USAGE -Force -ErrorAction SilentlyContinue

$rows = @()
foreach ($s in $suites) {
    if ($Only.Count -gt 0 -and $Only -notcontains $s.Name) { continue }
    if ($Skip -contains $s.Name) { continue }
    $script = Join-Path $tools $s.Script
    if (-not (Test-Path $script)) {
        $rows += [pscustomobject]@{ Name = $s.Name; Exit = 'MISSING'; Summary = '' }
        continue
    }

    # The standing rule: no stale resident may be alive when a suite measures the product.
    Get-Process Confern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700

    $log = Join-Path $Work ($s.Name + '.console.txt')
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Exe', $exePath) + $s.Extra
    $out = & powershell @argv 2>&1
    $code = $LASTEXITCODE
    $out | Set-Content -LiteralPath $log -Encoding UTF8

    # Two summary shapes exist: "checks=N pass=N fail=N" (most suites) and
    # "orientation matrix: 8/8 correct". Requiring the first shape everywhere made the
    # orientation suite look like it had run zero checks while exiting 0.
    $summary = $out | Select-String -Pattern 'checks=\d+' | Select-Object -Last 1
    if (-not $summary) { $summary = $out | Select-String -Pattern 'matrix: \d+/\d+ correct' | Select-Object -Last 1 }
    $line = ''
    if ($summary) { $line = $summary.Line.Trim() }
    $rows += [pscustomobject]@{ Name = $s.Name; Exit = $code; Summary = $line }
    "  {0,-12} exit={1,-4} {2}" -f $s.Name, $code, $line
}

""
"== totals =="
$bad = @()
foreach ($r in $rows) {
    $checks = 0; $pass = 0; $fail = 0; $ok = $false
    $m = [regex]::Match($r.Summary, 'checks=(\d+) pass=(\d+) fail=(\d+)')
    if ($m.Success) {
        $checks = [int]$m.Groups[1].Value; $pass = [int]$m.Groups[2].Value; $fail = [int]$m.Groups[3].Value
        $ok = $true
    }
    $mm = [regex]::Match($r.Summary, 'matrix: (\d+)/(\d+) correct')
    if ($mm.Success) {
        $pass = [int]$mm.Groups[1].Value; $checks = [int]$mm.Groups[2].Value; $fail = $checks - $pass
        $ok = $true
    }
    "  {0,-12} checks={1,-4} pass={2,-4} fail={3,-3} exit={4}" -f $r.Name, $checks, $pass, $fail, $r.Exit
    # Exit 3 is a suite's OWN way of saying "green, but N checks need real mouse/keyboard input"
    # (m1h uses it). Treat it as green only when it also reports zero failures.
    $acceptExit = ($r.Exit -eq 0) -or ($r.Exit -eq 3 -and $fail -eq 0)
    # A suite that prints no summary at all is NOT green: it may have died before reaching it.
    if (-not $acceptExit -or -not $ok -or $fail -gt 0) { $bad += $r.Name }
}

""
if ($bad.Count -gt 0) {
    "NOT GREEN: " + ($bad -join ', ')
    exit 1
}
"all suites green ($($rows.Count) suites)"
exit 0
