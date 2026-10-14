# 切片：圖片 → 單頁 PDF（格式輪盤的第六個輸出格式）

- **日期**：2026-10-14　**狀態**：計畫（未開工）
- **規格依據**：§3.2（修訂：PDF 由「第二里程碑」移入第一層輸出格式）、§6.2（輸出矩陣增加 PDF）、§5（寫檔契約）、§9.1（UI 不得自行判斷副檔名）
- **設計依據**：`FormatCatalog.TargetsFor`（第一層扇形的唯一來源）＋ `CodecRegistry.OfferedOutputs`（能力的唯一來源）
- **先例**：`ConversionEngine.MergePdf`（2026-10-07）——本專案**已經有一條「非 WIC 的生產者」**，本刀照它的形狀再拉一條
- **使用者裁定**（2026-10-14，原話）：①「目前感覺只有『圖片->PDF』我覺得可以做」；②「這項應該就塞進 M1 就好了吧?」；③在「A：每張圖各自一頁」與「B：多張合成一份多頁 PDF」之間選了 **A，B 以後再說**

---

## 0. Aegis Visibility

這一刀值得先寫計畫，因為它同時碰到**三個**會外溢的表面：①`CodecRegistry` 是格式知識的**唯一擁有者**，而 PDF **不是** WIC 編碼器（`CreateEncoder` 的回傳型別是 `BitmapEncoder`）⇒ 一定要決定「非 WIC 的目標格式由誰生產」；②規格的 §3.2／§6.2 要改（**宣告**改變，不是實作細節）；③`tools/verify-m1c.ps1` 的 S9.3 把編碼器數量寫死成 **5**，加了就是 **6**。三件事都不做，就會出現「做得出來但沒人按得到」或「按得到但驗收在說謊」。

## 1. 目標與硬性不變式

**目標**：拖一張圖片 → 輪盤第一層多一格 `PDF` → 放手 → 來源旁邊出現 `同名.pdf`，**一頁**，內容就是那張圖。

**不得破壞**（違反即視為缺陷）：

1. **不按觸發鍵的拖曳**逐位元等價（規格 §3.3）——本刀不碰 `DragDetector`。
2. **來源唯讀、永不覆寫**（§5）——沿用 `AtomicWriter` 與 `NamingPolicy`，不得新增第二個決定輸出路徑的地方。
3. **既有五個輸出格式**（PNG／JPG／WebP／BMP／TIF）的行為**逐位元不變**。
4. **PDF 不得成為 PDF 來源的目標格式**——`FormatCatalog.TargetsFor` 已經擋住（只認 `ConvertibleCategories`＝`IMAGE`），**本刀不得動它**，但要用斷言把這件事釘住。
5. **不新增第二個「品質」決定點**：PDF 沒有品質旋鈕，品質參數在這一條路上**不參與**（與 `MergePdf` 同一條規矩）。
6. **不做 B**（多張合成一份多頁 PDF）。多進一出在本專案已證明需要一個排序 UI（`MergeWindow`），那是另一刀。

## 2. Requirement Ready Check

```
Requirement Ready Check:
- Requirement source refs: 使用者 2026-10-14 對話裁定（①②③，見檔頭）；規格 §3.2／§6.2 修訂
- Goals and scope refs: §1（一張圖 → 一頁 PDF；B 明確排除）
- User / scenario refs: 規格 §2（把檔案從 A 格式變成 B 格式的桌面使用者）
- Requirement item refs: 「圖片→PDF」＝格式輪盤的一個新目標格式，不是工具層的新工具
- Acceptance / verification criteria refs: §8（頁數、頁面尺寸、內嵌影像尺寸、來源未動、指名拒絕）
- Open blocker questions: 無（B 已由使用者明確延後）
- Decision: ready
```

## 3. Change Necessity

```
Change Necessity:
- User-visible need: 拖一張圖可以直接拿到 PDF；目前只能先轉成圖再自己想辦法
- No-change / non-code option: 不足——使用者要的是「放手就得到 PDF」，這是產品行為，不是文件或設定
- Why code change is necessary: 需要一條新的輸出格式與其生產者；沒有任何設定或既有格式能代替
- Minimum change boundary: Formats/PdfFromImage.cs（新的唯一生產者）＋ CodecRegistry 的宣告與判定 ＋
  ConversionEngine.Convert 的一處分流 ＋ m1c 的編碼器計數。**不動** FormatCatalog、不動 WorkerServer、不動協議
- Decision: code-change
```

## 4. Existence Check

