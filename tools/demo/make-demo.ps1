$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$w      = Join-Path $env:TEMP 'confern-anim'
$rawDir = Join-Path $w 'raw'
$prDir  = Join-Path $w 'progress'
$outDir = Join-Path $w 'composed'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$W = 720; $H = 860
$fps = 25
$totalFrames = 240

$bg      = [System.Drawing.Color]::FromArgb(18, 20, 26)      # #12141A
$ink     = [System.Drawing.Color]::FromArgb(242, 243, 245)
$inkDim  = [System.Drawing.Color]::FromArgb(152, 160, 172)
$chip    = [System.Drawing.Color]::FromArgb(38, 42, 50)
$accent  = [System.Drawing.Color]::FromArgb(120, 220, 190)

$fmt  = @(); for ($i = 0; $i -lt 5; $i++) { $fmt  += [System.Drawing.Image]::FromFile((Join-Path $rawDir ("fmt{0}.png"  -f $i))) }
$tool = @(); for ($i = 0; $i -lt 6; $i++) { $tool += [System.Drawing.Image]::FromFile((Join-Path $rawDir ("tool{0}.png" -f $i))) }
$prog = @{}
foreach ($n in @('running','partial','success')) { $prog[$n] = [System.Drawing.Image]::FromFile((Join-Path $prDir ("progress-{0}.png" -f $n))) }

$fontTitle = New-Object System.Drawing.Font('Microsoft JhengHei', 21, [System.Drawing.FontStyle]::Bold)
$fontSub   = New-Object System.Drawing.Font('Microsoft JhengHei', 13, [System.Drawing.FontStyle]::Regular)
$fontChip  = New-Object System.Drawing.Font('Microsoft JhengHei', 12, [System.Drawing.FontStyle]::Regular)
$fontKey   = New-Object System.Drawing.Font('Consolas', 15, [System.Drawing.FontStyle]::Bold)

function New-Canvas { New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) }

function Draw-Caption($g, [string]$title, [string]$sub) {
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $brush = New-Object System.Drawing.SolidBrush($ink)
    $g.DrawString($title, $fontTitle, $brush, (New-Object System.Drawing.RectangleF(40, 746, ($W - 80), 44)), $fmt)
    if ($sub) {
        $b2 = New-Object System.Drawing.SolidBrush($inkDim)
        $g.DrawString($sub, $fontSub, $b2, (New-Object System.Drawing.RectangleF(40, 794, ($W - 80), 34)), $fmt)
        $b2.Dispose()
    }
    $brush.Dispose(); $fmt.Dispose()
}

# 輪盤原圖 304x349；光標在圖內的 (24,24)。放大兩倍後畫在畫布上，讓光標落在 (160,170)。
$scale = 2.0
$wheelW = [int](304 * $scale); $wheelH = [int](349 * $scale)
$wheelX = 56; $wheelY = 40
$cursorX = $wheelX + [int](24 * $scale)
$cursorY = $wheelY + [int](24 * $scale)

function Draw-Wheel($g, $img, [double]$alpha, [double]$scaleFactor) {
    if ($alpha -le 0.01) { return }
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

function Draw-FileChip($g, [double]$x, [double]$y, [double]$alpha, [string]$label) {
    if ($alpha -le 0.01) { return }
    $rect = New-Object System.Drawing.RectangleF([float]$x, [float]$y, 168, 34)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 8
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $c = [System.Drawing.Color]::FromArgb([int](255 * $alpha), $chip.R, $chip.G, $chip.B)
    $b = New-Object System.Drawing.SolidBrush($c)
    $g.FillPath($b, $path)
    $b.Dispose()
    $tc = [System.Drawing.Color]::FromArgb([int](255 * $alpha), $ink.R, $ink.G, $ink.B)
    $tb = New-Object System.Drawing.SolidBrush($tc)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($label, $fontChip, $tb, $rect, $sf)
    $tb.Dispose(); $sf.Dispose(); $path.Dispose()
}

function Draw-Pill($g, $img, [double]$alpha, [double]$scaleFactor) {
    if ($alpha -le 0.01) { return }
    $sw = [int]($img.Width * 2 * $scaleFactor); $sh = [int]($img.Height * 2 * $scaleFactor)
    $dx = [int](($W - $sw) / 2); $dy = [int](420 + (108 - $sh) / 2)
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $cm.Matrix33 = [float]$alpha
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetColorMatrix($cm)
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle($dx, $dy, $sw, $sh)), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose()
}

# 時間軸（秒）
$tDragStart = 0.00; $tHold = 0.92; $tSweep = 1.72; $tShift = 3.64; $tTools = 4.60
$tRelease = 6.60; $tPartial = 7.28; $tSuccess = 8.20

