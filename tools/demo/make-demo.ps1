param(
    # 示範動畫的語言。zh-Hant＝原本的行為（預設），en＝全英文版。
    [ValidateSet('zh-Hant', 'en')]
    [string]$Lang = 'zh-Hant',

    # 工作目錄（含 raw／progress／composed）。預設依語言分開，所以兩種語言不會互相蓋掉輸入格子。
    [string]$WorkRoot = '',

    # 用產品自己重新畫一次輸入格子（--wheel-demo／--progress-dump）。預設不畫，只讀現成的。
    [switch]$RenderCells,

    # 產品執行檔（--wheel-dump-raw 與 --progress-dump 的來源）。
    [string]$ConfernExe = '',

    # 每一次產品呼叫的硬性上限；逾時就 Kill。
    [int]$TimeoutMs = 30000,

    # 給了路徑才輸出影片（沒給就只產生 composed\ 的 PNG 幀，與原本用法相同）。
    [string]$Mp4 = '',
    [string]$Gif = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 把產品自己畫的輪盤格子（--wheel-dump-raw，保 alpha）疊成一段示範動畫。
# 輸入：<work>\raw（fmt0..4、tool0..5）、<work>\progress（進度列）
# 輸出：<work>\composed\f0000.png ...
#
# 中文版： tools\demo\make-demo.ps1
# 英文版： tools\demo\make-demo.ps1 -Lang en -RenderCells -Mp4 ..\docs\demo\confern-demo-en.mp4 -Gif ..\docs\demo\confern-demo-en.gif

# ── 字幕表 ────────────────────────────────────────────────────────────────
# 兩種語言共用同一份繪製碼（字型、字級、顏色、版面、時間軸都不分岔），只有文字不同。
# 加語言＝加一個 key；不要在這裡放任何版面數值。
$captions = @{
    'zh-Hant' = @{
        trigger    = '① 按住 Alt'
        triggerSub = '觸發鍵可換成 Space／Tab／CapsLock／Escape／Shift'
        format     = '② 移到想轉的格式'
        formatSub  = '停在你想轉的那一格，中央顯示結果'
        tools      = '③ 拖曳途中按 Shift'
        toolsSub   = '切到工具輪盤'
        pick       = '④ 選工具'
        pickSub    = '旋轉／清除／壓縮／裁切／讀 QR／去背'
        release    = '⑤ 放開就開始轉檔'
        releaseSub = '3 個檔案'
        done       = '⑥ 完成'
        doneSub    = '轉好的檔案就在來源檔旁邊，不會覆蓋既有檔'
    }
    'en' = @{
        trigger    = '① Hold Alt'
        triggerSub = 'Trigger key can be Space / Tab / CapsLock / Escape / Shift'
        format     = '② Move to the format you want'
        formatSub  = 'Rest on a sector - the centre shows the result'
        tools      = '③ Press Shift while dragging'
        toolsSub   = 'Switch to the tool wheel'
        pick       = '④ Pick a tool'
        pickSub    = 'Rotate / Clean / Compress / Crop / Read QR / Cutout'
        release    = '⑤ Release to start converting'
        releaseSub = '3 files'
        done       = '⑥ Done'
        doneSub    = 'Converted files stay beside the source, never overwritten'
    }
}
$cap = $captions[$Lang]

if (-not $WorkRoot) {
    $leaf    = if ($Lang -eq 'zh-Hant') { 'confern-anim' } else { 'confern-anim-' + $Lang }
    $WorkRoot = Join-Path $env:TEMP $leaf
}
if (-not $ConfernExe) {
    $ConfernExe = Join-Path $PSScriptRoot '..\..\src\Sidecar\bin\Release\net8.0-windows\Confern.exe'
}
$ConfernExe = [System.IO.Path]::GetFullPath($ConfernExe)

$w      = $WorkRoot
$rawDir = Join-Path $w 'raw'
$prDir  = Join-Path $w 'progress'
$outDir = Join-Path $w 'composed'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ── 產品格子：用產品的 --settings-file 換介面語言，重畫同樣的格子 ──────────
# 為什麼要複製本機既有設定再改語言：配色（palette=celadon）與觸發鍵都要跟中文版一致，
# 只換 Language 才能保證「同一張圖、只是換語言」。
function Write-LanguageSettings([string]$Path, [string]$Language) {
    $live = Join-Path $env:LOCALAPPDATA 'Confern\settings.json'
    $cfg  = if (Test-Path -LiteralPath $live) { Get-Content -LiteralPath $live -Raw -Encoding UTF8 | ConvertFrom-Json }
            else { [pscustomobject]@{} }
    $cfg | Add-Member -NotePropertyName Language -NotePropertyValue $Language -Force
    ($cfg | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $Path -Encoding UTF8
}

# 硬性逾時：--wheel-demo 少了就會等的旗標（例如只給 --wheel-dump）不可以卡住整個產生流程。
function Invoke-Confern([string[]]$ConfernArgs, [string]$LogPath) {
    if (-not (Test-Path -LiteralPath $ConfernExe)) { throw "Confern.exe not found: $ConfernExe" }
    $argv = @('--settings-file', $script:settingsFile, '--quiet')
    if ($LogPath) { $argv += @('--log', $LogPath) }
    $argv += $ConfernArgs
    # Start-Process 自己拼接命令列，含空白的參數要自己加引號。
    $quoted = @($argv | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath $ConfernExe -ArgumentList $quoted -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutMs)) {
        try { $p.Kill() } catch { }
        throw ("Confern did not exit within {0} ms -> killed: {1}" -f $TimeoutMs, ($ConfernArgs -join ' '))
    }
    if ($p.ExitCode -ne 0) {
        throw ("Confern exited {0}: {1}" -f $p.ExitCode, ($ConfernArgs -join ' '))
    }
}

function Assert-Cell([string]$Path, [int]$MinBytes) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "cell was not written: $Path" }
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -lt $MinBytes) { throw ("cell looks empty: {0} ({1}B < {2}B)" -f $Path, $len, $MinBytes) }
    return $len
}

