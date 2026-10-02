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
4. **托盤圖示 ＋ 開機自動啟動**——規格 §8 預設 on；兩者都掛在設定頁的規格上，排在 3 之後。
5. **點擊展開失敗清單**——刻意延後，可最後做。

相關計畫：`docs/aegis/plans/2026-10-01-m1def-first-release.md`（M1d–M1f）、
人工測試清單 `docs/TEST-PLAN-M1-first-run.md`（17 項）。

## 常用指令

```powershell
# 建置（建置後務必確認 exe 時間戳有變，否則可能跑到舊二進位）
dotnet build src\Sidecar -c Release -v minimal -nologo
(Get-Item "src\Sidecar\bin\Release\net8.0-windows\Sidecar.exe").LastWriteTime

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

1. **驗收前先殺 `Sidecar` 行程**：殘留行程會鎖住 exe、讓複製**靜默失敗**（MSB3027/MSB3021，
   被 `Select-String -First N` 濾掉就完全看不到），結果是「0 錯誤」卻驗到舊二進位。
   必要時刪 `src\Sidecar\bin`,`obj` 強制重建。`--wheel-demo` 會活到 30 秒 watchdog。
2. **`--wheel-dump` 與 `--wheel-dump-raw` 必須一起產生**：只給平面圖會讓 `check-frame.ps1`
   讀到過期 raw 而誤判。
3. `tools\*.ps1` 一律**純 ASCII**（PS 5.1 會用 ANSI 解碼 BOM-less 的 UTF-8，中文註解會無聲吃掉下一行）。

## 不要碰

- `docs/design/reference/` 下未追蹤的使用者素材（`anim/`、`*.jpg`、`*.png`、`tangerine-demo.mp4`）。
- `tools/glass-spike/`：技術 spike 已完成，結論已寫進 `2026-10-01-ui-redesign.md` §4。
