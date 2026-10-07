# `read-qr` fixtures（工具 `read-qr` 的測資）

這一目錄是**進版控的測資**，因為使用者要能重現驗收。它們由
`tools/verify-m3d.ps1` 的 **S19** 直接引用（路徑由腳本從 repo 根目錄推導，不寫死絕對路徑）。

## ⚠ 測資的來源（誠實聲明，不可省略）

**這七張圖的 QR 是用 `ZXing.Net` 自己的編碼器產生的**
（`ZXing.MultiFormatWriter.encode(payload, BarcodeFormat.QR_CODE, w, h, hints)` → `BitMatrix`，
再由 WIC 的 `PngBitmapEncoder`／`JpegBitmapEncoder` 寫成 PNG／JPEG）。

這件事的兩個後果要一起說：

- ✅ **對「解碼路徑」是有效測資**：它驗的是「WIC 解出來的 BGRA 像素 → luminance → ZXing 解碼 →
  文字原封不動回到使用者手上」這一整條路，包含格式轉換、stride、`CopyPixels`、
  `RGBLuminanceSource.BitmapFormat.BGRA32` 的位元組排列、以及縮圖／原尺寸兩段式策略。
  這些環節壞掉，這些測資會紅。
- ❌ **不能拿它宣稱「對真實世界照片的辨識率」**：同一套函式庫編碼、同一套函式庫解碼，
  等於考題與解答出自同一支筆。真實情境的變數（列印後再翻拍、螢幕摩爾紋、透視變形、
  局部反光、低對比、曲面）**一張都沒有出現在這裡**。
  「手機拍一張海報上的 QR 讀不讀得到」是**未實測**的，任何人都不該用這份測資回答那個問題。

## 已知的 payload（斷言用）

```
Confern QR fixture 2026-10-07 / payload-v1
```

42 個字元（全 ASCII，所以不涉及編碼猜測：`S19.01` 的逐字元比對不會被 charset 問題汙染）。
`MultiFormatWriter` 的 hints：`CHARACTER_SET=UTF-8`、`ERROR_CORRECTION=M`、
安靜區 4 模組（`EncodeHintType.MARGIN` 的預設值）。

## 檔案

| 檔案 | 尺寸 | 用途 |
| --- | --- | --- |
| `qr-clean-512x512.png` | 512×512 | **(a) 乾淨的 QR**（PNG 無損）。斷言解出來的文字逐字元等於上面的 payload。 |
| `qr-scaled-256x256.png` | 256×256 | **(b) 同一個 payload、被縮放過**（最近鄰，512→256）。 |
| `qr-scaled-jpeg-q40-512x512.jpg` | 512×512 | **(b') 同一個 payload、被 JPEG 壓縮過**（品質 40，有損、色度次取樣）。 |
| `qr-big-2400x2400.png` | 2400×2400 | 大圖（長邊 > 第一輪上限 1600 px），逼出**縮圖那一輪**。 |
| `qr-small-in-scene-160.png` | 3000×2250 | **逼出第二輪**：碼只有 160 px 寬、貼在畫面中央。實測第一輪（縮到 1600×1200）**解不到**，原尺寸那一輪才解到——這是「兩段式策略真的有必要」的當場證據（見下表）。 |
| `no-qr-512x384.png` | 512×384 | **(c) 負向測資**：一張漸層圖，裡面沒有 QR。斷言**明確失敗**（`QrNotFound`）且不產生任何檔案。 |

## 實測數字（2026-10-07，本機 Release x64；來自產品自己的日誌行）

| 測資 | 哪一輪解到 | 該輪像素 | 該輪耗時 | 解出的字元數 |
| --- | --- | --- | --- | --- |
| `qr-clean-512x512.png` | `first-pass` | 512×512 | 23 ms | 42 |
| `qr-scaled-256x256.png` | `first-pass` | 256×256 | 19 ms | 42 |
| `qr-scaled-jpeg-q40-512x512.jpg` | `first-pass` | 512×512 | 23 ms | 42 |
| `qr-big-2400x2400.png` | `first-pass`（縮過） | 1600×1600 | 43 ms | 42 |
| `qr-small-in-scene-160.png` | **`full-size`** | 3000×2250 | 82 ms | 42 |
| `no-qr-512x384.png` | （兩輪都沒有） | 512×384 | 11 ms | 0 |

`qr-small-in-scene-160.png` 的第一輪實測是 `first-pass 1600x1200 in 42 ms -> (no qr)`。
同一個場景把碼放大到 200 px 時第一輪就解到了（實測），所以 **160 px 是刻意選的**：
它落在「第一輪解不到、第二輪解得到」的那個區間裡。

## 產生器

產生器**不在 repo 裡**（與本專案既有的測資慣例一致：`verify-m3d.ps1` 的
`New-FixturePng`／`New-QuadrantJpeg` 也是當場產生，不進版控）。
它是一個一次性的 `net8.0-windows` 主控台專案，跑在 repo 之外（`%TEMP%`），
內容是：`MultiFormatWriter` 編碼 → BGRA byte[] → WIC 寫檔；最近鄰縮放；
`qr-small-in-scene-160.png` 是把碼貼在 3000×2250 的淡灰色斜紋底上。

要重新產生：把上面那幾件事寫成一支小程式（**同樣的 payload 字串、同樣的 hints**），
輸出檔名與尺寸照上表即可。**產生的圖片必須與現在這些逐位元相同**才算真的重現
（WIC 的 PNG 編碼不含時間戳，所以這是可重現的）。