function Invoke-RenderCells([string]$Root, [string]$Language) {
    $r = Join-Path $Root 'raw'
    $p = Join-Path $Root 'progress'
    $l = Join-Path $Root 'logs'
    New-Item -ItemType Directory -Path $r, $p, $l -Force | Out-Null

    $script:settingsFile = Join-Path $Root ("settings-{0}.json" -f $Language)
    Write-LanguageSettings $script:settingsFile $Language
    "cells: language=$Language settings=$($script:settingsFile)"

    # 格子的固定參數：中文版就是這樣畫的，照抄才不會換到顏色與玻璃。
    #   --trigger-key Alt   鍵帽上的字（少了它就退回命令列預設 'space'）
    #   --wheel-backdrop dark  玻璃底色用合成深色（與原本那批格子逐位元相同）
    # ⚠ 一定要同時給 --wheel-demo X Y；只給 --wheel-dump-raw 會等不到畫面。
    $fixed = @('--trigger-key', 'Alt', '--wheel-backdrop', 'dark', '--wheel-demo', '400', '400')

    # 格式輪盤 5 格（來源格式 HEIC、目標 PNG,JPG,BMP,TIF,PDF、預覽 3 個檔案、selected=0..4）。
    for ($i = 0; $i -lt 5; $i++) {
        $out = Join-Path $r ("fmt{0}.png" -f $i)
        Invoke-Confern ($fixed + @('--wheel-source', 'HEIC',
                         '--wheel-selected', "$i", '--wheel-previews', '3',
                         '--wheel-dump-raw', $out)) (Join-Path $l ("fmt{0}.log" -f $i))
        $n = Assert-Cell $out 2000
        "  fmt$i  $n B"
    }

    # 工具輪盤 6 格（--wheel-tools、來源 JPG、selected=0..5）。
    for ($i = 0; $i -lt 6; $i++) {
        $out = Join-Path $r ("tool{0}.png" -f $i)
        Invoke-Confern ($fixed + @('--wheel-tools', '--wheel-source', 'JPG',
                         '--wheel-selected', "$i",
                         '--wheel-dump-raw', $out)) (Join-Path $l ("tool{0}.log" -f $i))
        $n = Assert-Cell $out 2000
        "  tool$i $n B"
    }

    # 進度列 5 種（running／success／partial／error／faded）——一次 --progress-dump 全部畫出來。
    Invoke-Confern @('--progress-dump', $p) (Join-Path $l 'progress.log')
    foreach ($n in @('running', 'partial', 'success', 'error', 'faded')) {
        $n2 = Assert-Cell (Join-Path $p ("progress-{0}.png" -f $n)) 500
        "  progress-$n $n2 B"
    }
}

if ($RenderCells) { Invoke-RenderCells $WorkRoot $Lang }
$script:settingsFile = $null

$W = 720; $H = 860
$fps = 25

$bg      = [System.Drawing.Color]::FromArgb(18, 20, 26)      # #12141A
$ink     = [System.Drawing.Color]::FromArgb(242, 243, 245)
$inkDim  = [System.Drawing.Color]::FromArgb(152, 160, 172)
$accent  = [System.Drawing.Color]::FromArgb(120, 220, 190)