```
Existence Check:
- Proposed new surface: 一個新的檔案 Formats/PdfFromImage.cs（非 WIC 的生產者）
- Existing owner / reuse candidate: ConversionEngine.MergePdf 已經在做「非 WIC 生產者」這件事，
  但它住在 1400 行的轉檔引擎裡，而且是 merge 專用（多輸入、失敗政策不同）
- Why existing surface is insufficient: 把「一張圖 → 一頁 PDF」塞進 ConversionEngine 會讓那個檔案
  同時擁有「影像編碼」與「PDF 物件模型」兩種知識；而 Formats/ 目錄已經是格式機制的家
  （JpegMetadataStrip 就是同一個形狀：政策在 CodecRegistry、機制在 Formats/）
- Creation proof: 這個檔案的唯一責任是「把一張已解碼的圖寫成一頁 PDF」，介面只有一個方法；
  它不含任何格式政策（政策＝CodecRegistry 說 PDF 是可輸出目標）
- Entropy / retirement impact: 若日後做 B（多頁），MergePdf 與本檔會共用「開檔／加頁／存檔」的
  小段程式碼，屆時應抽出而不是各自長大——本刀先在檔頭註記這個觸發點
- Decision: add-with-proof
```

## 5. TDD Route

```
TDD Route:
- Mode: off（~/.config/aegis/config.toml 全域預設；本專案只有 M1c／M1d 曾由使用者逐切片授權 strict）
- Decision: skipped
- Strict authority: not applicable
- Strict signals: 有新行為（新輸出格式）、有新格式知識的擁有者邊界
- Light eligibility: 不適用（有行為變更）
- TDD-fit exception: —
- Test posture: post-change regression（先把功能做出來，再讓套件真的能量到它）
- Reason: 本刀的可驗證性很高（頁數、尺寸、內嵌影像都能從產物讀回），但測試載體是既有的
  tools/verify-m1c.ps1（PowerShell 套件），不是 C# 單元測試；先實作再擴充套件，與 M3 各刀一致
- Verification: §8 的六條客觀斷言 ＋ 一次假故障演練（改壞 → 紅 → 還原 → 綠）
```

## 6. 檔案地圖

| 檔案 | 動作 | 為什麼 |
|---|---|---|
| `src/Sidecar/Formats/PdfFromImage.cs` | **新** | 唯一的「圖片 → 單頁 PDF」生產者 |
| `src/Sidecar/Formats/CodecRegistry.cs` | 改 | `MvpOutputs` 加 `"PDF"`；新增 `IsNonImageTarget`（誰生產這個目標格式＝格式知識，只有這裡能回答）＋ 非 WIC 的探測 |
| `src/Sidecar/Convert/ConversionEngine.cs` | 改 | `Convert()` 在 offered 檢查之後分流；新增 `ToPdf` |
| `src/Sidecar/Formats/FormatCatalog.cs` | **不改** | `TargetsFor` 自動生效（`OfferedOutputs` 多一個就多一格扇形），而 PDF 來源的擋法已經在 |
| `src/Sidecar/Convert/WorkerServer.cs` | **不改** | `op=convert` 的 `target` 本來就是字串，沒有協議變更 |
| `tools/verify-m1c.ps1` | 改 | S9.3 的 `Count -eq 5` → `6`；新增 PDF 的 round-trip 斷言 |
| `docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` | 改 | §3.2／§6.2 的宣告修訂（附使用者裁定原話與日期） |

## 7. Plan Pressure Test

```
Plan Pressure Test:
- Owner / contract / retirement: 格式知識仍然只有 CodecRegistry 一個擁有者；生產機制在 Formats/；
  寫檔契約仍然只有 AtomicWriter＋NamingPolicy 一組。無重疊、無第二個決定點
- Architecture integrity / higher-level path: 有先用 MergePdf 這條既有先例；若改成「把 PDF 塞進
  BitmapEncoder 的抽象」會污染五個既有格式的路徑，故不採
- Verification scope: 每一條斷言都從**產物**讀回（PDFsharp 讀回頁數與頁面尺寸、PDF 物件模型讀回內嵌
  影像的像素尺寸、來源檔 sha256），不靠產品自報
- Task executability: 每個任務都有確切檔案、確切錨點、可貼上的程式碼與指令
- Pressure result: proceed
```

## 8. 任務

### T1 — `Formats/PdfFromImage.cs`（新檔）

**為什麼**：把「一張圖 → 一頁 PDF」的機制集中在一處；政策（誰是可輸出目標）留在 `CodecRegistry`。

**實作要點**（API 已於 2026-10-14 用本專案的既定方法查證：對 `PdfSharp-wpf.dll`（`net8.0-windows7.0`）查字串，`PdfSharp.Drawing`／`XImage`／`XGraphics`／`XUnit`／`XSize`／`DrawImage`／`FromFile`／`FromStream`／`PdfPage`／`AddPage` 全部存在）：

1. 用**既有的解碼路徑**取得像素：`CodecRegistry.ProbeDecode` 已證明檔可解；真正的像素走 WPF 的 `BitmapFrame`（與 `Convert` 同一條，不新增解碼器）。
2. **頁面尺寸 = 來源的實際 DPI 換算成點**（`pt = px × 72 / dpi`，DPI 取 `BitmapFrame.DpiX/DpiY`，取不到時用 96）——這樣印出來就是原尺寸，而且這一條可以**客觀斷言**（套件自己算期望值）。
3. **每一頁的來源只取第一頁／第一格**（多頁 TIFF、動畫 GIF）：與既有工具對 TIFF／GIF 的處置一致（規格 §6.1）。
4. 寫檔走**既有契約**：呼叫端負責 `AtomicWriter` 與 `NamingPolicy`，本檔只負責「寫進我給你的檔」。

