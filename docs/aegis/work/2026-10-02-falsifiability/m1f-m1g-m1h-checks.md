# M1f／M1g／M1h 驗收套件「可證偽性」稽核（91 條逐條分類）

- **日期**：2026-10-02
- **範圍**：`tools/verify-m1f.ps1`、`tools/verify-m1g.ps1`、`tools/verify-m1h.ps1`，以程式化方式掃出**共 91 次 `Check` 呼叫**。其中 `verify-m1g.ps1` 的 **S15.19 有兩條 `Check` 呼叫**（:285 通過分支、:287 失敗分支）但同一判定、同一標籤，因此**獨立檢查 id 為 90 個**；下表逐條列出這 90 個 id（S15.19 併為一列）。套件分佈：m1f 34、m1g 26（＝25 個 id）、m1h 31。
- **方法**：唯讀閱讀三支腳本與其依賴的產品原始碼（`Program.cs`、`TrayIcon.cs`、`Autostart.cs`、`ReleaseGuard.cs`、`DragDetector.cs`、`SettingsStore.cs`、`SettingsUi.cs`、`SettingsWindow.xaml.cs`、`ProgressPresenter.cs`、`AtomicWriter.cs`、`TempJournal.cs`），逐條讀出 predicate 與輔助函式；並**唯讀載入**先前跑次留下的產物（`%TEMP%\m1h-verify\panel-raw-*.png`、`%TEMP%\m1g-verify\logs\tray-selftest-1.log`、`%TEMP%\m1f-verify\logs\*`）量化門檻。未執行任何套件、未建置、未終止任何行程。
- **鐵律**：**一個永遠不會紅的檢查比沒有檢查更糟，因為它讓人相信某件事已經被驗證過。** 本文件的唯一目的，就是找出違反這條鐵律的檢查。
- **統計**：**REAL 57 ／ WEAK 32 ／ DEAD 1**（90 個獨立 id；以 `Check` 呼叫計為 57／32／1＋S15.19 重複那一條）。
  分套件：m1f 34＝REAL 26／WEAK 8／DEAD 0；m1g 25 id＝REAL 15／WEAK 9／DEAD 1；m1h 31＝REAL 16／WEAK 15／DEAD 0。
  - REAL：它所保護的行為若真的壞掉，這條會紅。
  - WEAK：只在狹窄或不現實的條件下才會紅；或它斷言的是**自報**（我們自己寫的 log 行、自己設的旗標、自己寫的檔）而不是外部效果；或功能只做一半它仍然通過。
  - DEAD：根本不可能紅——predicate 恆真、把程式剛寫下的同一個值再讀一次比對、與自己比較、或條件不可達。

> **實測基準值（後續所有門檻討論都用這些數字）**
>
> | 量測物 | 寬×高 | 白墨點數 | 墨跡帶數 | 右半墨跡占比 | minX／maxX |
> |---|---|---|---|---|---|
> | `panel-raw-short.png` | 171×96 | 653 | 2 | 0.361 | 17／134 |
> | `panel-raw-long.png` | 540×96 | 1694 | 2 | 0.396 | 17／503 |
> | `panel-raw-many.png` | 187×334 | 3630 | 9 | 0.406 | 17／150 |
> | `panel-raw-real.png` | 197×62 | 439 | 1 | 0.421 | 17／159 |
>
> 墨點＝`A>200 && R>230 && G>230 && B>230`（`Measure-Ink`，verify-m1h.ps1:139）。
> 另：`tray-selftest-1.log` 中 `tray:` 行 34 行、無前綴的 `tray ` 行 0 行；`diag-off-run-18.log` 的 `progress tick` 0 次而該次執行其實有 `ticks=38`（見 S14.34）。

---

## 1. 逐條分類表（91 次 `Check` 呼叫 ＝ 90 個獨立 id，全部列出）

### 1.1 `tools/verify-m1f.ps1`（34 條）

