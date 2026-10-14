# 切片：多張圖 → 一份多頁 PDF（`merge-pdf` 開始吃圖片）

- **日期**：2026-10-14　**狀態**：計畫（未開工）
- **規格依據**：§4.3（同類別才給選項／拒絕語意）、§6.1（多頁 TIFF／動畫 GIF 取第一頁）、§6.2（輸出矩陣）、§7.2（禁止靜默失敗）、§9.1（UI 不得自行判斷副檔名）、§5（寫檔契約）
- **前置切片**：`docs/aegis/plans/2026-10-14-image-to-pdf.md`（單張圖 → 單頁 PDF，已提交 `6c08c58`）
- **使用者裁定**（2026-10-14，原話）：①「如果一次選多個圖檔，可以新增一個『合併成PDF』的功能嗎?」；②入口＝**擴大既有的「合併 PDF」讓它同時吃圖片**；③頁序＝**用既有的排序視窗**；④頁面大小＝**要能提供選擇**；⑤先做這個，收尾排後面。

---

## 0. Aegis Visibility

這一刀碰到**三個會外溢的契約**：①`ToolDescriptor.AppliesTo` 的簽名（現在只拿得到格式字串，而「≥2 張圖才成立」需要**檔案數**）；②worker 協議（`pageSize` 是一個**新欄位**，`op=merge-pdf` 當年不需要升版正是因為它沒加欄位）；③`MergePdf` 的輸入契約（現在逐檔硬性要求 PDF）。三件事都是「只有一個擁有者」的那種東西，所以先寫計畫。

## 1. 目標與硬性不變式

**目標**：一次選**多張同類別圖片** → 輪盤工具層的「合併 PDF」可以按 → 開排序視窗（可調頁序、可選頁面大小）→ 確認 → 產生**一份多頁 PDF**，一頁一張圖。

**不得破壞**：

1. **單張圖不會看到「合併 PDF」**——≥2 張才成立。這是 `AppliesTo` 現有註解自己立的規矩（「按下去只會失敗的扇形」），本刀不得破例。
2. **PDF 來源的合併行為一字不變**（整頁物件搬移、不重編碼、`MergePdf` 的既有檢查與訊息）。
3. **混合選取仍然拒絕**（§4.3），圖片與 PDF 混在一起不給任何工具。
4. **`sourcePaths` 的陣列順序就是頁序**（協議既有定義）——不得新增第二個「順序」欄位。
5. **寫檔契約只有一個擁有者**：`AtomicWriter` ＋ `NamingPolicy`（含永不覆寫的 `(1)` 序號）。
6. **不新增第二個「品質」式的決定點**：頁面大小**只有一個** UI 擁有者（排序視窗），不得同時存在設定頁與視窗兩份。
7. **不碰**拖曳偵測、視覺層、命中層。

## 2. Requirement Ready Check

```
Requirement Ready Check:
- Requirement source refs: 使用者 2026-10-14 對話裁定（①②③④⑤，見檔頭）
- Goals and scope refs: §1（多張同類別圖片 → 一份多頁 PDF；頁序可調、頁面大小可選）
- User / scenario refs: 規格 §2（設計／工程：一批 TIFF 縮圖要轉 WebP——同一種「一批圖要變成一份東西」的需求）
- Requirement item refs: 工具層的 merge-pdf 擴充；不含新的格式扇形
- Acceptance / verification criteria refs: §8（頁數＝檔案數、頁序＝確認的順序、三種頁面大小的尺寸、來源未動、單張不出現、混合仍拒絕）
- Open blocker questions: 無（五項裁定皆已取得）
- Decision: ready
```

## 3. Change Necessity

```
Change Necessity:
- User-visible need: 一次拖多張圖直接得到一份多頁 PDF（掃描、翻拍、截圖合併）
- No-change / non-code option: 不足——單張→PDF 已存在，但合成一份需要新的輸入語意（多進一出）
- Why code change is necessary: 需要「≥2 張圖 ⇒ 合併可用」這個新判定，以及「一頁一張圖」的生產路徑；
  兩者都不存在（MergePdf 硬性只收 PDF、AppliesTo 拿不到檔案數）
- Minimum change boundary: PayloadVerdict（+檔案數）、ToolDescriptor.AppliesTo（改收判決）、
  MergeWindow（+頁面大小選擇）、WorkerMessage/WorkerProtocol（+pageSize、3→4）、
  MergePdf（+圖片分支）、PdfFromImage（+多頁入口）。**不動** FormatCatalog.TargetsFor、
  不動第一層扇形、不動 Convert()、不動拖曳／視覺／命中層
- Decision: code-change
```

## 4. Existence Check

