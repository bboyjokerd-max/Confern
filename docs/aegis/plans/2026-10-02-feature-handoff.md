# 接手起點：功能開發（2026-10-02）

這份是**換 session 後的起點**。UI 設計已暫定，這一輪回到功能開發。

## 現況

- `HEAD = 3fb5f98`（本文件那次 commit）、分支 `master`、單一 worktree、**工作樹乾淨**。
- UI 方向 D（玻璃輪盤）已落地並驗收；設計**暫定**，日後要調再說。
- 設計側已定案、**不要重做**的兩件事：
  - 光暈**關閉**（`WheelDesign.GlowEnabled = false`；強度/厚度/圈數參數全部保留，改 `true` 即還原）。
    實機「一大圈」的元凶就是光暈本身，使用者看過關閉版後表示喜歡。
  - 扇形外緣尖點 **7px 圓角**（`WheelDesign.SectorCornerPx`，`RoundedSectorPath`）。
- 回歸現況（2026-10-02 實測，**更正本文件原本的錯誤數字**）：`m1a` **38/38 ＋ 1 INCONCLUSIVE**
  （合成 S4 需要 `-DragSim`）、`m1b` **50/50（務必加 `-Section All`；預設只跑 `S6`＝13 項）**、
  `m1c` 20/20、`m1d` **54/54**（新增 11 項失敗原因斷言後）。
  ⚠ 原文寫的「`m1a` 39/40、`S4.2` 紅」兩個數字都與實況不符，已更正（原因見待辦 2）。

## 下一步（建議順序，出自 `2026-10-01-ui-redesign.md` §7）

1. ~~**中文失敗原因的自動斷言**~~ **已完成（2026-10-02）**：`m1d` 新增 11 項
   （`S12.6d/7c/8c` 渲染行、`S12.8d/e` 斷言自測、`S12.8f/g` 部分失敗、`S12.15a–d` UI 藥丸路徑），
   `m1d` 43 → **54**。
2. **真實拖曳 S4 結案（進行中）**——⚠ 原定「用當日 21 次真手勢日誌」**已不可行**：本程式
   **每次啟動就輪替日誌**，2026-10-01 那份證據已被覆蓋（別處找到的 `release(mouse up)` 字樣
   全來自合成 payload 測試，不是人手拖曳）。同時查出 **`S4.2` 是永遠不可能通過的檢查**：
   它斷言 `WM_DROPFILES`，但自 `RegisterDragDrop` 之後那條路永不觸發（`HitLayer.cs:124` 早有記載）。
   已修：`S4.2` 改斷言 OLE `IDropTarget` 路徑、`S4.3` 改斷言「恰好一個檔」、`S4.3b` 認路徑
   （為此在 `DropTarget` 的 DragEnter/Drop 日誌加上 `first=` 欄位——放手路徑原本只記數量，
   無法指名道姓）、`S4m` 改斷言真實產品契約（OLE DragEnter ×3／滑鼠放手 commit ×3／worker ok ×3）
   並新增 `-ArchiveDir` 讓證據落地。
   **剩下需要人手**（滑鼠合成輸入政策：不得在使用者工作時注入，故須使用者親自跑）：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1a.ps1 -Exe <exe> `
     -Section S4m -ArchiveDir docs\aegis\work\2026-10-02-s4-closure
   ```
   （先跑 S4a：**不按**觸發鍵拖 3 次；再跑 S4b：**按住空白鍵**拖 3 次）
3. **設定頁 M1f 本體**——規格 §8。**2026-10-02 完成模型層（頁面本身仍待做）**：
   `SettingsStore.Save`（原子、可覆寫——為此 `AtomicWriter` 新增 `overwrite` 參數：設定檔是我們
   自己的，覆寫是意圖；使用者的輸出檔仍受「永不覆寫」紅線保護）、`--settings-set k=v`（不接受的鍵／值
   一律 exit 4 並**說出原因**）、未知鍵警告（打錯一個字母不再靜默退回預設）、觸發鍵解析
   （**組合鍵尚未實作**，明講拒絕而非假裝接受——§8 列了 Ctrl+Shift，這是規格與實作的落差，待決）、
   Shift 的規格警告。**同時修掉三個「看起來有設定、其實沒接上」的項**：觸發鍵與接收層逾時原本只吃
   命令列旗標（改 `settings.json` 完全沒用），現在是「命令列 > 設定檔 > 規格預設」並在證據寫出來源。
   新 runner `tools\verify-m1f.ps1`（S14，22 項）。**剩下的頁面（D10 指定的 WPF）與其餘設定
   （開機啟動＝待辦 4、語言、緊急熱鍵、診斷詳細度）尚未接上。**

   **同日第二批：頁面本體（已完成並實測）**。WPF 設定頁（D10 指定的技術，`UseWPF` 早就因 M1c 的
   WIC 而開啟，所以不必動 csproj、也不必新增任何套件）——九列設定、方向 D 色票
   （顏色唯一權威仍是 `WheelDesign`：XAML 內 **0 個硬編碼色、0 個文字字面值**）、
   zh-Hant／en 各 58 個字串鍵（已核對每個鍵兩語言各一份）、Shift 的規格警告、
   尚未生效的項在畫面上誠實標示。視窗 540×780（實測 640 高塞不下九列＋說明＋警告；
   代價：150% 縮放時會高於螢幕，已記錄）。
   執行緒模型：自己的 STA 執行緒 ＋ 自己的 `Application`（行程內只建一次）；實測開窗 228 ms、
   第二次開啟被拒、關窗後行程照常結束。**這三條已寫成自動斷言**（m1f S14.23–S14.28），
   因為它們正是設定頁最現實、又完全看不到的失效模式。
   儲存 → 當場套用：設定頁的回呼只「排隊 ＋ `PostMessage`」，真正的套用一律回主執行緒
   （新增 `WM_APP_SETTINGS_CHANGED`）；日誌與覆蓋層都不是執行緒安全的，跨執行緒直接呼叫只會
   變成隨機損壞。套用的三個值各有單一擁有者：觸發鍵→`detector`、逾時→`guard`、其餘→`_settings`。
   **還沒做**：①按「儲存」那條鏈路在 repo 裡尚無可重現證據（需要一個走**真按鈕處理常式**的勾子，
   進行中；先前的驗證 harness 是暫時的、已刪除）；②`--settings-ui` 目前只有命令列入口，
   托盤入口屬待辦 4；③開機啟動／緊急熱鍵只存值。