| 檢查 | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S14.1 | verify-m1f.ps1:76 | REAL | 沒有設定檔時行程仍須以規格預設跑完並回 0；設定載入崩潰就紅。 |
| S14.2 | verify-m1f.ps1:77 | REAL | 六個預設值直接比對規格 §8 字面值，改任何一個預設就紅。 |
| S14.3 | verify-m1f.ps1:84 | REAL | 檔案必須**真的出現在磁碟**且退出碼 0（檔案沒寫出來就紅）。 |
| S14.4 | verify-m1f.ps1:85 | WEAK | 只斷言我們自己那行 `settings set done: applied=3 rejected=0`（自證，但能抓到計數漂移）。 |
| S14.5 | verify-m1f.ps1:89 | REAL | 用產品自己的讀取器把寫入值讀回比對，寫入路徑壞掉就紅。 |
| S14.6 | verify-m1f.ps1:100 | REAL | 對已存在的檔案再存一次（`AtomicWriter` 預設不覆寫，會拋）——覆寫被拿掉就紅。 |
| S14.7 | verify-m1f.ps1:102 | REAL | 覆寫後舊值必須還在（整份被新物件蓋掉就紅）。 |
| S14.8 | verify-m1f.ps1:107 | REAL | 讀磁碟上的原始 JSON 找 `"JpgQuality":100`／`"CaptureTimeoutSeconds":120`，先夾再寫被移除就紅。 |
| S14.9 | verify-m1f.ps1:109 | WEAK | 只斷言自寫的夾制訊息 `clamped JpgQuality 200 -> 100`。 |
| S14.10 | verify-m1f.ps1:116 | REAL | 九個規格鍵必須都在序列化結果裡，漏鍵就紅。 |
| S14.11 | verify-m1f.ps1:117 | REAL | 檔案必須含字面 `Ctrl+Alt+Shift+Q`（`UnsafeRelaxedJsonEscaping` 被拿掉就變 `\u002B` 而紅）。 |
| S14.12 | verify-m1f.ps1:118 | REAL | 原子寫入的 `*.tmp-*` 留在同資料夾就紅（`AtomicWriter.TempPathFor` 確實寫在目標同目錄）。 |
| S14.13 | verify-m1f.ps1:122 | REAL | 未知鍵必須以**退出碼 4** 收場（拒絕路徑被改成靜默就紅）。 |
| S14.14 | verify-m1f.ps1:125 | REAL | 組合鍵同樣要 exit 4 且說出「尚未實作」的理由。 |
| S14.15 | verify-m1f.ps1:129 | REAL | 不可改的 MVP 固定值同樣 exit 4。 |
| S14.16 | verify-m1f.ps1:136 | REAL | 打錯的鍵必須被點名、同時正確的 `JpgQuality=90` 仍載入。 |
| S14.17 | verify-m1f.ps1:143 | REAL | 檔案裡 `VK_TAB` 必須變成 `trigger=VK 0x09`（實際解析值，不是自報）。 |
| S14.18 | verify-m1f.ps1:144 | REAL | `capture=45` 必須變成 `watchdog=45000 ms`（設定沒接到守衛就紅）。 |
| S14.19 | verify-m1f.ps1:145 | WEAK | 斷言來源歸屬字串（來源標記寫死時不紅）。 |
| S14.20 | verify-m1f.ps1:148 | REAL | 命令列旗標必須壓過檔案（優先序反了或旗標失效就紅）。 |
| S14.21 | verify-m1f.ps1:155 | REAL | 警告必須含 `MOVE`、且 `"TriggerKey": "Shift"` 真的寫進檔案。 |
| S14.23 | verify-m1f.ps1:175 | REAL | 設定頁必須在 20 秒內開啟並回 0（開不起來／掛住就紅）。 |
| S14.24 | verify-m1f.ps1:176 | **WEAK** | 那個 `True` 來自 `bool singleInstance = SettingsUi.IsOpen;`（Program.cs:1179，第一次開成功後本來就是 true），與「第二次有沒有被拒」無關。 |
| S14.25 | verify-m1f.ps1:177 | REAL | `closed=True` 是 `!SettingsUi.IsOpen` 的判定，關不掉時那行會寫 `closed=False`。 |
| S14.26 | verify-m1f.ps1:178 | REAL | 行程必須在 20 秒上限內自行結束，關窗後卡住就紅。 |
| S14.27 | verify-m1f.ps1:179 | WEAK | 三行 log 的存在（`STA thread started`／`open requested`／`ok=True`），純自證。 |
| S14.28 | verify-m1f.ps1:194 | WEAK | 只要求日誌出現「in the running app」；`$appAlive` 只是 5 秒後行程還在（常駐行程本來就不會結束），頁面根本沒開也算綠。 |
| S14.29 | verify-m1f.ps1:208 | REAL | 用 OS 再查一次 pid，`--settings-ui` 行程必須真的活著，且 log 不得出現 `hit layer｜visual layer SHOWN`（覆蓋層外洩就紅）。 |
| S14.30 | verify-m1f.ps1:218 | REAL | 真 Save 按鈕的值（71/Tab/45/en）由產品自己的讀取器讀回比對；儲存鏈路壞掉檔案不會變。 |
| S14.31 | verify-m1f.ps1:222 | REAL | 以非法觸發鍵按儲存後檔案必須仍是 71/Tab（拒絕路徑被繞過就會寫入非法值而紅）。 |
| S14.32 | verify-m1f.ps1:243 | WEAK | `colours -gt 50` 只證明「不是單色圖」，畫面內容全毀仍可能通過。 |
| S14.34 | verify-m1f.ps1:270 | WEAK | `$ticksOff -eq 0` 用「數到 0」當成功；presenter 完全沒 tick 也一律綠（現有 log 顯示那次其實有 tick 38 次，所以現況是真過，但斷言方向本身不設防）。 |
| S14.35 | verify-m1f.ps1:275 | REAL | 必須有 ≥1 個 `progress tick`，診斷開關失效或 presenter 不跑就紅。 |
| S14.36 | verify-m1f.ps1:281 | REAL | 真實設定檔前後 SHA256 相同（整場跑動若污染使用者設定就紅）。 |