```
Existence Check:
- Proposed new surface: PdfFromImage 增加一個「把多張畫格寫成一份多頁文件」的入口
- Existing owner / reuse candidate: MergePdf 已經是「多進一出 → 一份 PDF」的擁有者；
  PdfFromImage 已經擁有「一頁 PDF 怎麼寫」
- Why existing surface is insufficient: MergePdf 走的是 PDF **整頁物件搬移**（不重編碼），
  圖片沒有頁面物件可搬，必須**重新排版**成頁面——那是 PdfFromImage 的知識，不是 MergePdf 的。
  但「一份文件、多個來源、一個輸出」的骨架屬於 MergePdf
- Creation proof: 新入口只做一件事（給定一批畫格與頁面大小 → 一份多頁 PDF），
  且與單張版本共用同一段「算點數、加頁、畫圖」的程式碼；兩者不會各自演化
- Entropy / retirement impact: 單張路徑（圖片→單頁 PDF）應改為呼叫多頁版本（N=1）——
  這一條要**當場做**，否則同一個行為會有兩份實作
- Decision: extend-existing-owner
```

## 5. TDD Route

```
TDD Route:
- Mode: off（~/.config/aegis/config.toml 全域預設）
- Decision: skipped
- Strict authority: not applicable
- Strict signals: 有行為變更（新輸入語意）、有協議欄位新增
- Test posture: post-change regression（先做出功能，再讓套件真的量到它）
- Reason: 測試載體是既有的 tools/verify-*.ps1（PowerShell 套件），不是 C# 單元測試；
  與本專案 M3 各刀一致
- Verification: §8 的八條客觀斷言 ＋ 一次假故障演練
```

## 6. 檔案地圖

| 檔案 | 動作 | 為什麼 |
|---|---|---|
| `src/Sidecar/Tools/IToolCatalog.cs` | 改 | `PayloadVerdict` 加 `FileCount`；`AppliesTo(string?)` 改收 `PayloadVerdict`（適用性判定仍然只有**一個**來源） |
| `src/Sidecar/Tools/ToolCatalog.cs` | 改 | `merge-pdf` 的條件：PDF 一律適用；**圖片類別且 ≥2 張**才適用。`ToolsFor` 改傳判決 |
| `src/Sidecar/Formats/FormatCatalog.cs` | 改 | `PayloadVerdict` 三個建構處補上檔案數（**判定邏輯一行都不動**） |
| `src/Sidecar/Tools/MergeWindow.cs` | 改 | 新增「頁面大小」三選一（**唯一的 UI 擁有者**）；`Order` 之外多帶一個模式 |
| `src/Sidecar/Tools/MergeUi.cs` | 改 | 回呼由 `string[]` 改為「順序 ＋ 模式」 |
| `src/Sidecar/Convert/WorkerServer.cs` | 改 | `WorkerMessage` 加 `pageSize`；`WorkerProtocol.Version` **3 → 4**；job 證據行**行尾**追加（不得插中間） |
| `src/Sidecar/Convert/ConversionClient.cs` | 改 | 送出 `pageSize`；日誌帶上它 |
| `src/Sidecar/Convert/ConversionEngine.cs` | 改 | `MergePdf` 加圖片分支（`sourcePaths` 全為圖片時走新路；PDF 一律走舊路）；`ToPdf` 改呼叫多頁版本 |
| `src/Sidecar/Formats/PdfFromImage.cs` | 改 | 新增「多張畫格 → 一份多頁文件」入口；單張版本改為呼叫它 |
| `tools/verify-m1c.ps1` / `verify-m1d.ps1` | 改 | 新的斷言；**既有 job 行前綴不得改動**（套件比對的是前綴） |

## 7. Plan Pressure Test

```
Plan Pressure Test:
- Owner / contract / retirement: 適用性判定仍只有 ToolDescriptor.AppliesTo 一處；
  頁序仍只有 sourcePaths 一處；頁面大小只有 MergeWindow 一處；寫檔仍只有 AtomicWriter+NamingPolicy。
  單張路徑會被多頁版本取代（見 §4 的 retirement）
- Architecture integrity / higher-level path: 沒有新抽象；多頁入口與單頁入口共用同一段排版程式碼
- Verification scope: 每一條都從**產物**讀回（頁數、每頁尺寸、頁序以檔名比對），不靠產品自報
- Task executability: 每個任務都有確切檔案與錨點（見 §8）
- Pressure result: proceed
```

## 8. 任務

### T1 — 判決帶上檔案數（契約）

`PayloadVerdict`（`IToolCatalog.cs`）加 `int FileCount`；三個建構處（`FormatCatalog.Classify` 的 `Empty`／`Mixed`／`Ready`／`Unsupported`）補上 `paths.Count`。⚠ **判定邏輯不得改**——只補資料。

### T2 — 適用性判定改收判決

`ToolDescriptor.AppliesTo(PayloadVerdict)`（`IToolCatalog.cs:139-145`）：

```
merge-pdf:  verdict.SourceFormat == "PDF"           ⇒ 適用（既有行為不變）
            verdict is image && verdict.FileCount>=2 ⇒ 適用（新）
            其餘                                      ⇒ 不適用
```