**驗收**：編譯過、能被 T5 的探測讀回一頁。

### T2 — `CodecRegistry`：宣告與判定

**改動點**（確切錨點）：

- `MvpOutputs`（規格 §6.2 宣告的輸出）加入 `"PDF"`——**這是規格修訂的程式面**，註解要寫明修訂日期與使用者裁定原話。
- 新增 `internal static bool IsNonImageTarget(string format)`：回答「這個目標格式是不是由非 WIC 的路徑生產」。**這必須住在這裡**，因為它是格式知識；放到引擎裡就會出現第二個知道格式清單的地方（與 `HasLosslessMetadataStrip` 同一條理由）。
- `ProbeWicEncoders()` 只探 `WicEncoderFormats`（五個）——**不改**；PDF 的探測是另一條（見 T4）。

### T3 — `ConversionEngine`：分流與 `ToPdf`

**改動點**（確切錨點，2026-10-14 的行號）：

- `Convert()`：line 45 的 `OfferedOutputs.Contains(targetFormat)` 檢查**不變**；在它之後、`ProbeDecode` 之前分流——`IsNonImageTarget(targetFormat)` 為真時走 `ToPdf`，否則走原本的路。
- 新增 `private ConversionResult ToPdf(string sourcePath, ...)`：骨架與 `MergePdf`（line 1373）逐項對齊——格式檢查 → `AtomicWriter.Exists` → `ProbeDecode`（PDF 需要**可解碼的圖片**，所以這一關反而比 merge 嚴）→ `NamingPolicy.ResolveOutputPath(sourcePath, "pdf", targetDirectory)` → 生產 → `WriteWithCollisionRetry`（line 2192，含 3 次重試與同一組錯誤分類）。
- line 2024 的 `CreateEncoder(targetFormat, quality)` **不得**被 PDF 走到（分流就是為了這個）。

### T4 — 探測：非 WIC 的 round-trip

**做法**：照 `ProbeWicEncoders` 的精神——**真的做一次、再真的讀回來**。用一張自產的小圖寫一個暫時 PDF，再用 `PdfReader` 讀回：**頁數必須是 1、頁面尺寸必須等於期望的點數、`/Contents` 必須非空**。任何一項不過就是不可用，`OfferedOutputs` 就不會有 PDF（**做不到的格式不出現扇形**，規格 §7.2）。

### T5 — 套件

- `tools/verify-m1c.ps1`：S9.3 的 `Count -eq 5` → `6`（並在敘述裡說明第六個是 PDF）。
- 新增斷言（同一套件，S9 家族）：①一張 JPG → 恰好一個 `同名.pdf`，**頁數 1**；②頁面尺寸 = 來源像素 × 72 / DPI（套件自己算，不讀產品的自報值）；③內嵌影像的 `/Width`／`/Height` = 來源像素；④來源檔 sha256／mtime／長度三者不變；⑤`--convert <pdf> PDF` 被**指名拒絕**（PDF 不是可轉換的來源）；⑥輪盤：圖片來源的目標清單**含** PDF，PDF 來源的目標清單**不含**任何格式。
- **假故障演練**：把③的期望值改壞 → 必須紅；還原 → 必須綠。

### T6 — 規格修訂與收尾

- 規格 §3.2 的「PDF」那一列改為指向新的一節；§6.2 的輸出矩陣加一列 PDF（註明「一頁、單張、頁面尺寸依來源 DPI」），並寫上裁定日期與原話。
- `tools/obsidian/sync.ps1`、進度日誌、提交。

## 9. 風險

| 風險 | 處置 |
|---|---|
| PDFsharp 的 `XImage` 對 alpha 的行為未實測 | **不承諾**透明度會被保留；本刀的驗收不含「透明區域仍透明」這一條。若之後要承諾，先量一次再說 |
| 大圖的記憶體 | 一張圖進記憶體再寫 PDF，與既有 `Convert` 同級；不新增串流承諾 |
| 規格修訂被誤讀成「放寬」 | §3.2 的修訂要寫明：**只有「圖片→PDF」移動**；PDF 仍然不是可解碼的來源、仍然不能轉成圖片 |
| 與 B（多頁合併）的關係 | 檔頭註記共用點；本刀不做，也不預留半套介面 |

## 10. Retirement

**沒有東西要退役**：本刀不取代任何既有路徑，五個既有格式的程式碼一行都不動，`CreateEncoder` 的簽名不變。若日後 B 落地，`PdfFromImage` 與 `MergePdf` 的「開檔／加頁／存檔」小段應抽成共同機制——那是**那時**的退役判斷，不是現在的。