### 1.2 `tools/verify-m1g.ps1`（26 次 `Check` 呼叫：25 個 id，S15.19 兩分支）

| 檢查 | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S15.1 | verify-m1g.ps1:142 | REAL | 自測回傳碼（13 項有任一失敗就回 1）。 |
| S15.2 | verify-m1g.ps1:143 | REAL | `Shell_NotifyIconW(NIM_MODIFY)` 被 shell 接受，是**外部觀察**（曾用偽造的 NIM_ADD 反證過）。 |
| S15.3 | verify-m1g.ps1:149 | WEAK | 只比對兩行 log（視窗類名＋`menu items=3`）；標題宣稱的 WS_EX_NOACTIVATE 這裡完全沒驗。 |
| S15.4 | verify-m1g.ps1:152 | WEAK | 整條是「我們自己的 log 格式」斷言（`tray ` 前綴 0 次、`tray:` ≥15 次），與托盤是否存在無關。 |
| S15.5 | verify-m1g.ps1:160 | WEAK | tooltip 字串與它的 PASS 同源（`tray.Tooltip`），自證。 |
| S15.6 | verify-m1g.ps1:163 | WEAK | 用**腳本自己寫死的三個標籤**去比對產品自報的 `labels_ok=True`。 |
| S15.7 | verify-m1g.ps1:175 | REAL | 實測三組 regex 都匹配、`nonAscii=True`（見 §5 註 A）；標籤被換成空字串或純 ASCII 就紅。 |
| S15.8 | verify-m1g.ps1:180 | WEAK | 只斷言 callback 的 log 行存在（「命令有被分派」）。 |
| S15.9 | verify-m1g.ps1:181 | REAL | 除了 tooltip，還要求 `shellUpdated=True`（即 `Shell_NotifyIconW(NIM_MODIFY)` 真的回 true）。 |
| S15.10 | verify-m1g.ps1:185 | WEAK | 三行自證 log（dispatch exit／paused=False／tooltip running）。 |
| S15.11 | verify-m1g.ps1:191 | REAL | `NIM_DELETE removed=True` 是 shell 的回答（探針在 dispose 後硬回 false，那半段不是證據）。 |
| S15.12 | verify-m1g.ps1:194 | WEAK | 自測自評 `failed=0 ok=True`；後半段與 S15.2 第三條件**同一行同一段字串**，重複斷言。 |
| **S15.24** | verify-m1g.ps1:207 | REAL | 讀的是**程式狀態** `detector.IsDisabled`——托盤 resume 真的把捕捉重新武裝就紅（詳見 §2）。 |
| **S15.25** | verify-m1g.ps1:212 | REAL | `tray.IsPaused` 是 state；錯誤復原時它會變 false，那行 PASS 與 tooltip 都不再匹配。 |
| S15.13 | verify-m1g.ps1:224 | WEAK | `checks=\d+ failed=0 ok=True`——`\d+` 不排除 0，**自測跑 0 條檢查也會綠**。 |
| S15.14 | verify-m1g.ps1:232 | REAL | 命令列獨立算出「加引號的絕對路徑」再比對產品給的值，不符就紅。 |
| S15.15 | verify-m1g.ps1:236 | WEAK | 六行自報 PASS（「值有 round-trip」仍只是產品自己說的）。 |
| S15.16 | verify-m1g.ps1:250 | **DEAD** | `valuesLeft -eq 0`：**完全沒寫入登錄檔也滿足**（空集合＝成功）。 |
| S15.17 | verify-m1g.ps1:276 | REAL | 常駐執行的 log 必須同時有 `tray: icon registered` 與 `message loop starting`（圖示沒建起來就紅）。 |
| S15.18 | verify-m1g.ps1:279 | REAL | 用 `EnumWindows` 真的拿到 `ConfernTray` 視窗、pid 等於被測行程、exstyle 含 0x8……（不加 WS_EX_NOACTIVATE 就紅）。 |
| S15.19 | verify-m1g.ps1:285／287 | REAL | 前景視窗 pid／類名前後相同才算過；自發性變化時誠實判 INCONCLUSIVE。 |
| S15.20 | verify-m1g.ps1:292 | REAL | 托盤要求結束後行程必須真的回傳 0（訊息迴圈沒退出就紅），log 只是輔助。 |
| S15.21 | verify-m1g.ps1:312 | REAL | 整份設定被改成 `startWithWindows=true` 後真機碼必須**一模一樣**（`--settings-set` 越權寫 Run 就紅）。 |
| S15.22 | verify-m1g.ps1:323 | REAL | 行程結束後讀真機碼值必須等於 `"<exe 絕對路徑>"`（本套最強的一條）。 |
| S15.23 | verify-m1g.ps1:343 | REAL | 自己的補償還原是否有效（restore 失敗就紅）。 |

