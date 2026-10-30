# 接手起點：功能開發（2026-10-02）

這份是**換 session 後的起點**。UI 設計已暫定，這一輪回到功能開發。

## 現況（2026-10-22 更新）

- `HEAD = a5f1d33`（下拉選單白底白字修好）之後再加一筆進度記錄、分支 `master`、單一 worktree、
  **工作樹乾淨**。
- UI 方向 D（玻璃輪盤）已落地並驗收；設計**暫定**，日後要調再說。
- 設計側已定案、**不要重做**的兩件事：
  - 光暈**關閉**（`WheelDesign.GlowEnabled = false`；強度/厚度/圈數參數全部保留，改 `true` 即還原）。
    實機「一大圈」的元凶就是光暈本身，使用者看過關閉版後表示喜歡。
  - 扇形外緣尖點 **7px 圓角**（`WheelDesign.SectorCornerPx`，`RoundedSectorPath`）。
- **回歸現況（2026-10-22 實測，同一支 exe，時間戳 `10:10:53`）**：12 支套件
  **462 條：436 pass、0 fail、26 inconclusive**。逐套數字見
  `docs/aegis/plans/2026-10-02-progress-log.md` 的「驗收數字」表。
  - `m1a` **28/28**（另有 20 條需真實輸入）、`m1b` **52/52（務必加 `-Section All`；預設只跑
    `S6`＝13 項；我自己的 sweep 腳本也漏過這個參數）**、`m1c` 20/20、`m1d` **74/74**、
    `m1f` **46/46**、`m1g` 25/25、`m1h` **56/62**（6 條需真實輸入）、`m3c` 20/20、
    `m3d` 39/39、方向矩陣 8/8、合併圖片 26/26、資料夾拖曳 42/42。
  - ⚠ 本文件先前寫的「`m1a` 38/38」「`m1b` 50/50」「`m1d` 54/54」都已過時，以 10-22 這組為準。

## 2026-10-21／22 這兩天做了什麼（接手時不必重做）

1. **介面八語言**（`m1f` S14.37）：`UiStrings` 8 張表 × 206 鍵；`SystemLanguage()` 改 LANGID；
   `--strings-selftest` 一次看完八張表；法／德／西原本**整張表沒接到**（`Resolve` 的 bug）。`550a7d5`、`8970e89`
2. **第六套配色「冷瓷」＋換配色當場重繪**（`m1f` S14.32f/g 把守衛擴到每套配色）。`761eb52`、`0c4cd2c`
3. **設定頁下拉選單白底白字**（`m1f` S14.38）：前兩次只修「展開的清單」，真正的白是**收起來的框**
   ——Aero2 的樣板根本不畫我們的底色。現在接管 ComboBox 自己的樣板，展開面板同底色。
   `a5f1d33`
4. **命令列表面**（`1b38bfc`，`tools/verify-cli.ps1` 46/46）：`confern convert <file>... --to FMT
   [-o DIR]`、`confern formats`。失敗碼 0／9（有檔轉不動）／4（命令本身錯）。轉檔仍走同一條
   `ConversionEngine`→`NamingPolicy`，所以「永不覆寫」對 CLI 一樣成立；舊的 `--convert` 旗標
   行為一字未改。順手修掉兩個實測抓到的真缺陷：①**常駐程式在跑時，任何診斷／CLI 命令都會
   崩潰**（預設日誌被獨占 ⇒ `Unhandled exception`，已改成退到 `confern.<pid>.log`）；
   ②**`--quality` 從來沒生效過**（`--quality 20` 的 `20` 被當成輸入檔 ⇒ 每次都跑預設 85；
   現在有 `CliArgs.ValueFlags` 表 + `verify-cli` S8 這條原始碼級斷言盯著它）。
   另新增 `tools/verify-all.ps1`：一次跑完所有套件並印總表，**固定給 `m1b` 帶 `-Section All`**
   （2026-10-22 那顆「13/13 全綠」的假綠燈就是漏了它），且先殺 Confern、先印 exe 時間戳。
