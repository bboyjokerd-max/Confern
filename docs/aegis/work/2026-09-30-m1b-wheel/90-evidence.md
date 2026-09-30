# M1b 輪盤幾何與顯示 — 證據

- Date: `2026-09-30`
- Slice: M1b（路線圖第 2 片）
- Plan: `docs/aegis/plans/2026-09-30-m1b-wheel.md`
- Spec: §4.3、§4.4、§9.1、§9.3
- 結論：**T1–T4 完成並以證據驗收；T5 部分、T7（拖放酬載）未開始；A1 尚不能以真實拖曳驗收**
- 期間的滑鼠政策：使用者正在工作，因此**全程沒有注入任何合成輸入**；需要滑鼠的 S8／A7 一律留待使用者同意

## 1. 環境

| 項 | 值 |
|----|----|
| 螢幕 | 單一 `(0,0) 2560×1080`（工作區 `2560×1032`） |
| Windows | 11 25H2 build 26200.9168 |
| .NET SDK | 8.0.425（`net8.0-windows`、x64、`WinExe`） |
| NuGet 全域快取 | **不存在**（`%USERPROFILE%\.nuget\packages` 無）→ 不得新增任何 `PackageReference` |

## 2. 檢查總表

| 區段 | 內容 | 結果 |
|------|------|------|
| S6 | 兩層堆疊：視覺層樣式、穿透、零侵入、首次顯示、程序結束清理 | **13/13 PASS** |
| T2 自測 | 扇區映射（含邊界角）、翻邊、夾制、點陣圖尺寸 | **17/17 PASS** |
| T4 模擬 | 捕捉 → 顯示 → 環狀選取 → Esc → 兩層同收，×3 循環 | **15/15 PASS** |
| T7 自我檢查 | `OleInitialize` + `RegisterDragDrop` 結果；T4 模擬接線回歸 | **S_OK / S_OK；接線 5/5 PASS** |
| S7 | 幾何自測、輪盤矩形、翻邊、接線迴圈、酬載路徑（注入式）、延遲、殘留視窗 | **22/22 PASS** |
| S6+S7（`-Section All`） | 上述全部 | **35/35 PASS** |
| S7 反向對照 | 故意指向不存在的執行檔 | **exit 1（不是成功）** |
| M1a 回歸 | S0／S1／S2／S5（非侵入區段） | **22/22 PASS** |

## 3. 關鍵實測

### 3.1 生死線 2：視覺層不會吃掉拖放（原 R5，致命風險 → 解除）

- 視覺層 `exstyle=0x80800A8`（`LAYERED` + `TRANSPARENT` + `TOOLWINDOW` + `NOACTIVATE` + `TOPMOST`）
- 命中層 `exstyle=0x8080098`（**沒有** `TRANSPARENT`，符合契約）
- 兩層同時可見時，輪盤矩形內 9 點 + 矩形外 4 點，`WindowFromPoint` **13/13 全部回命中層**
- 前景始終是使用者的瀏覽器（`MozillaWindowClass`）→ 兩層都沒有搶焦點

### 3.2 首次顯示延遲（預算 100 ms）

| 次 | 值 |
|----|-----|
| T1 示範圖（260×260） | 14.2 ms |
| 真輪盤（304×330） | 17.9 ms / 18.2 ms |

### 3.3 輪盤外觀（`wheel-frame.png`）

以獨立視覺模型核對（**不截使用者螢幕**，只 dump 本程式自己畫的點陣圖，並合成到中性底色）：
5 個扇區（`PNG/JPG/WEBP/TIF/BMP`）、中心 `HEIC → JPG`（對應已選扇區 index 1）、
下方 `3 個檔案` 預覽膠囊、左上鬼影卡（白色圓角卡 + 選中扇區色條）；**無文字重疊／裁切／錯位**。

### 3.4 生命週期接線

`guard.OnCaptured()` → `ShowWheel()` → `SelectNext(+1/+1/−1)` = `1,2,1` →
連續 5 次 forward 回繞回原點 → `guard.OnUserResolved("escape")` → `hit.Hidden` 通知 → 兩層同收。
3 個循環全部 PASS，且每次都以**作業系統真實狀態**斷言（`!hit.IsVisible && !presenter.IsVisible`）。