### 1.3 `tools/verify-m1h.ps1`（31 條）

> 編號 `S16.11` 不存在（腳本從 S16.10 直接跳到 S16.12）；不是漏掉檢查，是編號跳號。

| 檢查 | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S16.1 | verify-m1h.ps1:168 | REAL | dump 必須以 0 收場且 `failed=0`（任何變體畫不出來就回 7）。 |
| S16.2 | verify-m1h.ps1:171 | REAL | 四個 flat PNG 必須存在且能被 System.Drawing 解碼。 |
| S16.3 | verify-m1h.ps1:181 | REAL | 四個 raw PNG 必須存在（S16.2 的子集，但不重複就沒有像素可量）。 |
| S16.4 | verify-m1h.ps1:196 | REAL | 六個變體都必須有可解析的幾何行（缺一個就紅）。 |
| S16.5 | verify-m1h.ps1:204 | REAL | 日誌尺寸 vs 磁碟檔案尺寸，讀到過期／別的檔就紅。 |
| S16.6a | verify-m1h.ps1:214 | REAL | 長文字必須讓面板變寬（固定寬度就紅）。 |
| S16.6b | verify-m1h.ps1:216 | REAL | 實測 Δ=369 px（540 vs 171），門檻 100 px 有真實餘裕（門檻與那串刻意很長的字綁定，字改短才會退化成假門檻）。 |
| S16.6c | verify-m1h.ps1:218 | REAL | 藥丸渲染器同樣必須依文字量測（558 vs 168）。 |
| S16.7 | verify-m1h.ps1:222 | WEAK | 現況 slack=37 px（門檻 10）是真過；但若渲染失敗到沒有任何白色像素，`MaxX=-1` → slack=541 也「通過」。 |
| S16.8 | verify-m1h.ps1:224 | REAL | 實測 inkMinX=17（要求 8–20），左內縮改變就紅。 |
| S16.9 | verify-m1h.ps1:228 | REAL | 9 列 > 2 列 > 1 列的高度單調性（固定高度就紅）。 |
| S16.10 | verify-m1h.ps1:231 | WEAK | expected 與 actual 全來自產品自己的 `rows/shown/overflow/cap`，只驗產品自報自洽（8/9/8/1）。 |
| S16.12 | verify-m1h.ps1:239 | WEAK | 門檻 200/1500 太鬆：實測 short=653、real=439，**原因整列消失（只剩檔名）也會過**。 |
| S16.13 | verify-m1h.ps1:244 | WEAK | `Shown` 來自產品 log；若 rows 變空、兩邊同時歸零則 `0 -eq 0` 通過。 |
| S16.14 | verify-m1h.ps1:247 | REAL | bands 是量到的、cap 來自 log：實測 bands=9=cap+1，掉列或沒截斷就紅。 |
| S16.15 | verify-m1h.ps1:254 | WEAK | 右半墨跡比例只證明「原因欄沒整欄消失」：實測 0.361／0.396／0.406／0.421 對門檻 0.25，內容錯誤不紅。 |
| S16.16 | verify-m1h.ps1:270 | REAL | 走 worker 管線的那次轉檔必須真的失敗（ok=0 failed=1），沒失敗就紅。 |
| S16.17 | verify-m1h.ps1:276 | WEAK | 標題說「exactly one」，實際只驗 `$realLine.Success`（有沒有至少一行）。 |
| S16.18 | verify-m1h.ps1:278 | WEAK | `\S+` 遇上含空白的檔名會抓錯，但沒有匹配時 `$rowFile=''` 與常數不同仍會紅——強度取決於 fixture 的名稱。 |
| S16.19 | verify-m1h.ps1:283 | WEAK | 只驗「非空且含非 ASCII 字元」的形狀。 |
| S16.20 | verify-m1h.ps1:285 | REAL | 原因必須**包含**由 code point 組出的 `無法解碼`，訊息內容錯就紅（本套最有鑑別力的一條）。 |
| S16.21 | verify-m1h.ps1:287 | REAL | `pipe-panel-real.png` 必須真的存在（管線那條的 dump 檔沒寫出來就紅）。 |
| S16.22 | verify-m1h.ps1:297 | WEAK | 門檻 190 是腳本裡的魔數：實測 real 寬 **197 px**，只留 7 px 餘裕，對「真的畫得下原因」沒有鑑別力。 |
| S16.23 | verify-m1h.ps1:299 | REAL | real(197) 必須窄於 long(540)，寬度真的是文字驅動才算。 |
| S16.24 | verify-m1h.ps1:312 | WEAK | 自測自評 `failed=0 ok=True`（`checks=\d+` 同樣不排除 0 條）。 |
| S16.25 | verify-m1h.ps1:315 | WEAK | 只匹配自測自己那行 `PASS nothing is expanded before any toggle`。 |
| S16.26 | verify-m1h.ps1:317 | WEAK | 同上（`PASS drawing the pill is NOT what expands the list`）。 |
| S16.27 | verify-m1h.ps1:319 | WEAK | 四行 PASS 字串的集合。 |
| S16.28 | verify-m1h.ps1:324 | WEAK | 同上（`PASS a new conversion starts with the list collapsed`）。 |
| S16.29 | verify-m1h.ps1:326 | WEAK | 同上（`PASS Contains agrees with the rectangle it just reported`）。 |
| S16.30 | verify-m1h.ps1:333 | REAL | 不得有殘留行程（否則下一輪 exe 被鎖）。 |