5. **監看資料夾**（`tools/verify-watch.ps1` 39/39）：`confern watch <folder> --to FMT` 前台轉檔、
   `watch --add／--list／--remove` 寫 `settings.json` 的 `WatchFolders`，常駐程式啟動時自動開始。
   引擎在 `src\Sidecar\Watch\`（`FolderWatcher` 輪詢 250 ms、安靜 400 ms 才算寫完）；
   **沒有動任何視覺設計**（設定頁沒多一列）。三個實測抓到的問題寫在進度記錄那一節：`--add` 其實
   開了前台監看、`--list` 被子命令名吃掉、半寫入檔案需要「失敗可重試」而不是調門檻。
6. **安裝檔**（2026-10-23，`tools/build-installer.ps1` ＋ `tools/installer/confern.iss`）：一個
   **18.44 MB** 的 `Confern-Setup-0.1.0.exe`（Inno Setup 6.7.3）。缺 .NET 8 桌面執行階段時會先問
   使用者要不要自動下載安裝，選否則結束安裝；每位使用者裝到
   `%LOCALAPPDATA%\Programs\Confern`，免 UAC、有捷徑、可正常解除安裝、同 AppId 可覆蓋升級。
   腳本自己會**靜默安裝到臨時資料夾→用裝起來的 exe 跑 `formats`→靜默解除安裝→確認 exe 不見**，
   並斷言安裝日誌裡的 `dotnet8: present=True`。細節與三個編譯坑在進度記錄
   「2026-10-23 · 安裝檔」那一節。**沒驗到的**：這台有 .NET 8，所以「缺 .NET」那個對話框本人
   沒有實測（要有乾淨機器或 VM）。
7. **最近轉檔**（2026-10-23，`tools/verify-recent.ps1` 43/43）：托盤右鍵多一個「最近轉檔」子選單
   （最多 8 筆檔名 ＋ 清除紀錄），命令列多了 `confern recent [--limit N] [--clear]`。資料在
   `%LOCALAPPDATA%\Confern\recent.json`（上限 12 筆、原子寫入、輸出檔不見就移除、壞檔退回空清單
   並說原因、被別的行程改過會重讀）。三條登記路徑：`ConvertOnce`（CLI）、`ConversionClient`
   （worker 結果那一側，才知道是哪顆扇形）、`FolderWatcher`；**worker 不寫**。
   **沒有動任何視覺設計**。驗收抓到的兩個真缺陷（`StreamWriter` 沒 `leaveOpen` 導致紀錄永遠寫不
   進去；`--recent-limit` 單獨出現會掉到常駐分支而卡死驗收十分鐘）寫在進度記錄那一節。
   **驗收會寫進使用者的清單**這件事也修了：`CONFERN_RECENT` 環境變數（`verify-all` 自己設），
   所以套件真的轉檔時紀錄只會落在 `%TEMP%`。
8. **每種檔型一組預設**（2026-10-23，`tools/verify-presets.ps1` 40/40）：`confern preset --set
   HEIC=JPG:82` 之後，拖 HEIC 進來時輪盤**一開始就停在那顆扇形**，而且那個品質真的送進編碼器
   （同一張圖品質 60 = 9,794 B、100 = 20,686 B）。資料在 `%LOCALAPPDATA%\Confern\presets.json`
   （一筆三個欄位；與 `settings.json` 分開，所以**零 UI 改動**是可檢查的事實）。品質優先序只有
   一份：命令列明打 `--quality` &gt; 預設 &gt; 設定頁（為此 `--quality` 多了一個「是否明打」的位元）。
   `--set` 會先驗來源格式／目標格式／品質三件事，任何一件不過就 exit 4 並說清楚——**存得下卻永遠
   不會生效的預設比沒有更糟**。同一天驗收還抓到兩個真缺陷：`confern preset --log` 與
   `confern recent --log` 會掉到常駐分支開出托盤（CliArgs 用 `rest.Count == 0` 判斷模式，共用旗標
   也算進去了），以及產品自己的說明文字把名字拼成 `conforn` **29 次**（使用者第一眼就會照著打）。
   細節與實測數字在進度記錄「2026-10-23 · 每種檔型一組預設」那一節。

## 下一步（建議順序，出自 `2026-10-01-ui-redesign.md` §7）

1. ~~**中文失敗原因的自動斷言**~~ **已完成（2026-10-02）**：`m1d` 新增 11 項
   （`S12.6d/7c/8c` 渲染行、`S12.8d/e` 斷言自測、`S12.8f/g` 部分失敗、`S12.15a–d` UI 藥丸路徑），
   `m1d` 43 → **54**。
2. **真實拖曳 S4 結案（軟體面已完成並自動驗證；人工手勢仍待使用者）**——⚠ 原定「用當日 21 次真手勢日誌」**已不可行**：本程式
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
3. **設定頁 M1f 本體**——規格 §8。**2026-10-02 完成：模型層與頁面本體都已完成並驗收（`m1f` 34/34）**：
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

# 安裝檔（publish → ISCC → 靜默安裝 → 用裝起來的 exe 跑 formats → 解除安裝）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
# 產物: dist\Confern-Setup-<version>.exe（版本讀 Sidecar.csproj 的 <Version>）

# 一次跑完 16 支（自己會先殺 Confern、印 exe 時間戳，最後給總表）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-all.ps1 -Exe <exe>

# 使用者看得到的三個新入口
#   confern recent / confern recent --limit 5 / confern recent --clear   （托盤右鍵的「最近轉檔」）
#   confern preset / confern preset --set HEIC=JPG:82 / --remove / --clear （輪盤一開始停在哪）
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
`tools/start-confern.cmd`（2026-10-05 由 `start-tangerine.cmd` 改名；桌面捷徑同時改名為
`Confern.lnk` 並改指產品本體）。

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

### 現在還沒生效的設定（誠實清單，2026-10-22 更新）

- `StartWithWindows`：**已生效**（待辦 4，`Autostart` 是 `HKCU\...\Run` 的唯一擁有者）。
- `Language`：**已生效**——設定頁整頁跟著換（8 張語言表 × 206 鍵），輪盤扇形標籤也跟著換
  （提交 `483d249`）。托盤選單四個項目讀的是 `UiStrings.Get("tray.*")`
  （`TrayIcon.cs:47-50`），所以也跟著語言換；但「真的用滑鼠點托盤」仍未有獨立證據（舊帳）。
- `Palette`：**已生效**，改了就當場重繪整頁（`761eb52`、`0c4cd2c`）。
- `EmergencyHotkey` 與 `NamingCollision`：依規格是 MVP 固定值，設定頁有標示，不會假裝能動。
- 失敗清單的 6 條滑鼠項、`m1a` 的 20 條（S3／S4）、`m1h` 的 6 條：**需要真實輸入**，仍未跑。

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
**`m1a` 已在同一顆新二進位上重驗（16:44–16:45）：38/38、0 失敗、1 未定**（未定＝S4 需要
`DragSim.exe`，就是人工拖曳那一件事；runner 因此回 exit 3＝「有未定項」，不是失敗）。
S3.8「緊急停用真的被合成熱鍵觸發」、S3.9「偵測器停用」、S3.10「之後不再升起（2→2）」
**全數通過**：托盤那條安全修正沒有破壞保險 #5 的真實路徑，而且 S3.8 從此只可能被真正的熱鍵
滿足（托盤的暫停已經不再印那一行）。

### 尚未驗到（不要當成已驗）

真人用滑鼠點托盤選單（禁止合成輸入；自測走同一條分派）；`TaskbarCreated` 重註冊（需重啟 explorer）；
`S15.11` 只證明 `NIM_DELETE` 被接受，不是 shell 真的放掉了；`S15.7` 只斷言標籤是中文。
另有一次是我自己的錯：`S15.25` 第一版紅燈，查日誌後發現是**斷言用錯了日誌格式**（真實格式是
`tray: paused -> tooltip '…'`），產品是對的——修的是斷言，不是產品。

**下一個＝待辦 5：點擊展開失敗清單（§7.3）。**

---

## 待辦 5 設計記錄（2026-10-02）：點擊展開失敗清單

### 規格要什麼（§7.3，原文）

> 輪盤收起處顯示單行摘要：`✓ 8 / ✗ 2`，可點擊展開清單（檔名 + 原因）。清單**不自動彈出**，不打斷流程。

### 已經確認的技術前提（讀過程式碼，不是猜的）

- 那一行由 `ProgressPresenter.Compose` 繪製，寬度依文字量測（所以失敗訊息不會被裁掉），
  顯示在 `_layer.Show(_x - w/2, _y - h/2, bitmap)`——**藥丸自己知道它的矩形**，命中區域不該另立第二份幾何。
- 失敗資料已經有單一擁有者：`ConversionSummary`（`Failures`）與 `ConversionResult.UserMessage`（規格 §7.1 的
  **繁體中文**訊息，待辦 ① 的成果）。清單**不需要新的訊息來源**。
- `ComposeLine` 的註解目前明寫「點擊展開清單尚未實作」——實作後要一起改掉（文件與程式不一致就是下一個人的陷阱）。

### ⚠️ 需要使用者裁決的岔路：點擊要用哪一種方式收到？

| 方案 | 做法 | 代價 |
|---|---|---|
| **A. 專屬可點視窗**（我的建議） | 藥丸改由自己的小型 layered 視窗承載（`WS_EX_NOACTIVATE`／`WS_EX_TOOLWINDOW`，**不加** `WS_EX_TRANSPARENT`；繪製程式碼不變，只有容器改變）。清單展開時同一個視窗長大。 | 藥丸從共用視覺層搬出來，動到「容器」而不是「外觀」；視窗會**吃掉**那一下點擊（＝不會順便點到桌面上的圖示，這其實是好事） |
| **B. 走現成的全域滑鼠鉤子** | 不動視窗，用 `HookService` 既有的滑鼠事件＋藥丸矩形做命中判定。 | 鉤子**不能攔阻**事件（`非觸發拖曳零侵入` 是核心不變式），所以點藥丸會**同時點到下面的東西**（例如選到桌面圖示）。零新視窗，但把一個副作用留給使用者 |

A 與 B 都能滿足規格；差別是「那一下點擊要不要被吃掉」。A 動容器、B 動不變式——我建議 A。

### 另一個要一起決定的小事：藥丸停留時間

規格 §4.4 的成功淡出是 2 秒；程式對**有失敗**的情況已經留 5 秒（`FailureHoldMs`）。
但 5 秒內要去點一顆小藥丸，實際上很難點到——**這個功能會變成看得到、點不到**。
建議：**有失敗時，游標停在藥丸上就不淡出**（離開後照原本的 5 秒），成功時維持 2 秒不變。
成功路徑的行為完全不動。

> **已定案，而且停留秒數後來被修訂（2026-10-05）**：選的是「游標停在藥丸上就不淡出」這一項，
> 但使用者真人實測後把停留時間定為**一律 2 秒**（失敗與成功相同）。5 秒從來不在規格裡——
> 規格 §4.4 只寫「完成後 2 秒淡出」，5 秒是實作多加的，所以這是**改回規格**而不是改規格。
> 詳見 `docs/aegis/plans/2026-10-02-progress-log.md` 文末的 2026-10-05 修訂。

### 實作範圍（不論選 A 或 B 都要做，先做這部分不需要裁決）

1. `ConversionResult` 拆出 `Reason`（**不含檔名**的原因）並讓 `UserMessage` 由它組出來——純重構，
   現有訊息必須**一字不變**（m1f／m1d 已經在斷言那些字串，等於現成的回歸網）。清單才有「檔名 ＋ 原因」兩欄可排。
2. `ConversionSummary` 提供「檔名 ＋ 原因」的列資料（單一擁有者，畫面不再自己拼）。
3. `ProgressPresenter`：記住最後顯示的矩形、`Contains(x,y)`、`IsExpanded`、`ToggleDetail()`、
   以及 **static `ComposeDetail(rows, alpha)`**——抽成 static 的理由與 `Compose` 相同：
   要能在**沒有畫面**的情況下 dump 出來驗證字形與裁切（`✓`／`✗` 缺字會變方框，看程式碼看不出來）。
4. 清單外觀沿用同一套語彙：`WheelDesign.NoticeFill` 的錯誤紅、同一顆藥丸式圓角、同一支字型 helper、
   同一套 alpha。上限顯示 8 列＋「…還有 N 筆」，避免清單長到蓋住整個畫面。
5. 驗收：headless dump（`--progress-dump-detail`）＋ 一條「真的讓某個檔案轉檔失敗」的端到端斷言
   （m1d 已經有壞檔 fixture），斷言畫面資料裡**每個失敗列都有檔名、都有中文原因**。

---

## 待辦 5 進度（2026-10-02）：資料與渲染完成，接線待裁決

提交 `1abc0fa`（新增 `tools/verify-m1h.ps1` 31 項；動到 `ConversionTypes`／`ConversionClient`／
`WorkerServer`／`ProgressPresenter`／`Program`）。**完成的是不需要裁決的那一半。**

- `ConversionResult.Reason` 成為原因的單一擁有者，`UserMessage` 由它組出 ⇒ 規格 §7.1 的訊息
  **一字未變**（`m1f` 34/34、`m1d` 54/54 就是回歸證據，是跑出來的不是看出來的）。
- **管道新增 `sourcePath`**（`WorkerServer` → `ConversionClient` → 快照 `FailureFiles`）：
  UI 端原本只有一個拼好的字串，要拆回「檔名／原因」只能猜界線——那正是這個專案一直在付學費
  消滅的第二個真相來源。認不出前綴時退化成「整串當原因、保留檔名」（**這條退路未測**）。
- `ProgressPresenter`：`PillRect`／`Contains`／`IsExpanded`／`ToggleDetail()`／`static ComposeDetail`。
- `Program`：`--progress-dump-detail`／`-raw`／`--progress-detail-selftest`；**帶 `--wheel-payload`
  時不走無頭 dump**，改由真實 worker 轉檔後 dump——否則行程提前結束、驗收只看得到合成列，
  會是靜默假通過。
- 假故障（每個都先紅再恢復成綠）：固定寬度 → 5 項紅；上限 8→7 → 紅；靜默丟掉最後一列 → 紅；
  拿掉中文原因 → 紅。第一項還逼出一個真問題：**寬度不足時文字是折行不是截斷**，固定行高會把
  第二行裁掉，已用 `NoWrap + EllipsisCharacter` 明確處理並註解記錄。

### 驗收

`m1h` **31/31**、`m1f` **34/34**、`m1b` **50/50（All）**、`m1c` **20/20**、`m1d` **54/54**、
`m1g` **25/25**，全部 exit 0（exe `16:57:48`；我另外確認「比 exe 新的原始碼檔＝空」，所以不是
增量假象）。**`m1a` 未跑**：它注入合成滑鼠鍵盤，而這一片沒有動到它覆蓋的路徑。
證據 dump 在 `docs/aegis/work/2026-10-02-failure-list/`。

### 尚未驗到（不要當成已驗）

沒有接線 ⇒ **點擊怎麼送進來完全未驗**；字形檢查量的是墨跡與字寬，**不是字形遮罩比對**
（缺字只會被間接抓到）；`FromMessage` 前綴認不出來的退路未測；面板沿用藥丸既有行為，
**沒有螢幕邊界處理**；`WorkerMessage.sourcePath` 的成功路徑沒有斷言。

### 四個待裁決（寫在進度筆記給使用者看，實作前不動）

D1 點擊用專屬視窗（建議）還是既有鉤子；D2 有失敗時游標停在藥丸上就不淡出（建議）；
D3 面板圓角統一成 `min(高度/2, 18px)`（建議）還是維持現狀；D4 極長檔名加上限＋中間省略（建議）。
成功路徑的 2 秒淡出四個決定都不影響。

另記一則：獨立的視覺檢查說「錯誤紅太刺眼、與冷鋼／薄荷定位不符」——**不採納**，
那是凍結的 `WheelDesign.NoticeFill`，全產品錯誤色只有一個來源。不知情的審美意見不是證據。

---

## 待辦 5 · D1＋D2 實作規格（2026-10-02 定案，接線用）

使用者已核准 D1＝**專屬可點視窗**、D2＝**失敗藥丸游標停留時不淡出**。以下是可直接照做的規格。

### 為什麼是局部改動

`Program.cs:673` 的 `progressLayer = new VisualLayer(evidence)` 是**藥丸專屬**的實例（輪盤用 670 那個），
所以「換容器」不會波及輪盤。現有 `VisualLayer` 對外只有 6 個成員被 `ProgressPresenter` 用到：
`Show(int,int,Bitmap)`／`Hide()`／`IsVisible`／`Frames`／`StartTick(int)`／`TickRequested`。

⇒ 抽出這 6 個成員的小介面，`VisualLayer`（**原封不動**）與新的 `PillWindow` 共同實作，
`ProgressPresenter` 只依賴介面。

### `PillWindow` 的規格

- 類名 `ConfernPillWindow`；**頂層** `WS_POPUP`；exstyle `WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`，
  **絕對不加** `WS_EX_TRANSPARENT`（那正是目前點不到的原因）。
- 繪製沿用 `UpdateLayeredWindow`（與 `VisualLayer` 同一條路徑），**沒有新套件**。
- 訊息：`WM_LBUTTONUP` → 回呼（`ToggleDetail()`）；`WM_MOUSEACTIVATE` → `MA_NOACTIVATE`（不搶焦點）；
  `WM_MOUSEMOVE` ＋ `TrackMouseEvent(TME_LEAVE)` → 進入／離開（**D2 唯一的實作方式**）。
- 保留 `WndProc` 委派以免被 GC 回收（`TrayIcon.cs:60` 已有同樣的註解與做法）。
- 與訊息迴圈同執行緒（藥丸本來就在主執行緒建立）。

### 幾何：仍然只有一份

- 視窗位置與大小＝**同一個 bitmap 的尺寸**，由 `ProgressPresenter` 決定（收合＝藥丸；展開＝藥丸＋面板
  合成在同一張 bitmap 上的堆疊），呼叫端不得自己算第二份。
- 展開時的合成也要能 **headless dump**（新增一個 `expanded` 變體），否則展開狀態的版面
  只能靠肉眼，而這個專案已經證明「靠肉眼」會在裁切與折行上出錯。

### 展開後的生命週期（規格沒寫，這裡定案）

- 點藥丸＝展開；再點一次（藥丸或面板上任何位置）＝收合。
- 收合狀態：**游標停在藥丸上就不淡出**，離開後 2 秒淡出（成功與失敗相同）。
- 展開狀態：游標停在上面就不淡出；游標離開後**同樣 2 秒**淡出（與收合同一條規則，不再分開設定）。
- **2026-10-05 修訂**：原本失敗／展開是 5 秒，使用者真人實測後定為一律 2 秒。5 秒是實作多加的，
  規格 §4.4 只寫「完成後 2 秒淡出」；讀清單的保護由上面的 hover 規則提供，不是靠拉長秒數。
- **不做 Esc**（我們不取焦點，收不到鍵盤）；**不做點擊外部關閉**（視窗只收到自己範圍內的點擊）。
  這是刻意的取捨：多一個隱形的全局鍵盤鉤子比多一個 2 秒計時器危險得多。

### 驗收（可失敗、且不靠自證）

1. **真實合成點擊**（SendInput）落在藥丸上：`IsExpanded` 翻轉、視窗尺寸變成合成後的大小、
   面板的墨跡真的出現在畫面上（抓那一塊螢幕像素來量，不是讀我們自己的 log）。
2. **點在藥丸外不得展開**（`IsExpanded` 仍為 false）。
3. **不搶焦點**：點擊前後的前景視窗必須是同一個（`m1g` S15.19 已有同樣手法可抄）。
4. **停留不淡出**：合成滑鼠移入後，超過原本的淡出時間視窗仍在且 alpha 未降。
5. **一份幾何**：視窗矩形必須等於回報的 `PillRect`（或展開後的合成矩形），不得各自為政。
6. **無畫面自測**：`--pill-selftest` 對真實的 `WndProc` 送 `WM_LBUTTONUP`（不必真的動滑鼠）
   ⇒ 走的是同一條訊息路徑，可當第 1 項之外的獨立證據。
7. 假故障演練：`WM_LBUTTONUP` 改成不做任何事 ⇒ 第 1 項必須紅；移除 `MA_NOACTIVATE` ⇒ 第 3 項紅；
   移除 `TrackMouseEvent` ⇒ 第 4 項紅。

### 這一片的硬要求

純 ASCII 的 `.ps1`（PS 5.1 會把中文註解吃成 ANSI，這個專案已經踩過）；先殺 `Confern`；
建置後確認 exe 時間戳有變**且 `src\` 底下沒有比 exe 新的檔案**；不加新套件；
不動 `WheelDesign`／光暈／扇形圓角；不 commit；不改 `docs/`。
