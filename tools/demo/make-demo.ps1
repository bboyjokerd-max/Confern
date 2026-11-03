$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 把產品自己畫的輪盤格子（--wheel-dump-raw，保 alpha）疊成一段示範動畫。
# 輸入：%TEMP%\confern-anim\raw（fmt0..4、tool0..5）、%TEMP%\confern-anim\progress（五種進度列）
# 輸出：%TEMP%\confern-anim\composed\f0000.png ...

$w      = Join-Path $env:TEMP 'confern-anim'
$rawDir = Join-Path $w 'raw'
$prDir  = Join-Path $w 'progress'
$outDir = Join-Path $w 'composed'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

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
        $title = '① 按住 Alt'
        $sub = '觸發鍵可換成 Space／Tab／CapsLock／Escape／Shift'
        $k = $t / $tSweep
        $wheelImg = $fmt[0]
        # 第一格就要看得到輪盤（alpha 從 0.55 起跳），否則開頭會像空白畫面
        $wheelAlpha = 0.55 + 0.45 * [Math]::Min(1.0, $k * 2.0)
        $wheelScale = 0.72 + 0.28 * [Math]::Min(1.0, $k * 1.4)
    }
    elseif ($t -lt $tShift) {
        $title = '② 移到想轉的格式'
        $sub = '停在你想轉的那一格，中央顯示結果'
        $k = ($t - $tSweep) / ($tShift - $tSweep)
        $idx = [Math]::Min(4, [int]($k * 5))
        $wheelImg = $fmt[$idx]
    }
    elseif ($t -lt $tTools) {
        $title = '③ 拖曳途中按 Shift'
        $sub = '切到工具輪盤'
        $k = ($t - $tShift) / ($tTools - $tShift)
        $wheelImg = $fmt[4]
        if ($k -gt 0.5) { $wheelImg = $tool[0] }
        $wheelScale = 1.0 - 0.06 * [Math]::Sin([Math]::PI * $k)
    }
    elseif ($t -lt $tRelease) {
        $title = '④ 選工具'
        $sub = '旋轉／清除／壓縮／裁切／讀 QR／去背'
        $k = ($t - $tTools) / ($tRelease - $tTools)
        $idx = [Math]::Min(5, [int]($k * 6))
        $wheelImg = $tool[$idx]
    }
    elseif ($t -lt $tPartial) {
        $title = '⑤ 放開就開始轉檔'
        $sub = ''
        $k = ($t - $tRelease) / ($tPartial - $tRelease)
        $wheelImg = $tool[5]
        $wheelAlpha = [Math]::Max(0.0, 1.0 - $k * 2.2)
        $wheelScale = 1.0 - 0.12 * $k
        $pillName = 'running'; $pillAlpha = [Math]::Min(1.0, $k * 2.5); $pillScale = 0.9 + 0.1 * [Math]::Min(1.0, $k * 2.5)
        $showCursor = $false
    }
    elseif ($t -lt $tSuccess) {
        $title = '⑤ 放開就開始轉檔'
        $sub = '3 個檔案'
        $pillName = 'partial'; $pillAlpha = 1.0; $pillScale = 1.0
        $showCursor = $false
    }
    else {
        $title = '⑥ 完成'
        $sub = '轉好的檔案就在來源檔旁邊，不會覆蓋既有檔'
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

"frames: $totalFrames in $outDir  ($([Math]::Round($tEnd,2)) s @ $fps fps)"