---

## 2. S15.24／S15.25 單獨看夠不夠格？（安全回歸後補的兩條）

這兩條是安全回歸之後補的，判讀是：**它們抓得到那個已知回歸型態，但守不住 guard 那一半。**

- S15.24 的第三個條件是 `detector.IsDisabled`（`Program.cs:1525` 的實際狀態），S15.25 的 `tray.IsPaused` 也是 state。實測 `tray-selftest-1.log`：

  ```
  17:17:26.988 guard: REFUSED to resume: the kill switch (insurance #5) latched capture off until restart; restart the program to capture again
  17:17:26.988 detector: REFUSED to resume (tray resume): the kill switch latched capture off until restart
  17:17:26.988 tray: capture paused=True (requested=False) (guard.CaptureDisabled=True, detector.IsDisabled=True, killSwitchLatched=True)
  17:17:26.988 tray: PASS kill switch stays latched: the tray cannot resume capture  [guard.CaptureDisabled=True detector.IsDisabled=True latched=True]
  17:17:26.988 tray: PASS the icon tells the truth after a refused resume  [tray.IsPaused=True tooltip='Confern - paused']
  ```

  若產品真的把捕捉重新武裝，`detector.IsDisabled` 會變 false、tooltip 會翻成 running，那行 PASS 與 `tray: paused -> tooltip …` 都不再匹配 → 兩條都會紅。所以它們**是 REAL，不是劇場**。