4. **托盤圖示 ＋ 開機自動啟動**——規格 §8 預設 on；兩者都掛在設定頁的規格上，排在 3 之後。
5. **點擊展開失敗清單**——刻意延後，可最後做。

相關計畫：`docs/aegis/plans/2026-10-01-m1def-first-release.md`（M1d–M1f）、
人工測試清單 `docs/TEST-PLAN-M1-first-run.md`（17 項）。

## 常用指令

```powershell
# 建置（建置後務必確認 exe 時間戳有變，否則可能跑到舊二進位）
dotnet build src\Sidecar -c Release -v minimal -nologo
(Get-Item "src\Sidecar\bin\Release\net8.0-windows\Confern.exe").LastWriteTime

# 回歸（每個腳本都要 -Exe <exe>；m1b 預設只跑 S6，要 -Section All 才是 50 項）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1a.ps1 -Exe <exe>
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1b.ps1 -Exe <exe> -Section All
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1c.ps1 -Exe <exe>
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1d.ps1 -Exe <exe>

# 輪盤畫面檢查（-Glow 0 因為光暈目前關閉）
$wd = "docs\aegis\work\2026-10-01-wheel-impl"
powershell -NoProfile -ExecutionPolicy Bypass -File "$wd\check-frame.ps1" -Dir $wd -Glow 0
```

## 環境鐵律（踩過，別再踩）

1. **驗收前先殺 `Confern` 行程**：殘留行程會鎖住 exe、讓複製**靜默失敗**（MSB3027/MSB3021，
   被 `Select-String -First N` 濾掉就完全看不到），結果是「0 錯誤」卻驗到舊二進位。
   必要時刪 `src\Sidecar\bin`,`obj` 強制重建。`--wheel-demo` 會活到 30 秒 watchdog。
2. **`--wheel-dump` 與 `--wheel-dump-raw` 必須一起產生**：只給平面圖會讓 `check-frame.ps1`
   讀到過期 raw 而誤判。
3. `tools\*.ps1` 一律**純 ASCII**（PS 5.1 會用 ANSI 解碼 BOM-less 的 UTF-8，中文註解會無聲吃掉下一行）。

## 不要碰

- `docs/design/reference/` 下未追蹤的使用者素材（`anim/`、`*.jpg`、`*.png`、`tangerine-demo.mp4`）。
- `tools/glass-spike/`：技術 spike 已完成，結論已寫進 `2026-10-01-ui-redesign.md` §4。

---

## 產品改名（2026-10-02）：工作代號 `Sidecar` → **`Confern`**

使用者定案（con ＋ fern；`confer` 本身也是英文真詞）。路線圖 Q5 結案——它必須在**待辦 4（托盤圖示＋
開機自動啟動）之前**決定，因為那兩項會把名字寫進登錄檔 Run 機碼與托盤提示。

跟著改的：exe 名（`Confern.exe`）、`AssemblyName`、`%LOCALAPPDATA%\Confern\`（設定／日誌／暫存日記）、
日誌檔名（`confern.log`）、單一實例 mutex、驗收腳本用來找視窗的**視窗類別名**
（`ConfernHitLayer`／`ConfernVisualLayer`）、worker pipe 名、暫存檔名、設定頁標題、啟動器
`tools/start-tangerine.cmd`。

**名稱的唯一擁有者＝`src/Sidecar/ProductInfo.cs`**（`Name` 與由它推導的 `Slug`）。散著硬寫的話，
改名會留下一個沒改到的副本——而那**不會報錯**，只會在使用者 AppData 底下長出第二套目錄、
或托盤顯示舊名字。

C# 命名空間與 `src/Sidecar` 目錄**刻意保留歷史名稱**：把產品改名與程式碼重構混在同一個 diff，
出事時會分不出是哪一件造成的。若要做全專案命名空間清理，那是獨立的一個機械式提交。

改名後五套回歸全數重跑並通過：**m1f 32/32、m1a 38/38（＋1 未定）、m1b 50/50、m1c 20/20、m1d 54/54**。