$fmt  = @(); for ($i = 0; $i -lt 5; $i++) { $fmt  += [System.Drawing.Image]::FromFile((Join-Path $rawDir ("fmt{0}.png"  -f $i))) }
$tool = @(); for ($i = 0; $i -lt 6; $i++) { $tool += [System.Drawing.Image]::FromFile((Join-Path $rawDir ("tool{0}.png" -f $i))) }
$prog = @{}
foreach ($n in @('running','partial','success')) { $prog[$n] = [System.Drawing.Image]::FromFile((Join-Path $prDir ("progress-{0}.png" -f $n))) }

$fontTitle = New-Object System.Drawing.Font('Microsoft JhengHei', 21, [System.Drawing.FontStyle]::Bold)
$fontSub   = New-Object System.Drawing.Font('Microsoft JhengHei', 13, [System.Drawing.FontStyle]::Regular)
$fontSmall = New-Object System.Drawing.Font('Microsoft JhengHei', 12, [System.Drawing.FontStyle]::Regular)

function New-Canvas { New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) }

function Draw-Caption($g, [string]$title, [string]$sub) {
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $brush = New-Object System.Drawing.SolidBrush($ink)
    $g.DrawString($title, $fontTitle, $brush, (New-Object System.Drawing.RectangleF(40, 746, ($W - 80), 44)), $sf)
    if ($sub) {
        $b2 = New-Object System.Drawing.SolidBrush($inkDim)
        $g.DrawString($sub, $fontSub, $b2, (New-Object System.Drawing.RectangleF(40, 794, ($W - 80), 34)), $sf)
        $b2.Dispose()
    }
    $brush.Dispose(); $sf.Dispose()
}

# 輪盤原圖 304x349；光標在圖內 (24,24)。放大兩倍後畫在畫布上。
$scale = 2.0
$wheelW = [int](304 * $scale); $wheelH = [int](349 * $scale)
$wheelX = 56; $wheelY = 40
$cursorX = $wheelX + [int](24 * $scale)
$cursorY = $wheelY + [int](24 * $scale)

function Draw-Wheel($g, $img, [double]$alpha, [double]$scaleFactor) {
    if ($alpha -le 0.01 -or $img -eq $null) { return }
    $sw = [int]($wheelW * $scaleFactor); $sh = [int]($wheelH * $scaleFactor)
    $dx = $wheelX + [int](($wheelW - $sw) / 2); $dy = $wheelY + [int](($wheelH - $sh) / 2)
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $cm.Matrix33 = [float]$alpha
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetColorMatrix($cm)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle($dx, $dy, $sw, $sh)), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose()
}

function Draw-Cursor($g) {
    $p = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 246, 250), 1.6)
    $g.DrawEllipse($p, ($cursorX - 5), ($cursorY - 5), 10, 10)
    $p.Dispose()
    $b = New-Object System.Drawing.SolidBrush($accent)
    $g.FillEllipse($b, ($cursorX - 1.5), ($cursorY - 1.5), 3, 3)
    $b.Dispose()
}

function Draw-Pill($g, $img, [double]$alpha, [double]$scaleFactor) {
    if ($alpha -le 0.01 -or $img -eq $null) { return }
    $sw = [int]($img.Width * 2 * $scaleFactor); $sh = [int]($img.Height * 2 * $scaleFactor)
    $dx = [int](($W - $sw) / 2); $dy = [int](420 + (108 - $sh) / 2)
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $cm.Matrix33 = [float]$alpha
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetColorMatrix($cm)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle($dx, $dy, $sw, $sh)), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose()
}

# 時間軸（秒）—— 影片直接從「輪盤出現」開始，沒有前面拖檔案的鋪陳。
$tSweep   = 0.76
$tShift   = 2.68
$tTools   = 3.56
$tRelease = 5.48
$tPartial = 6.12
$tSuccess = 7.00
$tEnd     = 8.40

$totalFrames = [int][Math]::Round($tEnd * $fps)

