# 切片計畫：拖一個資料夾 ⇒ 展開成裡面的檔（2026-10-15）

使用者 2026-10-15 選定：「拖一個資料夾進去 ⇒ 裡面的圖合成一份 PDF」。

## 1. 這個功能真正的樣子

**不是**一個新的合併功能，而是**把「一批檔案」的來源從「使用者手動選好」擴大成「他自己指的資料夾」**。

關鍵在於**展開的時機**：只要在 `FormatCatalog.Classify` **之前**把資料夾換成裡面的檔案清單，
下游**一行都不用改**——判定、工具層、扇區、頁面大小三選一、排序視窗、引擎全部照舊。

```
拖曳 payload（可能有資料夾）
      ↓  ← 這一刀唯一新增的東西（單一擁有者）
  展開：資料夾 -> 檔案清單（遞迴、排序、過濾、上限）
      ↓
FormatCatalog.Classify(paths)        既有
      ↓
工具層 ToolCatalog.ToolsFor(verdict) 既有（AppliesTo 已收 verdict）
      ↓
merge-pdf / 其他工具                  既有
```

**這正是上一刀那個契約修正的回報**：`AppliesTo` 現在拿得到檔案數 ⇒
「一個裝著 20 張圖的資料夾」會**自動**讓 MERGE PDF 那顆扇形出現，不必為它加任何新規則。

## 2. 現在的行為（實測，不是推論）

```
--tools-list --wheel-payload <一個裝了 3 張圖 + 2 份 PDF 的資料夾>
tool list: count=0 tools=[] (source=UNKNOWN, status=Unsupported, notice=不支援的格式, ...)
tool list: FAILED the tool layer would be empty (the wheel would show nothing)
```

⇒ 產品**完全沒有**目錄概念（`Directory.Exists` 全庫只在 `QrDiagnostic.cs:378`、
`NamingPolicy.cs:44` 兩處，都與 payload 無關）。payload 的路徑在 `Program.cs:3341` 起被逐條記成
`payload file: …`。

## 3. 必須當場決定的規則（每一條都會寫進規格）

| # | 決定 | 理由 |
|---|---|---|
| D-A | **遞迴**展開子資料夾 | 掃描／截圖的實際長相就是「一層層」；只做第一層會被使用者當成壞掉 |
| D-B | 深度上限 **8 層**、檔案數上限 **1000** | 防的是誤拖 `C:\` 這種事，不是防壞人。**超限要明說**（「只取了前 1000 個」），不可以靜默截斷 |
| D-C | 只收**這個產品認得的格式**（用 `FormatCatalog` 判定，**不自己列清單**） | 一個資料夾裡一定有 `.txt`／`.lnk`／`Thumbs.db`；把它們塞進判定會讓整批變 Mixed |
| D-D | 名稱採**自然排序**（`scan2` 在 `scan10` 前面） | 字典序會把 10 排到 2 前面，而**頁序就是合併的結果**，這種錯會直接印在成品上 |
| D-E | 跳過隱藏檔／系統檔（`Thumbs.db`、`desktop.ini`、`~$*`、`.DS_Store`） | 同上 |
| D-F | 資料夾與檔案**混合拖入**時，展開後一起判定 | 沒有理由禁止 |
| D-G | 展開**只發生一次**（不連鎖、不重複展開） | 一個路徑只解一次，避免同一批出現兩份 |
| D-H | 輪盤通知要說出**實際發生的事**：「資料夾：讀到 N 個檔（跳過 M 個）」 | 「畫面上有的東西必須是真的」 |

## 4. 一起修的真缺陷（不是加分項，是這個功能的前提）

`ConversionEngine.MergeImagesToPdf` 現在是**先把每一張都解成 `BitmapSource` 收進一個 list，
最後才交給 `PdfFromImage.WriteMany`**。

⇒ 一個 200 張、每張 12MP 的掃描資料夾 ≈ **10 GB 常駐記憶體** ✗。而「拖一個資料夾」的
主要使用情境**正是**這種批次。

⇒ 改成**一張一張**：解碼 → 寫那一頁 → 釋放，再下一張。`PdfFromImage` 要從
「收一批畫格」改成「開一份文件、逐頁餵」的形狀（`WriteMany` 保留給已經在記憶體裡的那條路，
新增串流版；兩者共用同一個頁面尺寸／置中的實作 ⇒ 不會有兩份排版規則）。

**驗收方式**：合併 200 張同樣的圖，量**峰值工作集**（`PeakWorkingSet64`），
必須與 20 張時**同一個量級**（不是與張數成正比）。這是一個**可量的數字**，不是感覺。

## 5. 工作項

- **T1**　`PayloadPaths`（新，唯一擁有者）：`Expand(IReadOnlyList<string> dropped) -> (paths, notice)`。
  規則 D-A～D-G 全在這裡，別處不得再寫第二份「哪些檔要收」。
- **T2**　接到 payload 進來的唯一入口（`Program.cs:3341` 上游那一段），展開後才 `Classify`。
  通知走 D-H。
- **T3**　`PdfFromImage` 的串流版 ＋ `MergeImagesToPdf` 改成逐頁；量峰值工作集前後對照。
- **T4**　驗收腳本 `tools\verify-folder-drop.ps1`：資料夾 ⇒ 展開後的張數／跳過的檔數／
  自然排序（`scan2` 在 `scan10` 前）／隱藏檔被跳過／空資料夾仍是 Unsupported／
  混合拖入／上限訊息／**峰值工作集不隨張數線性成長**。含一次假故障演練。
- **T5**　規格 §3.x 補「拖曳來源可以是資料夾」＋ D 系列決策；日誌；Obsidian。

## 6. 不做

- 不做「拖資料夾 ⇒ 逐檔各轉一份」（那是另一個功能，且 `Mixed` 的規則會先擋住它）。
- 不改輪盤的 UI 設計（方向 D、`GlowEnabled=false`、`SectorCornerPx=7`，使用者已凍結）。
- 不在這一刀碰 M2 的五個家族（使用者 2026-10-15 明說「不是一定要做」）。