「是不是圖片類別」**不得自己寫一份清單**：問 `CodecRegistry.CategoryOf`（格式知識的唯一擁有者）。
`ToolCatalog.ToolsFor` 只改傳參，其餘不動。

### T3 — 排序視窗加「頁面大小」

`MergeWindow` 加三選一（僅在批次含圖片時顯示）：**各自原始尺寸（預設）／統一成第一張／A4 置中留邊**。
模式 id（ASCII，進協議）：`original` / `first` / `a4`。順序權威仍是 `_order`，模式是**第二個獨立值**，
由 `Order` 的旁邊一起交出去（`MergeUi` 的回呼簽名改為 `(string[] order, string pageSize)`）。

### T4 — 協議加欄位並升版

`WorkerMessage.pageSize`（`string?`，null＝`original`）；`WorkerProtocol.Version` **3 → 4**
（理由：這次**真的**加了欄位，而 `op=merge-pdf` 當年不用升版正是因為它沒加）。
UI 與 worker 是同一支 exe，沒有跨版本相容問題；升版讓「不同版本的兩半」當場連不上，
而不是安靜地少一個欄位。job 證據行在**行尾**追加 `pageSize=…`。

### T5 — 引擎：圖片分支

`MergePdf` 在既有的逐檔檢查之前先判「這一批是不是圖片」：
- **全是 PDF** ⇒ 走既有路徑，**一行都不改**（回歸的基準）。
- **全是圖片**（同類別、≥2）⇒ 逐檔取第一頁畫格（多頁 TIFF／GIF 只取第一格，§6.1），依序交給
  `PdfFromImage` 的新入口，頁面大小依模式決定：
  - `original`：每頁 = 該圖 px × 72 / 該圖 dpi（與單張路徑一致）
  - `first`：每頁 = 第一張的點數（其餘等比縮放置中）
  - `a4`：A4（595.28 × 841.89 pt）等比縮放置中留邊
- **混到**（圖片＋PDF）⇒ 具名拒絕，不寫任何檔（§4.3 的精神：不同語意不放同一批）。
- 輸出檔名、`(1)` 序號、3 次重試、失敗分類**全部沿用**（`NamingPolicy` ＋ `WriteWithCollisionRetry`）。
- **單張路徑改為呼叫多頁版本（N=1）**，否則同一行為會有兩份實作（§4 的 retirement）。

### T6 — 套件

新增斷言（`verify-m1d.ps1` 為主）：
1. `[jpg, jpg]` 的工具清單**含** `merge-pdf`；`[jpg]` 的**不含**（本刀的紅線）。
2. `[jpg, pdf]` 混合 ⇒ `Mixed`，工具清單空。
3. 三張圖合併 ⇒ **頁數＝3**、檔名沿用第一張、來源 sha256 不變。
4. 頁序＝確認的順序（把順序排成 `c,a,b` ⇒ 以每頁尺寸或內嵌影像寬高**逐頁**比對）。
5. `original`：每頁尺寸＝各自 px×72/dpi（套件自己算期望值）。
6. `first`：每頁尺寸＝第一張的點數。
7. `a4`：每頁 595.28 × 841.89 pt（±0.5）。
8. **假故障演練**：把第 5 條的期望值改壞 ⇒ 紅；還原 ⇒ 綠。

### T7 — 規格與收尾

規格 §4.3／§6.2 補「多張同類別圖片 ⇒ 合併成一份多頁 PDF（頁序可調、頁面大小可選）」；
`WorkerProtocol` 的版本註解補上 3→4 的理由；進度日誌 ＋ Obsidian ＋ 提交。

## 9. 風險

| 風險 | 處置 |
|---|---|
| `AppliesTo` 改簽名會動到工具目錄的既有斷言 | 先跑一次 `verify-m3d` 的 S22 與 `verify-m1d` 當基準，改完全套再跑 |
| 三種頁面大小讓驗收膨脹 | 每種只斷言**頁面尺寸**一條（客觀、可從產物讀回），不驗像素內容 |
| 大批圖片的記憶體 | 一次只持有當前畫格（逐檔解碼、逐頁寫入），與單張路徑同級 |
| 與單張路徑的行為分歧 | T5 明訂單張改走多頁版本（N=1），且第 5 條斷言同時守住兩條路 |
| 協議升版讓舊 worker 連不上 | 同支 exe 起 worker，無跨版本情境；升版是為了讓不一致**當場**失敗 |

## 10. Retirement

**單張圖片→PDF 的獨立實作退役**（T5）：它併入多頁版本以 `N=1` 呼叫。
`AppliesTo(string?)` 的舊簽名退役（T2）。其餘不動：`Convert()`、第一層扇形、`TargetsFor`、拖曳／視覺／命中層全部保持原樣。