- 但 S15.24 的第一個條件 `guard.CaptureDisabled &&` 是**恆真**：`CaptureDisabled` 只在 `ReleaseGuard.Pause()`／`OnKillSwitch()` 被設為 true，而 `Resume()` 一旦 `KillSwitchLatched` 就提前 return（`ReleaseGuard.cs:167`）——**沒有任何路徑能在同一行程內讓它變回 false**。也就是說「kill switch 不能被托盤解除」這條保證，實際上**只由 detector 那一半在守**；若哪天回歸發生在 guard 側的閂（例如有人把 `KillSwitchLatched` 檢查移出 `Resume`）而 detector 側仍正確，S15.24 依然綠。
- 另外，兩條都要求產品自己吐出 `guard: REFUSED to resume`／`detector: REFUSED to resume` 與那行自測 PASS；若 log 文字被改（而行為正確）會「紅在錯的理由上」——這在 m1g 是常態風險（腳本註解自承第一版 S15.25 就是這樣誤紅的）。

---

## 3. 五個最可疑檢查 ＋ 最小破壞實驗

以下實驗都**未執行**（撰寫時機器上有別的 job 在跑；要跑請先確認沒有其他套件在動）。

### ① m1h `S16.13`（verify-m1h.ps1:244）— 與自報值同時歸零即通過

- 斷言：`(($mShort.Bands -eq $short.Shown) -and ($mReal.Bands -eq $real.Shown))`，其中 `Shown` 由 `Get-Variant`（:113）從產品自己的 `shown=` 讀出。
- 破壞：在 `ProgressPresenter.ComposeDetail` 開頭加 `if (rows.Count <= 2) return new Bitmap(1,1, PixelFormat.Format32bppArgb);`，同時讓 `RunProgressDumpDetail` 的 log 對這兩個變體寫 `rows=0 shown=0`。
- 預測：`bands=0`、`shown=0` → `0 -eq 0` 成立 → S16.13 **通過**（錯誤理由：兩個來源一起壞）。反之若只壞圖、log 照寫 shown=2，才會紅——這條檢查的效力完全取決於「產品有沒有老實申報」。

### ② m1h `S16.12`（:240）— 門檻鬆到容得下「原因欄整列消失」

