# Tangerine（macOS）解構 × Windows 版移植方案

- 文件版本：v1（2026-XX-XX）
- 標的：[Tangerine: File Converter（Mac App Store id6813034065）](https://apps.apple.com/us/app/tangerine-file-converter/id6813034065)、開發者 LEAP STUDIO
- 資料來源：官網 tangerineformac.com、Product Hunt、ChatGate 報導、搜尋摘要（Reddit 原始討論被 Cloudflare 擋）
- **本文件的可信度分級**：`[事實]` 來自公開來源；`[推論]` 是我對其內部實作的合理推測，**未經原始碼驗證**；`[待確認]` 需要你或實測補齊。

---

## 1. 一句話定義

> 它把「拖曳」本身變成 UI 通道：在**拖曳途中**按下修飾鍵，格式輪盤在游標處彈出，選一個，檔案就地轉好、寫在原檔旁邊。

不是「另一個轉檔器」，而是**把轉檔迴圈縮短到零個視窗**。價值在互動設計，不在轉檔引擎。

---

## 2. 產品解構

### 2.1 真正創新的只有三件事

| # | 機制 | 為什麼是護城河 |
|---|------|----------------|
| 1 | **拖曳中攔截（drag-time interception）** | 在 drag session 進行中讀取修飾鍵狀態、並在游標處繪製自己的 UI。市面上幾乎沒有競品做這件事（Clop / Permute / Compresto 都是「拖到 app 上」或「開視窗」）。 |
| 2 | **就地寫檔（sibling output）** | 不搬檔、不覆蓋、不跳存檔對話框 → 省掉整個「選輸出路徑」步驟。官方 FAQ 明講：「Never replaces your original.」 |
| 3 | **local-only + 無帳號** | 官網一半篇幅在講「檔案不離開你的 Mac」。隱私不是功能，是**定價理由**。 |

**其餘 90% 是包裝好的既有工具鏈**（ffmpeg / ImageMagick / PDF 工具）。這部分 Windows 上完全等價可得，**不是護城河，只是工程量**。

### 2.2 功能矩陣 `[事實，官網＋報導]`

| 類別 | 轉換 | 進階工具 |
|------|------|----------|
| 圖片 | JPG / PNG / WebP / HEIC / TIFF / SVG / AVIF / BMP，可匯出 PDF、DOCX | 壓縮、裁切（拖曳或固定比例）、縮放（像素或百分比）、旋轉／翻轉、**去背**、去 metadata（EXIF 定位/機型/時間）、讀 QR、多圖合成一份 PDF |
| 音訊 | MP3 / M4A / WAV / FLAC / OGG / Opus / AIFF / WMA | 修剪、合併 |
| 影片 | MP4 / MOV / MKV / WebM / AVI / WMV / GIF，可抽 MP3 | 壓縮、裁切、修剪、分割、合併 |
| PDF | → DOCX / JPG / PNG / TXT（300 DPI 全頁） | 合併、分割、遮蔽（redact） |
| 文字 | TXT → PDF / JPG / PNG；SRT / VTT → 純文字 | — |
| 壓縮檔 | ZIP / TAR / GZIP / RAR | 解壓、打包 |

對外數字：**188 個轉換選項、25 個進階工具**（官網）／75 個選項（另一個 .app 網域，數字不一致 → 見 §10）。

### 2.3 互動模型 `[事實]`

- 常駐：**選單列 app**，無主視窗。
- 觸發：拖曳檔案時按住 **Shift** → 彈出「輸出格式」輪盤；再加 **Option** → 切換成「編輯工具」輪盤。
- 操作：**方向鍵**選、**Enter** 套用、**Esc** 取消；純鍵盤可完成（也支援先選檔再按 Enter 開輪盤）。
- 多檔：同類別可批次，**混合選取會拒絕**（因為沒有共同可用的操作集）——這個決策值得照抄。
- 輸出：原檔旁邊、同主檔名換副檔名、**永不覆寫原檔**。

### 2.4 商業與發行 `[事實]`

- 一次買斷 **US$15**（原價 $29.99），無訂閱；免費試用 **20 次轉換**。
- **Apple silicon only、macOS 13+**。
- 官網 FAQ 自己寫著：「**Is it available only for macOS? Yes. There is no Windows or Linux version.**」

→ **這就是你要的空隙**：作者自己宣告了不做的平台。且 2026-09 才發表，第三方報導仍「缺乏評價與 social proof」——市場尚未被佔滿。

### 2.5 競品座標

| 產品 | 模型 | 與 Tangerine 差別 |
|------|------|-------------------|
| Permute / Compresto / Convert & Compress | 開 app、丟檔案進去 | 多一個視窗、多一次切換 |
| Clop | 常駐，**自動**壓縮剪貼簿/下載 | 無「選格式」的意圖表達 |
| Transfigurator / Convierto | 純圖片轉換 | 類別窄 |
| **DropIt（Windows）** | 拖到浮動目標圖示 | **最接近 Windows 既有解**，但無輪盤、無「就地寫回」 |
| TeraCopy / FileMenu Tools | shell 擴充攔截拖放 | 做複製/工具，不做轉檔 |

**結論**：Windows 上「拖曳即轉檔」不是空白的，但「拖曳中彈輪盤 + 就地寫回 + 全類別」是空的。

---

## 3. 技術解構

### 3.1 macOS 為什麼做得到 `[推論]`

1. AppKit 的拖曳是**來源應用自己的 modal run loop**（`NSEventTrackingRunLoopMode`）。拖曳期間，來源 app 仍在 pumped 狀態下處理事件。
2. 第三方程式可以用 **NSEvent 的 global monitor / CGEventTap** 觀察拖曳過程中的修飾鍵狀態（`NSEvent.modifierFlags`）。
3. 於是：偵測到「拖曳中 + Shift」→ 建立一個位於游標處的 overlay 視窗畫輪盤 → 監聽方向鍵/Enter/Esc → 取消原拖放（不執行移動），改由自己完成轉檔並寫回原資料夾。

**關鍵**：它並不需要成為「丟放目標」，只需要**觀察 + 覆蓋 UI + 取消**。

### 3.2 Windows 為什麼不能照抄 `[推論]`

Windows 的檔案拖放走 **OLE `DoDragDrop`**：

- drag loop 跑在**來源行程（explorer.exe）的執行緒**上，是 modal 的。
- 第三方行程**無法**從外面取得拖曳中的 `IDataObject`（拿不到「正在被拖的是哪些檔」）。
- 也**無法**在拖曳中於游標處畫自己的視窗——除非自己是 drop target（drop target 的回呼是在 explorer 的訊息迴圈上被呼叫的，所以才 pump 得動自己的視窗）。
- 反過來說：**成為 drop target 的那一刻，你就同時拿到「拖曳中的修飾鍵狀態」與「檔案清單」**（`IDropTarget::DragOver` 的 `grfKeyState` 有 `MK_SHIFT`；`Drop` 時 `IDataObject` 帶 `CF_HDROP`）。這是 Windows 上唯一忠實的等價路徑。

### 3.3 一個必須做決策的移植陷阱：**Windows 的 Shift 已經有意義**

在 macOS 拖曳時 Shift 幾乎沒用途；但在 Windows Explorer：

- `Ctrl` + 拖曳 = 複製
- `Shift` + 拖曳 = **移動**（同磁碟內）
- `Alt` + 拖曳 = 建立捷徑

**照抄 Shift 會和使用者肌肉記憶衝突**（使用者按 Shift 拖曳本來想移動檔案，結果彈出轉檔輪盤）。

→ 建議：觸發鍵**可設定**，預設值不要用單一 Shift。候選：`Ctrl+Shift`、`Alt+Shift`、`右鍵拖曳`（Windows 原生支援右鍵拖放，且右拖本身就是「放開後給選單」的語意，**語意最貼合**）。`[決策點]`

---

## 4. Windows 實作路線（三條，建議做 C→B→A 漸進）

### 路線 A：Shell Drop-Target 攔截（最忠實，風險最高）

- 註冊 `IDropTarget` 殼層擴充：`HKCR\*\shellex\DragDropHandlers`、`Directory\shellex\DragDropHandlers`、`Folder\shellex\DragDropHandlers`、`Drive\shellex\DragDropHandlers`。
- `DragEnter/DragOver` 被呼叫時（在 explorer 執行緒）→ 顯示 overlay 輪盤跟隨游標；`Drop` → 取得 `CF_HDROP` 檔案清單。
- **擴充本體必須極輕**：explorer 行程內只做轉發（具名管道／LocalServer COM），實際轉檔交給獨立 worker 行程。
- 風險：
  - 殼層擴充是 Windows 上最容易讓 explorer 崩潰的介面 → 必須有逾時保底（時間到一律回 `DROPEFFECT_NONE`）與 kill switch。
  - Win11 會停用回應過慢的擴充；需 32/64 位元匹配；UIPI 讓管理員權限視窗的拖曳攔不到。
  - 安裝/移除需重啟 explorer。
  - 瀏覽器／Outlook 拖出的是**虛擬檔（FileGroupDescriptor，延遲渲染）**，不是 `CF_HDROP`，要另外處理。
- **前提**：這條路的可行性一定要**先用最小原型實測**（見 §7 M0.5），不要直接投資。

### 路線 B：全域 hook + 自繪 HUD（中等）

- `WH_MOUSE_LL` + `WH_KEYBOARD_LL` 偵測「左鍵按住移動超過門檻 + 觸發鍵」→ 送 `Esc` 取消 explorer 的拖放（檔案不會被移動）→ 顯示自己的輪盤 → 轉檔寫回原資料夾。
- 難點：**拿不到被拖的檔名**。補救：用 UI Automation `ElementFromPoint`，或對前景 explorer 視窗用 `IFolderView::ItemFromPoint` / `IShellView` 反查游標下的項目；多選時再讀 explorer 的選取集合。
- 崩潰半徑小（不進 explorer 行程），但依賴 UI 自動化 → 對 Win11 版本更新較敏感。

### 路線 C：**浮動接收目標**（最務實，建議當 MVP 與保底）

- 自己開一個 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED` 的 always-on-top 小視窗，**它自己就是 drop target**（`WM_DROPFILES` / OLE drop 皆可）→ 用 `CF_HDROP` 100% 穩定拿到檔案清單。
- 全域 hook 偵測拖曳開始且觸發鍵按下 → 淡入在游標旁；放開即轉。
- 差異：**要在「我們的目標」上放手**，不是任意位置。用鍵盤與右鍵選單補足。
- 業界先例：**DropIt**、**TeraCopy** 都是這個模型 → 證明穩定、可商業化。

### 路線 C+：觸發時升起的全螢幕接收層 `[構想，未驗證 — 需 M0.5 實測]`

在 C 之上多加一個關鍵洞見：**OLE 拖放決定「放手落在誰身上」時，會用 `WindowFromPoint` 邏輯找游標下的視窗，而它會跳過 `WS_EX_TRANSPARENT` 的視窗。**

於是：拖曳開始且觸發鍵按下時，升起一個 **全螢幕、topmost、`WS_EX_LAYERED` 且 alpha = 1/255** 的視窗（視覺上等於完全看不見，但**仍然可被命中**）→ 使用者**放到任何位置都會落在我們身上**。

- ✅ 不需任何殼層擴充 → 不需管理員、不碰 explorer、可上 MSIX、無崩潰風險
- ✅ 同時拿到 `CF_HDROP` 檔案清單與 `DragOver` 的 `grfKeyState` → 真正的「任意位置放手 + 拖曳中彈輪盤」
- ⚠️ 風險 1：**z-order 與拖曳殘影**。殼層自己畫「跟著游標的檔案鬼影」，我們的全螢幕層可能蓋住它 → 對策是自己畫鬼影（反正輪盤本來就要畫在游標處）
- ⚠️ 風險 2：**收不回去就是災難**。若接收層因偵測失敗沒收起，整個桌面會變成點不到任何東西 → 必須有硬保險：任何 mouse-up、逾時、程序結束一律強制隱藏
- ⚠️ 風險 3：**觸發鍵放開卻剛好放手**的競態 → explorer 已經把 drop 鎖定在我們身上，檔案不會移動。對策：這種情況由我們自己補做移動/複製（依 Ctrl/Shift 語意）
- ⚠️ 風險 4：多螢幕 + 每螢幕 DPI 縮放，要每個螢幕各開一層
- ⚠️ alpha=0 會**穿透命中測試**、alpha≥1 才可命中 → 這是整個技巧成立的關鍵，必須實測確認

### 路線比較

| | A 殼層攔截 | B hook + HUD | C 浮動目標 | **C+ 全螢幕接收層** |
|---|---|---|---|---|
| 任意位置放手 | ✅ | ✅ | ❌（要放到目標上） | ✅（待實測） |
| 拖曳中彈輪盤 | ✅（真的在拖曳中） | ✅ | ✅ | ✅ |
| 取得檔案清單 | ✅ 原生 | ⚠️ 需 UI 自動化反查 | ✅ 原生 | ✅ 原生 |
| explorer 崩潰風險 | **高** | 低 | **無** | **無** |
| 安裝複雜度 | 高（需管理員、重啟 explorer） | 低（**低階滑鼠/鍵盤 hook 不需要管理員權限**） | 低 | 低 |
| MSIX 上架友善 | ❌ 殼層擴充與 MSIX 相衝 | ⚠️ | ✅ | ✅ |
| 建議順序 | 第 3 步 | 第 2 步（可選） | 保底 | **第 1 步（先驗證 C+）** |

### 三個都該做的附加通道（低成本、高覆蓋）

1. **右鍵選單殼層擴充**（`*` / `Directory` 的 `ContextMenuHandlers`）→ 選「Convert with …」→ 輪盤。比 A 安全得多，且是最常見的 Windows 使用慣例。
2. **SendTo 資料夾** + **命令列**：`app.exe --to png <files...>`（可被 AutoHotkey／排程／檔案監看呼叫）。
3. **系統匣 + 全域熱鍵**：選檔後按熱鍵直接開輪盤（= 原版「按 Enter 開輪盤」的鍵盤路徑）。

---

## 5. 轉檔引擎對照表與授權紅線

| 類別 | Windows 建議 | 授權／注意 |
|------|--------------|------------|
| 圖片解碼（JPG/PNG/TIFF/BMP） | **WIC**（內建，零散佈成本） | OS 內建 |
| HEIC / AVIF | HEIF 影像擴充（需散布或引導安裝）＋ libheif / libavif | libheif LGPL；**Windows 不保證使用者有 HEIC 解碼器** |
| WebP | libwebp | BSD |
| 圖片處理（縮放/裁切/旋轉） | SkiaSharp（MIT）或 libvips（LGPL） | 別用 ImageMagick CLI 當核心（散佈與沙箱較痛） |
| 影音/音訊/GIF/字幕 | **ffmpeg** | ⚠️ **必用 LGPL build 且動態連結**；`--enable-gpl`（含 x264 等）**不可**放進閉源付費 App。你機器上的 `ffmpeg 9.0 gyan.dev full_build` 屬 GPL，開發可用、**不可隨產品散布** |
| PDF 轉圖／轉文字 | **PDFium**（BSD）或 MuPDF（**AGPL → 商業必須買授權**） | PDFium 較安全 |
| TXT → PDF | QuestPDF（MIT，注意營收門檻條款）／原生排版 | iText 為 AGPL |
| PDF → DOCX | 最難的一格。LibreOffice headless（MPL，可）或買商業 SDK | 品質與字型是難點，建議**先不承諾** |
| 壓縮／解壓 | SharpCompress（MIT）或 7-Zip（LGPL） | ⚠️ **7-Zip 的 unRAR 條款禁止用其 RAR 程式碼做壓縮器**；RAR 只能解、且不可反編譯 |
| 去 metadata | 自寫 WIC metadata 清除，或呼叫 ExifTool | ExifTool 為 Perl（散佈麻煩）；Exiv2 為 GPL → 小心 |
| **去背（Remove BG）** | ONNX Runtime + U2Net/RMBG（離線，模型約 30–60 MB） | ⚠️ **這是最大的功能斷層**：Mac 有免費 Vision/CoreML；Windows 要自己帶模型、自己算。Windows AI APIs 需 Copilot+ NPU 機種，覆蓋率不足 |
| 讀 QR | ZXing.NET（Apache-2.0）／ZBar | 安全 |

**架構結論**：Windows 版「能用哪些引擎」幾乎都解決了，**唯二的坑是 HEIC 解碼與去背**（都源自失去 Apple 的系統級免費能力）。建議 M1 先不進去背，或明確標示「需下載模型」。

---

## 6. 建議架構

```
┌──────────────────────────────────────────────────────────┐
│ UI 行程（.NET 8 + WPF / WinUI 3）                         │
│  · 輪盤 HUD（跟隨游標、鍵盤操作、Esc 取消）                 │
│  · 設定頁（觸發鍵、輸出命名規則、衝突策略）                  │
│  · 系統匣、熱鍵、進度佇列                                   │
└───────────────┬──────────────────────────────────────────┘
                │ 具名管道（JSON 訊息）
┌───────────────▼──────────────────────────────────────────┐
│ Worker 行程（轉檔引擎）                                    │
│  · Job 佇列、並行上限、取消、暫時檔與原子寫入                 │
│  · WIC / Skia / ffmpeg(LGPL) / PDFium / SharpCompress     │
└───────────────┬──────────────────────────────────────────┘
                │
┌───────────────▼──────────────────────────────────────────┐
│ 殼層整合（原生 C++ 小 DLL，唯一必須原生的一段）              │
│  · IDropTarget 攔截（路線 A，可選）／右鍵選單擴充            │
│  · 只做轉發 + 逾時保底，永不在此做轉檔                       │
└──────────────────────────────────────────────────────────┘
```

**技術棧建議**

| 元件 | 選擇 | 理由 |
|------|------|------|
| 本體 UI | **C# / .NET 8 + WPF** | 你的機器已有 node/python/git/ffmpeg，但**尚無 .NET SDK 與 MSVC**（見 §7）；WPF 對 always-on-top overlay、托盤、多螢幕 DPI 最成熟 |
| 殼層擴充 | **C++（VS Build Tools）** | COM 殼層擴充用 C# 可行但更脆；維持原生小 DLL |
| 替代方案 | Tauri / Electron | UI 開發快，但**殼層擴充仍得寫 C++**，且體積大、冷啟動慢（overlay 要秒開，這點很重要） |

---

## 7. 里程碑與工作量（單人估算，含驗證）

| 階段 | 內容 | 估時 |
|------|------|------|
| **M0.5 可行性原型（先做這個）** | 一個小程式：全域滑鼠 hook 偵測拖曳 + 浮動 drop target + 顯示假輪盤 + 拿到檔名清單。**只驗證「拖曳 → 攔截 → 拿到檔案」這條命脈** | 2–4 天 |
| M1 Alpha | 輪盤 HUD（方向鍵/Enter/Esc）、圖片 3 種格式轉換、就地寫檔與命名衝突策略、設定頁觸發鍵 | 1–2 週 |
| M2 殼層整合 | 右鍵選單擴充、SendTo、命令列、系統匣、熱鍵；（可選）路線 A 攔截 | 2–3 週 |
| M3 全功能 | 影音／音訊／PDF／文字／壓縮；壓縮、裁切、縮放、旋轉、合併、遮蔽；去 metadata、QR | 3–4 週 |
| M4 去背 + HEIC | ONNX 模型整合、HEIC 解碼散佈策略 | 1–2 週 |
| M5 產品化 | 安裝包（Inno Setup；**MSIX 與殼層擴充相衝**）、程式碼簽章、20 次試用 + 一次性授權、更新器、崩潰回報 | 1–2 週 |

**總計：約 2–3 個月**（全職）／4–6 個月（兼職）。**M0.5 沒過就不該往下走。**

### 你機器目前的工具鏈實測

- ✅ 已有：`node v22.23.2`、`pnpm 11.22.0`、`python 3.10.6`、`git 2.44`、`ffmpeg 9.0`（GPL full build，見 §5）
- ❌ 缺少：**.NET SDK**（`dotnet.exe` 在，但 `--list-sdks` 無輸出 → 只有 runtime）、**MSVC/cl.exe**、`cmake`、`Rust`、`magick`、`7z`、`exiftool`、`pandoc`、`soffice`
- 系統：Windows 11 **25H2，build 26200.9168**（登錄檔 ProductName 寫 "Windows 10 Pro" 是 Win11 的已知登錄檔怪癖，實際為 Win11）
- 磁碟容量：沙箱下 `Get-PSDrive` 回 0，**讀不到，不影響結論**

→ 動工前需安裝：**.NET 8 SDK**（必要）、**VS Build Tools + C++ 工作負載**（做殼層擴充時才需要）。

---

## 8. 風險登記簿

| 風險 | 影響 | 對策 |
|------|------|------|
| 路線 A 讓 explorer 崩潰 | 使用者直接解除安裝、一星負評 | 預設先做 C；A 只在 M0.5 實測通過後才做；逾時保底 + kill switch |
| Windows 的 Shift 語意衝突 | 使用者誤觸、檔案被移動 | 觸發鍵可設定，預設不用單一 Shift（§3.3） |
| GPL 工具誤入產品 | 法律風險、上架被拒 | §5 的授權表當 checklist；ffmpeg 只用 LGPL 動態連結 |
| 去背／HEIC 功能落差 | 與原版「功能對等」的宣稱不成立 | 明確標示為「需額外下載」，或延到 M4 |
| 品牌／商標 | 被原作者主張混淆 | §9 |
| Win11 版本更新打破 UI 自動化（路線 B） | 功能間歇失效 | 不把 B 當唯一路徑 |
| overlay 冷啟動太慢 | 拖曳中彈不出來，體感全毀 | 常駐行程預先啟動、UI 預先建立（勿用 Electron 冷啟） |

---

## 9. 命名與法律

1. **不要叫 Tangerine。** 同名商標／混淆風險高；原作者官網還特別提過「同名產品」造成的困擾。取一個自己的名字。
2. 不要複製它的圖示、文案、截圖、動畫資產。
3. **機制（拖曳中彈輪盤）本身通常不受著作權保護**，但：不要逆向它的二進位、不要用它的 App Store 素材、注意是否有 UI 專利。
4. 試用/授權模型可以照抄商業模式（20 次 + 一次性 $15）——**商業模式不受著作權保護**。

---

## 10. 待確認事項

| # | 事項 | 怎麼解 |
|---|------|--------|
| 1 | **兩個官網數字不一致**：`tangerineformac.com`（Shift+Option、188 選項）vs `tangerineformac.app`（⌘、75 選項、FAQ 也說無 Windows 版）。其中一個可能是舊版或抄襲/SEO 站 | 以 App Store 頁面與 press kit 為準；下載安裝看實際行為 |
| 2 | App Store 頁面用抓取只拿到中國區首頁（被導向），**沒拿到官方描述與截圖** | 你把 App Store 的截圖貼給我，我用 vision 讀，能把輪盤的扇區數、配色、動畫節奏解構得更準 |
| 3 | 「拖曳中彈輪盤」的 macOS 實作是**推論** | 需實測或看它的行為錄影 |
| 4 | 定價與試用次數（$15 / 20 次）來自媒體與搜尋摘要 | 以 App Store 現價為準 |
| 5 | 路線 A 在 **Win11 25H2** 上是否仍完整支援 `DragDropHandlers` | **M0.5 實測** |

---

## 11. 下一步選項

- **(甲) 直接做 M0.5 可行性原型**：在你機器上建一個最小 C#/.NET 專案，驗證「拖曳 → 浮動目標攔截 → 拿到檔案清單 → 假輪盤」。需要先裝 .NET 8 SDK。
- **(乙) 先寫完整規格書（PRD）**：把輪盤 UI、設定項、錯誤處理、命名規則、試用/授權流程全部定死，再動工。
- **(丙) 先做 UI 設計稿**：用 OpenPencil 把輪盤（扇形數、尺寸、配色、跟隨游標的偏移）與編輯面板畫出來給你看。
- **(丁) 停在解構**：報告到此，你自己評估。
