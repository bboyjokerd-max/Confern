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

---

## 待辦 3 收尾（2026-10-02）：兩個「存得下但沒生效」的設定都生效了

### 診斷記錄（規格 §8）——語意裁定為 (b) 並實作

使用者裁定：**關＝只寫必要事件（預設）；開＝再加上高頻 detail 行**。不是「關＝完全不寫日誌」。
理由：日誌是唯一的事後追查證據，且全程留在本機、不上網；關掉它只是把事故現場丟掉，換來檔案小一點。

實作在 `Diagnostics/Evidence.cs`：`Line()`＝必要事件（永遠寫）、`Detail()`＝高頻行（診斷開啟才寫）。
判準寫在 `Detail()` 的註解裡：**這行在正常操作中會一秒出現很多次嗎？** 會 → `Detail`。
目前降級的只有 `progress tick`（轉檔期間每個 tick 一次）。

⚠ **驗收腳本會斷言的行不可以降級**：腳本預設跑在診斷關閉之下。`wheel hover selection` 拖曳時也會
反覆出現，但 m1b S8.9 斷言它 ≥1 次 → 必須留在 `Line`（程式碼裡已留註解，避免以後誤降級）。

每次啟動都會宣告模式（`diagnostics=off (essential events only)`），讓讀日誌的人知道 detail 行是
**被設定擋掉**、不是程式壞了。

驗證（m1f，**雙向**，因為只有單向的開關等於沒被測過）：
- `S14.34` 關：日誌宣告 off、保留 `conversion finished:`、0 條 detail 行，且會大聲說明
  「`--progress-trace` 開著但診斷是關的 → detail 被抑制」。
- `S14.35` 開：日誌宣告 `off -> on`，同一個指令產生 **70 條** detail 行。
- 這條驗收用的指令是 `--wheel-payload`（主路徑上真的會轉檔、又不需要滑鼠的入口）。
  **原本打算用 `--progress-trace` 單獨觸發，實測四個候選指令都不會產生 detail 行**——
  那會變成一條永遠不可能通過的斷言，所以沒寫。

### 觸發鍵——維持單鍵（規格 §8 收窄，§9.4 記明擴充點）

規格 §8 原本把 `Ctrl+Shift` 列為可選觸發鍵，但組合鍵從未實作、且會被明確拒絕。使用者裁定本里程碑
**只支援單鍵**，但不得寫死成無法擴充。擴充點是唯一的解析者 `SettingsStore.TryResolveTriggerKey`。

### 現在還沒生效的設定（誠實清單）

`StartWithWindows`（待辦 4）與 `Language`（只影響設定頁自身的語言）仍未接上；`EmergencyHotkey`
與 `NamingCollision` 依規格是 MVP 固定值。設定頁上每一列都有標示，不會假裝能動。

---

## 待辦 4 完成（2026-10-02）：托盤圖示 ＋ 開機自動啟動

提交 `ad9aaaa`。新增 `src/Sidecar/Tray/TrayIcon.cs`、`src/Sidecar/Startup/Autostart.cs`、
`tools/verify-m1g.ps1`（25 項）；`Program.cs` 接線；四支既有腳本加上 `--no-autostart`。

### 最重要的發現：規格預設值會悄悄改掉使用者的系統設定

規格 §8 的 `StartWithWindows` 預設是 **on**。第一版把它接進常駐路徑之後，**既有的驗收套件開始
在使用者真正的 `HKCU\...\Run` 留下一筆 `Confern`**（指向 `bin\Release`）。
這不是推論出來的：是裝了登錄檔監看、逐秒抓出寫入者（`m1f --settings-ui-live`、`m1b --wheel-payload`）
才確認的。⇒ 新增 `--no-autostart`（三態：`true` 寫入／`false` 移除／`null` 完全不要碰），四支既有
套件的常駐型啟動一律帶上；`m1g` 斷言「跑完一整輪，真實 Run 機碼與跑之前完全相同」。

**規則（已寫進 `Autostart` 的類別註解）**：只有常駐程式會套用開機自動啟動；`--settings-set`
永遠是純設定檔編輯器。否則任何一次自動化驗收都會改掉使用者的開機啟動設定，而那種副作用
事後幾乎不可能被發現。