- 斷言：`(($mShort.Ink -gt 200) -and ($mLong.Ink -gt 200) -and ($mMany.Ink -gt 1500) -and ($mReal.Ink -gt 200))`。
- 破壞：把 `texts[i] = rows[i].Text` 改成只畫檔名（`rows[i].FileName`），保留面板與紅色底。
- 預測：以實測比例（short 653、real 439，檔名欄約占 6 成）短面板約 400 墨點、real 約 250–300，**兩者都 >200 → S16.12 通過**；S16.15 的 rightShare 會掉到 ~0.1 以下而變紅——S16.12 單獨看幾乎沒有鑑別力。

### ③ m1f `S14.24`（verify-m1f.ps1:176）— 斷言的是「第一次有沒有開成功」

- 斷言：`$uiText -match 'second open refused \(still exactly one window\)=True'`；來源是 `Program.cs:1179` 的 `bool singleInstance = SettingsUi.IsOpen;`。
- 破壞：把 `SettingsUi.Open` 的單例守衛（`_isOpen` 檢查）移除，讓第二次呼叫真的開第二個視窗。
- 預測：`SettingsUi.IsOpen` 仍是 true（第一個視窗還開著），log 照樣印 `=True`，**S14.24 通過**；S14.25／26 也不會紅（關掉的是其中一個視窗）。唯一改變的是 log 多一行 `settings ui: open requested`——而沒有任何檢查在數視窗。

### ④ m1g `S15.16`（verify-m1g.ps1:250）— 空結果＝成功

- 斷言：`$leftBehind -eq 0`，其中 `$leftBehind` 是「子機碼存在時的值個數，否則 0」。
- 破壞：把 `Autostart.Enable()` 改成只寫 log、完全不碰登錄檔（`key.SetValue` 整段刪掉）。
- 預測：子機碼從未被建立 → `Test-Path` false → `$leftBehind=0` → **S15.16 通過**；S15.13 因為讀的是自報 `checks=\d+ failed=0 ok=True` 也**通過**；只有 S15.14（期望值純函式）與 S15.22（讀真機碼）會紅。這是「用 0 當成功」的教科書案例。

### ⑤ m1h `S16.22`（:298）— 寫死的 190 px 魔數，只留 7 px 餘裕且不具鑑別力

- 斷言：`($mReal.Width -ge 190)`；實測 `panel-raw-real` 寬 **197 px**。
- 破壞：把 `ComposeDetail` 的 `width = Math.Max(width, (int)Math.Ceiling(needed) + DetailPadX*2 + HorizontalPadding)` 改成以固定下限為主（`Math.Max(BitmapWidth, …)`）／讓 padding 不隨文字量測成長，只要 real 那行仍量到 ≥190 就綠。
- 預測：這條對「固定／半固定寬度」的迴歸**沒有**分辨力（真正的固定寬度會先讓 S16.6a 紅）；反過來任何讓 real 行縮短 8 px 的合法改動（例如繁體原因字串改短、字型微調）都會讓它**紅得莫名其妙**。同一次執行的相對比較是 S16.23 在做的，S16.23 才是有效的那一條。

> 候補：`S14.32` 的 `colours -gt 50` 對 500 ms 內繪製的深色設定頁幾乎必定成立——把整頁畫成單一漸層仍會通過。`S15.4` 則純粹是 log 格式。

---

## 4. 結論（不客氣版）