### 3.5 OLE 拖放目標（T7）

| 檢查 | 結果 |
|------|------|
| `OleInitialize` | **S_OK**（主執行緒本就有 `[STAThread]`，未撞 `RPC_E_CHANGED_MODE`） |
| `hit#0 RegisterDragDrop` | **S_OK** |
| 實作約束 | 類別必須 `public` + `[ComVisible(true)]`；COM 可見的公開簽章**不可**使用 internal 型別（CS0051）→ 另立 `OleInterop.OlePoint`（版面同 `Native.POINT`、兩個 int）；`DropTarget` 因此不直接依賴 `Evidence`，改注入 `Action<string>` 記錄器 |
| 收層幂等 | `IDropTarget::Drop` 與 `WM_DROPFILES` 兩條路都呼叫 `guard.OnDrop()`；`HideNow` 以作業系統真實可見度判斷，第二次是 no-op |

⚠️ **S8 必須回答的風險**：同一視窗同時 `DragAcceptFiles` + `RegisterDragDrop` 之後，shell 是否**仍會**送
`WM_DROPFILES`？若不再送，放手當下的「檔案清單權威」必須改成 OLE 這條。兩條路都會收層，
所以不會有收不了層的失效模式；但**檔案清單的權威必須唯一**（M1d 的檔案操作會依賴它）。

### 3.6 酬載之後的路徑（**注入式**驗證，明確不含 OLE 管線）

以 `--wheel-payload` 把假酬載直接餵進「真實 `DragEnter` 之後」的處理常式（同一份 `ShowWheelFromPayload`）。
**它不證明 OLE 管線可用**（那要 S8）；它證明的是拿到酬載之後的行為：

| 輸入 | 扇區 | 預覽 | 延遲 |
|------|------|------|------|
| `C:\Temp\holiday.jpg` | `PNG,WEBP,BMP,TIF`（**JPG 已排除**） | `holiday.png` → 選取後 `holiday.webp` → `holiday.bmp` | 15.8 ms |
| `C:\Temp\holiday.heic` | 5 個（HEIC 不在清單內） | `holiday.png` → `holiday.jpg` | 17.3 ms |
| 兩張 `.jpg` | `PNG,WEBP,BMP,TIF` | `2 個檔案`（不隨選取改變） | — |

### 3.7 S7 區段的範圍（重要：它不證明什麼）

`-Section All` ＝ **S6 + S7**，兩者都**不注入任何輸入**，因此可以在使用者工作時跑；
`All` **刻意不含 S8**（S8 會注入真實滑鼠輸入，必須先問使用者）。
S7 的酬載項是**注入式**：它證明「拿到酬載之後」的行為正確，**不證明** OLE 管線能把酬載送上來。

## 4. 本切片發現並修正的缺陷