### 托盤

Win32 `Shell_NotifyIcon`；兩個尺寸的圖示都用 GDI+ 依 `WheelDesign` 當場畫（無素材檔、無新套件）。
選單三項＝開啟設定／暫停轉換／結束；雙擊＝開啟設定頁（設定頁從此有 in-app 入口）。
三處與原規劃不同，理由都成立：

1. **托盤視窗用隱藏的頂層 `WS_POPUP`＋`WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`，不是 `HWND_MESSAGE`**：
   message-only 視窗**收不到** `TaskbarCreated` 這類廣播，explorer 重啟後圖示會靜默消失。
2. **新增 `--tray-exit-after <ms>`**：常駐程式不會自己結束，而驗收不得注入合成輸入、也不該用 Kill
   （Kill 會跳過 `Dispose`，正好遮掉要驗的東西）。它送的是與選單「結束」同一條訊息路徑。
3. **新增 `--no-autostart`**（理由見上）。

### 複核時修掉的安全回歸（第一版真的出貨會出事）

托盤的暫停／繼續第一版直接呼叫守衛的**保險 #5** 路徑：

- `paused` → `guard.OnKillSwitch()`：日誌印出 `*** KILL SWITCH ***`。而 `m1a` 的 S3.8 正是在斷言
  那一行 ⇒ **那條斷言可以被「按一下托盤暫停」滿足**。同時它也把「直到重啟」的閂扣上了。
- `!paused` → `guard.Resume()` 無條件復原 ⇒ **按一下托盤就能解除緊急停用**，與規格 §4.2.2 的
  「停用到重啟」直接矛盾——一條安全保證只剩文件上有寫。
- 托盤照「使用者剛按下的意圖」翻自己的提示文字 ⇒ 復原被拒絕時它會顯示 `running`，而捕捉其實
  是停用的。**會說謊的狀態顯示比沒有更糟。**

修法＝**狀態共用、復原的權力不共用**（去重是這個重構的價值，不能丟）：

- `ReleaseGuard.Pause(why)`＝可復原的托盤路徑，不印 KILL SWITCH、不扣閂；
  `OnKillSwitch()` 才會設下只進不退的 `KillSwitchLatched`；`Resume()` 遇到閂就**大聲拒絕**
  （使用者按了按鈕卻什麼都沒發生，正是他最需要日誌解釋的時刻）。`DragDetector.SetPaused` 同構。
- 托盤改成在回呼跑完之後**去問真正的事實**（`try/finally`），所以它顯示的是結果，不是意圖。
- 正常關機改用 `Pause("clean shutdown")`：從此「日誌裡有 KILL SWITCH」重新等於「熱鍵真的被按過」。
- `m1g` 新增 S15.24／S15.25，**兩者在修改前的程式碼上都會失敗**（已確認失敗的樣子）。

### 這一片的驗收

`m1g` **25/25**、`m1f` **34/34**、`m1b` **50/50（All）**、`m1c` **20/20**、`m1d` **54/54**，
真實 Run 機碼跑前跑後皆乾淨（exe 時間戳 `16:29:28`）。
**`m1a` 沒有重驗**：它的 S3 會合成滑鼠與鍵盤輸入，而使用者當時正在用電腦；先前的 38/38 是
**舊二進位**的數字，而這次的修改動到的正是它 S3 覆蓋的路徑。⇒ 待辦 5 開工前，先請使用者給一個
空檔重跑 `m1a`。

### 尚未驗到（不要當成已驗）

真人用滑鼠點托盤選單（禁止合成輸入；自測走同一條分派）；`TaskbarCreated` 重註冊（需重啟 explorer）；
`S15.11` 只證明 `NIM_DELETE` 被接受，不是 shell 真的放掉了；`S15.7` 只斷言標籤是中文。
另有一次是我自己的錯：`S15.25` 第一版紅燈，查日誌後發現是**斷言用錯了日誌格式**（真實格式是
`tray: paused -> tooltip '…'`），產品是對的——修的是斷言，不是產品。

**下一個＝待辦 5：點擊展開失敗清單（§7.3）。**