這三套**不是劇場，但也遠不是鐵壁**：90 個獨立檢查裡 57 條是真的（m1f 的檔案往返／夾制／退出碼、m1g 的 registry 真值與 exe 路徑、m1h 的像素×日誌交叉比對都是真證據），但有一整層是「把產品自己剛寫下的 log 再讀一遍」——**m1g 最嚴重**：托盤那 12 條幾乎全是自證，真正碰到 Windows shell 的只有 S15.2（NIM_MODIFY 被接受）、S15.11（NIM_DELETE 被接受）與 S15.18（視窗存在且 exstyle 正確），其餘（tooltip、選單文字、三個 dispatch、exit 0）都只證明「自測跑了並自己說通過」；**m1h 的像素斷言門檻鬆到只證明「圖不是全空」**（ink>200 對實測 439、colours>50、rightShare>0.25 對實測 0.42、width≥190 對實測 197），其中 S16.13 還把產品的自報值當成一半的預期值；**m1f 則是一半強一半弱**——S14.8／S14.30／S14.31 會在功能真的壞掉時紅，但 S14.24（數的是第一次開成功）、S14.28（只看到一行 log，行程還活著不代表頁面開著）、S14.34（「數到 0」當成功）在功能缺席時仍然綠。最該優先修的四件事：(1) 把 `checks=(\d+) failed=0 ok=True` 這類自評改成帶下界（`checks=[1-9]\d*`）；(2) S14.24 改成實際數視窗（或 `SettingsUi` 暴露 window count）；(3) m1h 的 ink／寬度門檻改成「同一次執行內兩變體的相對比較」而非絕對魔數；(4) S15.16 改成「Enable 之後機碼真的存在」的正向斷言。最後要說一句不客氣的話：**S16.24–S16.29 那六條（自測 PASS 字串比對）與 S15.4、S15.12 這種「我們自己的 log 格式」斷言，正是鐵律所指的那種檢查——它們永遠不會紅，卻會讓看報告的人以為規格 §7.3 的展開行為與托盤事件流已經被獨立驗證過了。**

---

## 5. 附錄：兩個環境陷阱與一則方法註記

**註 A（S15.7 的實證，推翻「ASCII-only 腳本比對 CJK」的初判）**：在現有 `tray-selftest-1.log` 上實際執行腳本第 165 行的 regex，`success=True`，group1／2／3 分別為 `開啟設定`／`暫停轉換`／`結束` 且 `nonAscii=True`；`tray:` 行 34、無前綴的 `tray ` 行 0。所以 S15.7 是 REAL。（初判為 DEAD 的理由是「腳本刻意純 ASCII，卻在 :164 硬寫了 `labels_ok=True` 與三個 CJK 標籤字面值」，與實際行為不符——留此記錄以免後人重犯同樣的推論。）

**註 B（環境陷阱 1）**：`verify-m1g.ps1:306` 的
`$runBeforeSet = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runName -ErrorAction SilentlyContinue).$runName`
**少了** `-ErrorAction SilentlyContinue`（同檔 :113 有帶）。實測：當 Run 機碼存在但 `Confern` 值不存在時，`Get-ItemProperty -Name <不存在的值>` 會發出**非終止錯誤** `Property Confern does not exist at path HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run.`。因為 `$ErrorActionPreference='Continue'`，判定不受影響（`$v` 仍為 `$null`），但 stderr 會多一則錯誤訊息，容易被讀報告的人誤判為失敗；目前真機碼狀態正是「機碼存在、值不存在」，所以每次跑 m1g 都會出現。修法：補上 `-ErrorAction SilentlyContinue`。

**註 C（環境陷阱 2）**：`verify-m1h.ps1:273` 的
`$realLine = [regex]::Match($payloadText, "detail row: file=(\S+) reason='([^']*)'")`
假設**檔名不含空白**。`\S+` 遇到空白會在檔名中間停住，於是 `S16.18` 的 `$rowFile -eq $FILE_DECODE` 會因為抓到半個檔名而紅——紅在 fixture 的名稱上，不是紅在產品的「檔名／原因怎麼分」契約上（而那個契約正是 `Program.cs:1831-1841` 註解說這條路要守的東西）。同一類綁定也出現在 `S16.22` 的 190 px：它是「`corrupt.png 無法解碼`」這一組 fixture 量出來的產物（實測 197 px），換一組合法的中文原因就會變成假門檻。建議日後把這兩處改成與 fixture 無關的相對比較（例如與同一串文字的 `MeasureString` 期望寬度比較），或至少把檔名 regex 改成 `(.+?) reason=`。