for ($f = 0; $f -lt $totalFrames; $f++) {
    $t = $f / $fps
    $bmp = New-Canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear($bg)

    $ttl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 96, 108))
    $g.DrawString('Confern', $fontSmall, $ttl, 40, 22)
    $ttl.Dispose()

    $title = ''; $sub = ''
    $wheelImg = $null; $wheelAlpha = 1.0; $wheelScale = 1.0
    $pillName = $null; $pillAlpha = 0.0; $pillScale = 1.0
    $showCursor = $true

    if ($t -lt $tSweep) {
        $title = $cap.trigger
        $sub = $cap.triggerSub
        $k = $t / $tSweep
        $wheelImg = $fmt[0]
        # 第一格就要看得到輪盤（alpha 從 0.55 起跳），否則開頭會像空白畫面
        $wheelAlpha = 0.55 + 0.45 * [Math]::Min(1.0, $k * 2.0)
        $wheelScale = 0.72 + 0.28 * [Math]::Min(1.0, $k * 1.4)
    }
    elseif ($t -lt $tShift) {
        $title = $cap.format
        $sub = $cap.formatSub
        $k = ($t - $tSweep) / ($tShift - $tSweep)
        $idx = [Math]::Min(4, [int]($k * 5))
        $wheelImg = $fmt[$idx]
    }
    elseif ($t -lt $tTools) {
        $title = $cap.tools
        $sub = $cap.toolsSub
        $k = ($t - $tShift) / ($tTools - $tShift)
        $wheelImg = $fmt[4]
        if ($k -gt 0.5) { $wheelImg = $tool[0] }
        $wheelScale = 1.0 - 0.06 * [Math]::Sin([Math]::PI * $k)
    }
    elseif ($t -lt $tRelease) {
        $title = $cap.pick
        $sub = $cap.pickSub
        $k = ($t - $tTools) / ($tRelease - $tTools)
        $idx = [Math]::Min(5, [int]($k * 6))
        $wheelImg = $tool[$idx]
    }
    elseif ($t -lt $tPartial) {
        $title = $cap.release
        $sub = ''
        $k = ($t - $tRelease) / ($tPartial - $tRelease)
        $wheelImg = $tool[5]
        $wheelAlpha = [Math]::Max(0.0, 1.0 - $k * 2.2)
        $wheelScale = 1.0 - 0.12 * $k
        $pillName = 'running'; $pillAlpha = [Math]::Min(1.0, $k * 2.5); $pillScale = 0.9 + 0.1 * [Math]::Min(1.0, $k * 2.5)
        $showCursor = $false
    }
    elseif ($t -lt $tSuccess) {
        $title = $cap.release
        $sub = $cap.releaseSub
        $pillName = 'partial'; $pillAlpha = 1.0; $pillScale = 1.0
        $showCursor = $false
    }
    else {
        $title = $cap.done
        $sub = $cap.doneSub
        $pillName = 'success'; $pillAlpha = 1.0; $pillScale = 1.0
        $showCursor = $false
    }

    if ($wheelImg -ne $null) { Draw-Wheel $g $wheelImg $wheelAlpha $wheelScale }
    if ($pillName) { Draw-Pill $g $prog[$pillName] $pillAlpha $pillScale }
    if ($showCursor) { Draw-Cursor $g }
    Draw-Caption $g $title $sub

    $g.Dispose()
    $bmp.Save((Join-Path $outDir ("f{0:D4}.png" -f $f)), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($i in $fmt) { $i.Dispose() }
foreach ($i in $tool) { $i.Dispose() }
foreach ($k in $prog.Keys) { $prog[$k].Dispose() }
$fontTitle.Dispose(); $fontSub.Dispose(); $fontSmall.Dispose()

"frames: $totalFrames in $outDir  ($([Math]::Round($tEnd,2)) s @ $fps fps)  lang=$Lang"

# ── 影片輸出（選用）──────────────────────────────────────────────────────
# 參數刻意跟中文版一致：25 fps、720x860；gif 取樣 12 fps 並縮成一半（360x430）。
if ($Mp4 -or $Gif) {
    $ff = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)
    if (-not $ff) { throw 'ffmpeg.exe not found on PATH' }
    $pattern = Join-Path $outDir 'f%04d.png'
    $common  = @('-y', '-hide_banner', '-loglevel', 'error', '-framerate', "$fps", '-i', $pattern)
    if ($Mp4) {
        $dir = Split-Path -Parent $Mp4
        if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        & $ff.Source @common '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', $Mp4
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $Mp4 (exit $LASTEXITCODE)" }
        "mp4: $Mp4  $((Get-Item -LiteralPath $Mp4).Length) B"
    }
    if ($Gif) {
        $dir = Split-Path -Parent $Gif
        if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $vf = 'fps=12,scale=360:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a'
        & $ff.Source @common '-vf', $vf, '-loop', '0', $Gif
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $Gif (exit $LASTEXITCODE)" }
        "gif: $Gif  $((Get-Item -LiteralPath $Gif).Length) B"
    }
}