for ($f = 0; $f -lt $totalFrames; $f++) {
    $t = $f / $fps
    $bmp = New-Canvas
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear($bg)

    # 標題
    $ttl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 96, 108))
    $g.DrawString('Confern', $fontChip, $ttl, 40, 22)
    $ttl.Dispose()

    $title = ''; $sub = ''
    $wheelImg = $null; $wheelAlpha = 1.0; $wheelScale = 1.0
    $pillName = $null; $pillAlpha = 0.0; $pillScale = 1.0
    $chipAlpha = 0.0; $chipX = 470; $chipY = 96
    $showCursor = $true

    if ($t -lt $tHold) {
        $title = '① 把檔案拖到桌面空白處'
        $sub = '拖一個 HEIC 檔到桌面'
        $k = ($t - $tDragStart) / $tHold
        $chipAlpha = [Math]::Min(1.0, $k * 3)
        $chipX = 420 - 250 * [Math]::Min(1.0, $k * 1.15)
        $chipY = 190 + 30 * [Math]::Min(1.0, $k * 1.15)
        $wheelImg = $fmt[0]; $wheelAlpha = 0.0; $showCursor = $false
        $chipAlpha = [Math]::Min(1.0, $chipAlpha)
    }
    elseif ($t -lt $tSweep) {
        $title = '② 按住 Alt'
        $sub = '觸發鍵可換成 Space／Tab／CapsLock／Escape／Shift'
        $k = ($t - $tHold) / ($tSweep - $tHold)
        $wheelImg = $fmt[0]
        $wheelAlpha = [Math]::Min(1.0, $k * 2.2)
        $wheelScale = 0.18 + 0.82 * [Math]::Min(1.0, $k * 1.25)
        $chipAlpha = 1.0 - [Math]::Min(1.0, $k * 2)
        $chipX = 170; $chipY = 136
    }
    elseif ($t -lt $tShift) {
        $title = '③ 移到想轉的格式'
        $sub = '停在你想轉的那一格，中央顯示結果'
        $k = ($t - $tSweep) / ($tShift - $tSweep)
        $idx = [Math]::Min(4, [int]($k * 5))
        $wheelImg = $fmt[$idx]
        $chipAlpha = 0.0
    }
    elseif ($t -lt $tTools) {
        $title = '④ 拖曳途中按 Shift'
        $sub = '切到工具輪盤'
        $k = ($t - $tShift) / ($tTools - $tShift)
        $wheelAlpha = 1.0
        $wheelImg = $fmt[4]
        if ($k -gt 0.5) { $wheelImg = $tool[0] }
        $wheelScale = 1.0 - 0.06 * [Math]::Sin([Math]::PI * $k)
    }
    elseif ($t -lt $tRelease) {
        $title = '⑤ 選工具'
        $sub = '旋轉／清除／壓縮／裁切／讀 QR／去背'
        $k = ($t - $tTools) / ($tRelease - $tTools)
        $idx = [Math]::Min(5, [int]($k * 6))
        $wheelImg = $tool[$idx]
        $chipAlpha = 0.0
    }
    elseif ($t -lt $tPartial) {
        $title = '⑥ 放開就開始轉檔'
        $sub = ''
        $k = ($t - $tRelease) / ($tPartial - $tRelease)
        $wheelImg = $tool[5]
        $wheelAlpha = [Math]::Max(0.0, 1.0 - $k * 2.2)
        $wheelScale = 1.0 - 0.12 * $k
        $pillName = 'running'; $pillAlpha = [Math]::Min(1.0, $k * 2.5); $pillScale = 0.9 + 0.1 * [Math]::Min(1.0, $k * 2.5)
        $showCursor = $false
    }
    elseif ($t -lt $tSuccess) {
        $title = '⑥ 放開就開始轉檔'
        $sub = '3 個檔案'
        $pillName = 'partial'; $pillAlpha = 1.0; $pillScale = 1.0
        $showCursor = $false
    }
    else {
        $title = '⑦ 完成'
        $sub = '轉好的檔案就在來源檔旁邊，不會覆蓋既有檔'
        $pillName = 'success'; $pillAlpha = 1.0; $pillScale = 1.0
        $showCursor = $false
    }

    if ($wheelImg -ne $null) { Draw-Wheel $g $wheelImg $wheelAlpha $wheelScale }
    if ($pillName) { Draw-Pill $g $prog[$pillName] $pillAlpha $pillScale }
    if ($chipAlpha -gt 0.01) { Draw-FileChip $g $chipX $chipY $chipAlpha 'IMG_2417.HEIC' }
    if ($showCursor) { Draw-Cursor $g }
    Draw-Caption $g $title $sub

    $g.Dispose()
    $bmp.Save((Join-Path $outDir ("f{0:D4}.png" -f $f)), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($i in $fmt) { $i.Dispose() }
foreach ($i in $tool) { $i.Dispose() }
foreach ($k in $prog.Keys) { $prog[$k].Dispose() }
$fontTitle.Dispose(); $fontSub.Dispose(); $fontChip.Dispose(); $fontKey.Dispose()

$made = @(Get-ChildItem $outDir -File -Filter '*.png').Count
"frames: $made of $totalFrames in $outDir"
