# M1d–M1f 初版（可測試版）— 實作計畫

**規格**：§4.4 多檔、§5 檔案處理、§6 格式矩陣、§7 錯誤處理、§8 設定、§9.1 行程架構、§9.3 效能
**日期**：2026-10-01
**目標（用戶指示）**：「直接做完初版讓我明天測試」——**明天要能真的拿來拖曳轉檔**。

---

## 0. 使用者看到的東西要能成立

到目前為止（M1a／M1b／M1c）輪盤是**真的**、格式知識是**真的**，但**不會轉檔**。
初版的驗收標準只有一句：**拖檔 → 選格式 → 檔案真的出現在同一資料夾，而且原檔一個位元都沒變。**

## 1. 切片

| 切片 | 內容 | 驗證 |
|------|------|------|
| **D1 NamingPolicy** | 同資料夾、同主檔名＋新副檔名；衝突 ` (1)`、` (2)`…**永不覆寫、永不詢問** | S10：衝突序列、`holiday.heic → holiday.png` |
| **D2 AtomicWriter** | 同資料夾 `<name>.<ext>.tmp-<guid>` → flush → `File.Move(overwrite:false)`；失敗即刪暫存；長路徑 `\\?\` | S10：目標已存在時**不覆寫**、失敗後**零殘檔** |
| **D3 轉檔引擎** | WIC 解碼 → 目標編碼器；JPG 品質、TIFF 無壓縮、BMP 32-bit、PNG 無損；metadata／ICC 盡量原樣保留（保不住要**明講**） | S11：尺寸／格式／像素保真、品質有作用 |
| **D4 殘檔回收** | 轉檔期刊（journal）：建立暫存前登記、rename 後註銷；啟動時清掉「已不在進行中」的暫存 → §7.1「崩潰／斷電後清殘檔」 | S10：人造殘檔會被回收 |
| **E1 Worker 行程** | 規格 §9.1：具名管道＋版本化 JSON（`{protocolVersion, jobId, sourcePaths, targetFormat, quality}`）；UI 不傳 UI 狀態 | S12：協定往返、版本不合時重啟而非崩潰 |
| **E2 進度與摘要** | 輪盤收起處**原地單行** `N/M`；完成後 2 秒淡出；部分失敗 `✓ N / ✗ M`（§4.4、§7.3）。**不得逐行洗版** | S12：一行進度、一次摘要 |
| **F1 設定** | `%LOCALAPPDATA%\Sidecar\settings.json`：JPG／WebP 品質（60–100）、觸發鍵、逾時、診斷；預設即規格 §8 | S13：預設值、範圍夾制、壞檔退回預設 |
| **F2 錯誤訊息** | §7.1 對照表逐項落地（不支援／無法解碼／無法寫入／空間不足／來源消失／鎖定／逾時） | S11／S12：每種失敗都有訊息且**零殘檔** |
| **F3 測試入口** | 雙擊即可啟動的 `.cmd`＋明天的人工測試清單（含「原檔時間戳不變」的檢查方法） | 人工 |

## 2. 明示不做（明天不測這些）

- **WebP 輸出**：規格有宣告，但本機沒有編碼器（缺 libwebp 打包）→ 扇區不出現，日誌明講。
- **設定頁 UI**：初版用 `settings.json`＋文件說明；WPF 設定頁留給 M1f 正式版。
- **HEIC 輸出**：規格明定不提供（不散布 HEVC）。
- **雲端佔位檔 30 秒逾時**：實作會做（來源讀取逾時），但明天若沒有 OneDrive 佔位檔就測不到 → 誠實列為未驗。
- **`FileGroupDescriptor` 虛擬檔**：訊息路徑會做，但明天不測（需要瀏覽器拖出）。

## 3. 風險

| 風險 | 對策 |
|------|------|
| 轉檔把原檔弄壞（§7.2 第一條，最嚴重） | 一律唯讀開啟（`FileShare.ReadWrite`）＋寫入只碰暫存檔與 rename；S10 以**位元組雜湊＋時間戳**驗證原檔不變 |
| 暫存檔殘留（§7.2 第三條） | journal ＋ 失敗路徑必刪 ＋ 啟動回收；S10 逐案斷言資料夾內零 `.tmp-*` |
| 目標已存在卻被覆寫 | `File.Move(overwrite:false)`：**就連競態也不可能覆寫**（核心 API 層保證，不是靠先檢查） |
| UI 被轉檔卡住 | 轉檔在 **Worker 行程**（§9.1）；UI 只等管道訊息；S12 量 UI 回合 |

---

## 4. 實作狀態（2026-10-01 收尾）

| 切片 | 狀態 | 實作 | 驗證 |
|------|------|------|------|
| D1 NamingPolicy | ✅ 完成 | `src/Sidecar/Files/NamingPolicy.cs` | S12.4a-c、S12.13c/e |
| D2 AtomicWriter | ✅ 完成 | `src/Sidecar/Files/AtomicWriter.cs`、`LongPath.cs` | S12.4c、S12.13d |
| D3 轉檔引擎 | ✅ 完成 | `src/Sidecar/Convert/ConversionEngine.cs` | S12.1、S12.2、S12.6–S12.8 |
| D4 殘檔回收 | ✅ 完成 | `src/Sidecar/Files/TempJournal.cs` | 啟動回收有日誌；**人造殘檔回收未自動化**（見下） |
| E1 Worker 行程 | ✅ 完成 | `Convert/WorkerProtocol.cs`、`WorkerServer.cs`、`ConversionClient.cs`、`PipeLink.cs`、`ConversionRunner.cs` | S12.9、S12.10a–g |
| E2 進度與摘要 | ✅ 完成 | `src/Sidecar/Visual/ProgressPresenter.cs` | S12.10b–d、S12.10h/i |
| F1 設定 | ✅ 完成（無 UI） | `src/Sidecar/Settings/SettingsStore.cs` | S12.11、S12.12（`--settings-probe`） |
| F2 錯誤訊息 | ✅ 完成 | `Convert/ConversionTypes.cs`（`UserMessage`） | S12.6–S12.8（逐項比對 §7.1 文字） |
| F3 測試入口 | ✅ 完成 | `tools/start-tangerine.cmd`、桌面捷徑、`docs/TEST-PLAN-M1-first-run.md`、`tools/check-test-artifacts.ps1` | 桌面捷徑實測可啟動 |

**驗證總表**：M1a 38/38、M1b 50/50、M1c 20/20、**M1d（新增）41/41**，建置 0 警告 0 錯誤。
證據與量測數據見 `docs/aegis/work/2026-10-01-m1def-conversion/90-evidence.md`。

### 與計畫不同的地方（誠實記錄）

| 項目 | 計畫 | 實際 | 為什麼 |
|------|------|------|--------|
| 進度呈現 | 單行 `N/M` | ✅ 照做，且**同一個視窗原地更新**（`frames == updates`） | — |
| 放手執行 | 未寫進切片 | **補上**（bug 2） | 規格 §4.3「選好放手」是使用者最自然的動作，原本只有 Enter 能執行 |
| 殘檔回收 | S10 人造殘檔會被回收 | 只有**啟動時**回收有做且有日誌，**沒有**自動檢查 | 需要一個「先製造殘檔、再啟動」的檢查；列入 M1f |
| 失敗清單展開 | F2 只要求訊息 | 一行 `✓ N / ✗ M`（未做點開清單） | 規格 §7.3 只要求單行；展開另立工作 |

### 追加的待辦（本切片新發現）

1. **§9.3 的預算沒有像素維度**：「5 MB JPG→PNG < 800 ms」在本機 20.7 MP 的照片上是 1265 ms。
   預算應改寫成「N 百萬像素 → X ms」，否則無法判定（量測見證據文件 §3）。
2. **D4 的自動檢查**：造殘檔 → 啟動 → 斷言被回收且**不誤刪非本程式建立的檔案**。
3. **T7／T6 診斷債**：`--raise`、`--cycle-test`、`--watchdog-seconds`、`--assume-explorer-foreground` 等
   診斷選項在 M1f 前要收斂（保留 `--codecs`／`--convert`／`--wheel-payload`／`--settings-probe` 並寫進文件）。