| # | 缺陷 | 影響 | 處置 |
|---|------|------|------|
| 1 | `verify-m1a.ps1 -Section S0,S1,S2,S5` **不吃逗號清單**，且未知區段靜默回報 `0/0 checks passed` 並 **exit 0** | 我一度拿「0/0 成功」當成回歸通過——**正是反思裡記過的「永遠不會失敗的檢查」** | 兩個跑者都改為支援逗號清單、未知區段 `exit 2`、**零檢查即 `exit 2`**；重跑得 22/22 真結果 |
| 2 | S6.7 斷言視覺層在 `(DemoX,DemoY)` | 檢查本身錯：視窗原點依設計是 `游標 − PadPx(24)`（要容納游標上的鬼影卡） | 修**檢查**（改成依契約推算），不動程式 |
| 3 | `Marshal.Copy` 沒有 `IntPtr→IntPtr` 多載（計畫裡的程式碼不合法，CS1503） | 建置失敗 | 改走中繼緩衝區逐列複製；計畫內的程式碼一併更正 |
| 4 | PowerShell **逗號優先序高於加號**：`@($x + 1, $y + 2)` → `$x + (1,$y) + 2` | 跑者取樣段拋 `op_Addition on Object[]`，且 PS 5.1 不給行號 | 每個元素獨立加括號 |
| 5 | 方法名 `GeometrySelfTest` 與同類別欄位同名；且執行邏輯掛在 `App` 而非 `Options` | CS1955／CS0117 | 改名 `RunGeometrySelfTest` 並以 `App.` 呼叫 |
| 6 | **原格式出現在自己的輸出扇區裡**（來源 `JPG` 時扇區仍含 `JPG`），違反規格 §4.3 | 使用者會看到「JPG → JPG」這種荒謬選項 | `StubFormatCatalog.TargetsFor` 改為排除來源；並在介面上寫明「排除來源是 `IFormatCatalog` 的契約，M1c 不得推回 UI」。**示範資料（來源 HEIC）永遠踩不到這個缺陷**——只有餵真實酬載才會浮現 |
| 7 | COM 可見的公開簽章用到 internal 型別（`Native.POINT`、`Evidence`） | CS0051 ×7 | 另立 `OleInterop.OlePoint`（版面相同）；`DropTarget` 改注入 `Action<string>` 記錄器，不再依賴 `Evidence` |
| 8 | S7 沿用 S6 的假設，以為 `--wheel-demo --wheel-dump` 之後程式會留在前景 | S7.3 假失敗 | `--wheel-dump` 是**一次性**診斷（顯示→存檔→結束）。拆成兩次執行：要探測矩形的那次不加 dump，dump 另跑一次 `-Wait` |
| 9 | **`Select-String` 預設不分大小寫**，所以「不得有 `FAIL` 行」的檢查會被程式自己的 `failed=0` 摘要命中 | S7.12 假失敗（反向的假訊號：明明全過卻判失敗） | 新增 `Test-LogCase`（`-CaseSensitive`）；規定凡「不得出現 FAIL」一律用它 |

## 5. 事故（自我揭露）

**`Program.cs` 被清空一次**：我誤留一行 `[System.IO.File]::WriteAllLines($p, @())` 作為「前置準備」，
直接把檔案寫成 0 bytes。已用 `git checkout -- src/Sidecar/Program.cs` 從 T1 提交完整還原（365 行），
僅損失同一輪尚未提交的編輯。**教訓**：改寫檔案一律先 `Copy-Item` 備份 + 內容守衛（驗證區塊含預期字串、
結尾字元正確）才寫回；永不把「清空」當成準備動作。

## 6. 未驗證／殘餘（不得宣稱通過）

| 項 | 狀態 | 需要什麼 |
|----|------|----------|
| **A1**「20/20 次輪盤出現於游標 20 px 內」 | ❌ 未驗 | 真實拖曳（要滑鼠）+ T7 酬載 |
| **A7** 不按觸發鍵的拖曳零侵入 | ❌ 未驗（M1a 已鋪路） | 真人手勢 |
| **T7** `DragEnter` 酬載 → 真實 `原格式` | 🟡 已實作，OLE 初始化與註冊皆 S_OK；**端到端未驗** | 真實拖曳（S8） |
| **S8** OLE 與 `WM_DROPFILES` 是否並存 | ❌ 未驗 | 真實拖曳（S8） |
| **S7** 非侵入檢查（22 項） | ✅ 已驗（含反向對照） | — |
| **T5** 混合選取拒絕／無可轉換目標提示 | 🟡 繪製路徑已有（`notice` 參數），但資料來源（格式矩陣）在 M1c | M1c 的 `IFormatCatalog` 實作 |
| 第二螢幕／Per-Monitor DPI | ❌ 未驗（本機單螢幕） | 第二螢幕硬體（M1f） |
| 真實放手的 drop 與輪盤同時存在 | ❌ 未驗 | S8（要滑鼠） |

## 7. 診斷旗標（交付前必須清理）

`--wheel-demo`、`--wheel-dump`、`--wheel-source`、`--wheel-targets`、`--wheel-selected`、
`--wheel-previews`、`--wheel-geometry-selftest`、`--wheel-simulate-cycles`
——與 M1a 的 `--raise`／`--cycle-test`／`--watchdog-seconds`／`--assume-explorer-foreground`
同批在 M1f 前移除或收進「診斷模式」。
