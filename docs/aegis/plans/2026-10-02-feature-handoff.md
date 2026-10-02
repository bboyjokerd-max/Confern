# 接手起點：功能開發（2026-10-02）

這份是**換 session 後的起點**。UI 設計已暫定，這一輪回到功能開發。

## 現況

- `HEAD = ef13973`、分支 `master`、單一 worktree、**工作樹乾淨**（`git status` 0 筆）。
- UI 方向 D（玻璃輪盤）已落地並驗收；設計**暫定**，日後要調再說。
- 設計側已定案、**不要重做**的兩件事：
  - 光暈**關閉**（`WheelDesign.GlowEnabled = false`；強度/厚度/圈數參數全部保留，改 `true` 即還原）。
    實機「一大圈」的元凶就是光暈本身，使用者看過關閉版後表示喜歡。
  - 扇形外緣尖點 **7px 圓角**（`WheelDesign.SectorCornerPx`，`RoundedSectorPath`）。
- 回歸現況：`verify-m1b` 50/50、`m1c` 20/20、`m1d` 43/43、`m1a` 39/40
  （唯一紅的 `S4.2` 已證實與 UI 無關，見下方待辦 2）。

## 下一步（建議順序，出自 `2026-10-01-ui-redesign.md` §7）

1. **中文失敗原因的自動斷言**——小、可立即驗證。今日 `962ffea` 只有人工與直接觀察證據。
2. **真實拖曳 S4 結案**——用當日 21 次真手勢日誌，標記為「以真實使用證據通過」（不是新的驗證）。
3. **設定頁 M1f 本體**——規格 §8；`M1f-lite` 已完成，這是完整版。
4. **托盤圖示 ＋ 開機自動啟動**——規格 §8 預設 on；兩者都掛在設定頁的規格上，排在 3 之後。
5. **點擊展開失敗清單**——刻意延後，可最後做。

相關計畫：`docs/aegis/plans/2026-10-01-m1def-first-release.md`（M1d–M1f）、
人工測試清單 `docs/TEST-PLAN-M1-first-run.md`（17 項）。

## 常用指令

```powershell
# 建置（建置後務必確認 exe 時間戳有變，否則可能跑到舊二進位）
dotnet build src\Sidecar -c Release -v minimal -nologo
(Get-Item "src\Sidecar\bin\Release\net8.0-windows\Sidecar.exe").LastWriteTime

# 回歸
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1a.ps1   # 同 m1b / m1c / m1d
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1b.ps1

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
