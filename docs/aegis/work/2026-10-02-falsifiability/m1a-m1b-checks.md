# M1a / M1b 驗收套件：逐條可反駁性稽核（125 條 Check）

## 範圍與方法

- **範圍**：`tools/verify-m1a.ps1`（54 個 `Check` 呼叫點）、`tools/verify-m1b.ps1`（71 個），合計 **125 條**。
- **方法**：逐條讀出 predicate 及其依賴的 helper（`Check`/`Test-Log`/`Test-LogCase`/`Count-Log`/`Measure-LogGapMs`/`Get-OfferedOutputsCsv`/`Get-ShownTargetsCsv`/`Remove-FromCsv`），並回頭讀被測程式碼對照日誌行的產生處（`Program.cs`、`Interaction/HitLayer.cs`、`Visual/VisualLayer.cs`、`Visual/WheelPresenter.cs`、`Interaction/DropTarget.cs`、`Interaction/ReleaseGuard.cs`、`Interaction/DragDetector.cs`、`Diagnostics/Evidence.cs`、`Formats/FormatCatalog.cs`、`Formats/CodecRegistry.cs`、`tools/dragsim/Program.cs`），判斷「把該行為弄壞，這條會不會紅」。純唯讀稽核：未編輯、未建置、未執行套件、未終止行程。

## 判準

鐵律：**一條永遠不可能失敗的檢查，比沒有檢查更糟，因為它讓人以為有東西被驗過了。**

- **REAL**：把該行為弄壞，predicate 會變 false，**而且證據來自 App 之外**——runner 自己做的 Win32 探測、行程 exit code、檔案雜湊/mtime、另一個行程產生的事件。
- **WEAK**：只讀 App 自己寫的日誌行 / 自己設的旗標 / 自己回的 argv 值；或與同節區某條外部量測重複的自我報告；或通過條件太窄、功能部分缺席時仍會綠（空過）。
- **DEAD**：任何現實的產品故障都無法讓它變 false（恆真、拿自己剛寫的值再斷言一次、預期值與實際值同源、行程結束後的殘留探測、條件不可達）。

## 統計

| 分類 | m1a | m1b | 合計 |
|---|---:|---:|---:|
| REAL | 24 | 39 | **63** |
| WEAK | 28 | 30 | **58** |
| DEAD | 2 | 2 | **4** |
| 總計 | 54 | 71 | **125** |

> 註：腳本本身有重複編號（m1a 兩個 `S3.0`；m1b 的 `S6.11`、`S7.3`、`S7.4`、`S7.9`×3、`S7.16b`、`S8.8` 各出現兩次），下表以 `file:line` 區分，不省略任何一條。

---

## 逐條表格

### `tools/verify-m1a.ps1`（54 條）

| 檢查 id | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S0.1 | verify-m1a.ps1:184 | REAL | 整份套件在提權殼層下跑就會紅（`whoami /groups` 不再含 `S-1-16-8192`）。 |
| S0.2 | verify-m1a.ps1:190 | REAL | App 被提權（0x3000）或探測失敗（`IntegrityRid` 回 -1）就紅。 |
| S1.1 | verify-m1a.ps1:202 | WEAK | `--dump-monitors` 分支是無條件 `return 0`（Program.cs:608-620），所以只測得到「有沒有崩潰」。 |
| S1.2 | verify-m1a.ps1:204 | REAL | 監視器列舉壞掉、stdout 一行 `monitor#` 都沒有就紅。 |
| S1.3 | verify-m1a.ps1:216 | WEAK | `0 -eq 0` 時空過；存在的意義只是回頭驗 runner 自己的 regex。 |
| S1.4 | verify-m1a.ps1:217 | WEAK | 「只有一個 primary」是 Windows 保證的事實，App 只是轉述，除非它自己的 dump 格式壞了。 |
| S1.5 | verify-m1a.ps1:220 | WEAK | 只證明行程在 1.5 秒內沒退出；一個什麼都不做的行程也會綠。 |
| S1.6 | verify-m1a.ps1:223 | REAL | 少建一個螢幕的命中視窗就紅（只在 `$monitors` 也為 0 時才空過，而那樣 S1.2 已紅）。 |
| S1.7 | verify-m1a.ps1:236 | REAL | 閒置時任一命中視窗 `IsWindowVisible` 為 True 就紅。 |
| S1.8 | verify-m1a.ps1:237 | REAL | 由 OS exstyle 量 0x20 位；位元被設就紅（且 App 開機時自己會 throw）。 |
| S1.9 | verify-m1a.ps1:238 | REAL | 視窗矩形與 App 自報的螢幕範圍不符就紅（無視窗時空過，但 S1.6 已紅）。 |
| S1.10 | verify-m1a.ps1:241 | WEAK | 斷言自家日誌字串「`WS_EX_TRANSPARENT=not set(OK)`」，與 S1.8 的外部量測重複。 |
| S1.11 | verify-m1a.ps1:242 | WEAK | 斷言自家日誌的 `allHidden=True`（自己設的旗標），且該行的視窗數沒跟監視器數對帳。 |
| S2.1 | verify-m1a.ps1:254 | REAL | 收層後 `hit.IsVisible`（真的問 OS）仍為真就回 1 → 紅；但分不出「收乾淨」與「從沒升起」。 |
| S2.2 | verify-m1a.ps1:256 | WEAK | 只是數自家 `hit layer SHOWN` 行數；把 `ShowAll` 掏空成只印日誌仍然是 50 行。 |
| S2.3 | verify-m1a.ps1:257 | WEAK | 把 runner 自己傳的 50 與 App 自己算的 flag 再念一次。 |
| S2.4 | verify-m1a.ps1:258 | WEAK | 斷言自家計數器（在 `Dispose` 剛銷毀自己視窗後算出來的 `leftoverWindows=0`）。 |
| S2.5 | verify-m1a.ps1:260 | **DEAD** | 前一行已 `WaitForExit()`，行程的視窗已被 OS 銷毀，該 pid 不可能再擁有視窗 → 永遠 0。 |
| S3.0a | verify-m1a.ps1:271 | WEAK | 只註冊 FAIL 的失敗分支（App 活著時這條不存在），不是斷言。 |
| S3.0b | verify-m1a.ps1:274 | WEAK | 斷言自家日誌說自己關掉了前景檢查（自己設的旗標）。 |
| S3.1 | verify-m1a.ps1:286 | REAL | 注入的滑鼠按下＋位移若不再驅動狀態機，兩行轉移都不會出現 → 紅。 |
| S3.2 | verify-m1a.ps1:287 | WEAK | 自家日誌；同一件事下一條 S3.3 已用 OS 量過。 |
| S3.3 | verify-m1a.ps1:290 | REAL | 由 OS 探測到至少一個 `visible=True` 的命中視窗才算升起。 |
| S3.4 | verify-m1a.ps1:295 | WEAK | 斷言內部事件字串 `release requested: trigger released`，而「有沒有真的收層」這一節完全沒量。 |
| S3.5 | verify-m1a.ps1:296 | WEAK | 斷言自家 `hit layer HIDDEN (250 ms grace elapsed)` 這行；窗外效果（視窗真的消失）沒有探測。 |
| S3.6 | verify-m1a.ps1:312 | REAL | 第二次捕捉沒發生（SHOWN 行 < 2）就紅，是 S3.7/S3.10 的反空過前提。 |
| S3.7 | verify-m1a.ps1:313 | REAL | 注入的 mouse-up 沒被 hook 看到就不會有 insurance #1 這行。 |
| S3.8 | verify-m1a.ps1:325 | REAL | 熱鍵路徑壞掉就不會有 `*** KILL SWITCH`（作者刻意讓托盤暫停不共用這行）。 |
| S3.9 | verify-m1a.ps1:326 | WEAK | 自家旗標（`detector DISABLED`）的自我報告，真正的後果由 S3.10 承擔。 |
| S3.10 | verify-m1a.ps1:339 | REAL | 扣閂後再注入一輪卻又出現 SHOWN 行就紅；但若扣閂後那次注入完全沒送達事件，會空過。 |
| S3b.1 | verify-m1a.ps1:353 | WEAK | 只斷言 App 念出自己生效的逾時值；使用者 settings.json 一覆寫就紅，且沒有任何東西量「30 秒真的會收」。 |
| S3b.2 | verify-m1a.ps1:359 | WEAK | 期望值就是 runner 自己傳的 `$WatchdogSeconds * 1000`，只證明 App 有回音 argv。 |
| S3b.3 | verify-m1a.ps1:360 | REAL | 沒有真的升起就沒有 SHOWN 行，而 S3b.5 對「從沒升起」也是綠的，這行是唯一見證。 |
| S3b.4 | verify-m1a.ps1:363 | REAL | 逾時沒觸發就不會有 `HIDDEN (guard #3 watchdog)`；計時行為的唯一直證。 |
| S3b.5 | verify-m1a.ps1:365 | REAL | 唯一用 OS 探測「watchdog 之後真的不可見」的收層斷言（且要求視窗存在，防空過）——本套件正確儀器的範例。 |
| S4.1 | verify-m1a.ps1:387 | REAL | OS 探測不到可見的命中視窗就紅。 |
| S4.2 | verify-m1a.ps1:407 | REAL | 事件源自另一個行程（DragSim 的真 `DoDragDrop`）；`RegisterDragDrop` 沒做就永遠不會有這行。 |
| S4.3 | verify-m1a.ps1:408 | REAL | 由外部拖放送來的檔名清單長度解析錯誤（不是 1）就紅。 |
| S4.3b | verify-m1a.ps1:409 | WEAK | predicate 是「範例路徑出現在日誌的任何一行」，沒綁在 drop 那行；DragEnter 讀到路徑但 drop 從未落地也會綠。 |
| S4.4 | verify-m1a.ps1:410 | WEAK | 斷言自家 `hit layer HIDDEN (drop resolved)`；drop 後視窗是否真的消失沒問 OS（同樣的錢 S3b.5 花得起）。 |
| S4.5 | verify-m1a.ps1:411 | WEAK | 只是「drop 沒把行程弄死」。 |
| S4.6 | verify-m1a.ps1:415 | REAL | 拖放前後的 SHA256 不同就紅（檔案被動到）。 |
| S4.7 | verify-m1a.ps1:416 | REAL | mtime 變了（即使內容相同）就紅。 |
| S4a/S4b-alive | verify-m1a.ps1:433 | WEAK | S4a/S4b 的失敗分支，只在 App 已退出時印 FAIL。 |
| S4a.1 | verify-m1a.ps1:446 | WEAK | 「沒有升起」的負向斷言，沒有正向對照組：人什麼都不做、或 App 早就死了，一樣綠。 |
| S4a.2 | verify-m1a.ps1:447 | WEAK | 同上，「沒有攔到 drop」在什麼都沒發生時必然成立。 |
| S4b.1 | verify-m1a.ps1:452 | REAL | 需要三次真的 Explorer 拖曳；`OLE DragEnter` 只可能由 shell 的拖放迴圈觸發。 |
| S4b.2 | verify-m1a.ps1:453 | WEAK | 斷言自家旗標「wheel visible」，不是螢幕上的輪盤。 |
| S4b.3 | verify-m1a.ps1:454 | WEAK | 斷言 worker 自評 `ok=True`；這兩支 runner 從頭到尾沒檢查過任何輸出檔真的存在（該項在 m1d 另有覆蓋）。 |
| S4b.4 | verify-m1a.ps1:455 | WEAK | 收層的自家日誌行計數，沒有 OS 探測。 |
| S5.1 | verify-m1a.ps1:482 | REAL | 擊殺前 OS 必須看到可見的命中視窗。 |
| S5.2 | verify-m1a.ps1:487 | **DEAD** | 強制擊殺單一行程後，OS 必定回收它的視窗；除非世上還有第二個 Confern 實例，否則永遠 0。 |
| S5.3 | verify-m1a.ps1:490 | WEAK | dump 路徑無條件 `return 0`；只能區分「崩潰/exit 3」與「正常」，且完全沒看重新啟動後的行為。 |
| S5.4 | verify-m1a.ps1:493 | WEAK | 只證明新行程 1.5 秒內沒退出（沒驗它有建命中視窗）。 |

### `tools/verify-m1b.ps1`（71 條）

| 檢查 id | file:line | 分類 | 一句話理由 |
|---|---|---|---|
| S6.0 | verify-m1b.ps1:193 | WEAK | 失敗分支，不是斷言。 |
| S6.1 | verify-m1b.ps1:201 | REAL | 命中層視窗不存在就紅。 |
| S6.2 | verify-m1b.ps1:202 | REAL | 視覺層視窗數不是 1 就紅。 |
| S6.3 | verify-m1b.ps1:211 | REAL | 由 OS 讀 exstyle 的 0x20 位；少了就紅。 |
| S6.4 | verify-m1b.ps1:212 | REAL | 同上，`WS_EX_NOACTIVATE`。 |
| S6.5 | verify-m1b.ps1:213 | REAL | 同上，layered/topmost/toolwindow 三位元。 |
| S6.6 | verify-m1b.ps1:215 | REAL | OS 說視覺層可見才算顯示。 |
| S6.7 | verify-m1b.ps1:218 | REAL | OS 矩形必須等於 cursor−PadPx 與 304×349（App 只知道 cursor，這是交叉量測）。 |
| S6.8 | verify-m1b.ps1:238 | REAL | 輪盤內 9 點若有一點不落在命中層（＝穿透失效）就紅；`$hitHwnd` 為空時是 fail-closed。 |
| S6.9 | verify-m1b.ps1:251 | REAL | 輪盤外 4 點也必須落在命中層（命中層覆蓋整個螢幕），與 S6.8 互補。 |
| S6.10 | verify-m1b.ps1:255 | REAL | `GetForegroundWindow` 若變成任一 Confern 圖層就紅。 |
| S6.11 | verify-m1b.ps1:260 | WEAK | 缺行才 FAIL 的分支。 |
| S6.11 | verify-m1b.ps1:263 | WEAK | 數字由 App 自計的碼錶產生，且只涵蓋 `presenter.Show`：視覺層建立、`BuildCatalog`（首次 WIC 載入）都不在內——`Show` 變 no-op 會得到 ~0 ms 而通過。 |
| S6.12 | verify-m1b.ps1:265 | WEAK | 自家日誌字串，與外部量測的 S6.3 重複。 |
| S6.13 | verify-m1b.ps1:270 | WEAK | 行程已被殺，OS 必定回收其視窗；`pid=0` 只可能抓到「另一個殘留實例」，而那時又是紅得莫名其妙。 |
| S7.1 | verify-m1b.ps1:289 | REAL | 幾何自測含獨立的硬編碼期望值（角度→扇區、翻邊座標、304×349），錯一項就 exit 5。 |
| S7.2 | verify-m1b.ps1:290 | WEAK | 與 S7.1 同一個判定的第二個頻道（同一份 FAIL 行寫進日誌）。 |
| S7.7 | verify-m1b.ps1:298 | WEAK | 只驗檔案存在且 > 8000 bytes；畫錯、畫空白、畫成純色都照過。 |
| S7.3 | verify-m1b.ps1:309 | WEAK | 失敗分支。 |
| S7.3 | verify-m1b.ps1:312 | REAL | 視覺層視窗數不是 1 就紅。 |
| S7.4 | verify-m1b.ps1:319 | REAL | OS 說可見才算顯示。 |
| S7.5 | verify-m1b.ps1:320 | REAL | 與 S6.7 同族的交叉幾何量測。 |
| S7.6 | verify-m1b.ps1:321 | REAL | 同上（bitmap 尺寸）。 |
| S7.4 | verify-m1b.ps1:322 | WEAK | 失敗分支（正則不匹配時的替補 FAIL）。 |
| S7.8 | verify-m1b.ps1:324 | WEAK | 自家日誌 `wheel shown cursor=`。 |
| S7.9 | verify-m1b.ps1:338 | WEAK | 失敗分支。 |
| S7.9 | verify-m1b.ps1:343 | REAL | 未翻邊的原點是 `edgeX−24`，不可能 < `edgeX−124`，所以這條真的在量翻邊。 |
| S7.10 | verify-m1b.ps1:344 | REAL | 翻邊後原點＋寬度必須落在工作區內。 |
| S7.9 | verify-m1b.ps1:345 | WEAK | 失敗分支。 |
| S7.11 | verify-m1b.ps1:354 | REAL | 內部電池的任何一項不過就 exit 5；但其中視覺層那兩項讀的是自記旗標，繼承 S7.20 的盲點。 |
| S7.12 | verify-m1b.ps1:355 | WEAK | 與 S7.11 同一判定的第二個頻道。 |
| S7.13 | verify-m1b.ps1:356 | WEAK | 把 runner 傳的 3 再念一次，外加同一個 `failed=0`（exit code 已表達）。 |
| S7.14 | verify-m1b.ps1:364 | WEAK | `--wheel-payload` 分支結尾是無條件 `return 0;`（Program.cs:892），所以只偵測得到崩潰，偵測不到判決錯誤。 |
| S7.15 | verify-m1b.ps1:365 | REAL | 副檔名→格式對照表若壞掉，`source=JPG` 就不會出現（唯一見證是日誌）。 |
| S7.16 | verify-m1b.ps1:367 | REAL | 來源格式混進扇區清單（`targets=[…JPG…]`）就紅；弱點是隻認 "JPG" 這個拼法。 |
| S7.16b | verify-m1b.ps1:371 | WEAK | 取不到 shown 時的失敗分支。 |
| S7.16b | verify-m1b.ps1:373 | REAL | 兩個**不同行程**（`--codecs` 與 `--wheel-payload`）的集合必須一致，是真跨儀器比對。 |
| S7.17 | verify-m1b.ps1:375 | REAL | 輸出檔名由來源主檔名＋目標副檔名推導，推導壞掉就紅。 |
| S7.18 | verify-m1b.ps1:382 | REAL | 期望值由受測清單推導（不寫死），選取沒前進就紅。 |
| S7.19 | verify-m1b.ps1:385 | REAL | 分類判決（Ready）錯了就紅；注意它其實只驗了副檔名對照表（`FormatOfPath` 純看副檔名）。 |
| S7.20 | verify-m1b.ps1:386 | WEAK | `wheelVisibleAfter` 來自 `presenter.IsVisible` → `VisualLayer._visible`，那是**自己設的 bool**，不是問 OS；輪盤留在使用者畫面上也照樣 `False`。 |
| S7.21 | verify-m1b.ps1:389 | WEAK | 延遲數字由受測碼自己計時、量測區間由受測碼自己選；`sw` 提前停或 Show 變 no-op 都會得到 ~0 ms 而通過。 |
| S7.22 | verify-m1b.ps1:394 | WEAK | 同 S7.14：該分支無條件回 0。 |
| S7.23 | verify-m1b.ps1:395 | REAL | 同類別批次被判成非 Ready 就紅。 |
| S7.24 | verify-m1b.ps1:396 | REAL | 多檔預覽若印單一檔名（而非 `2 個檔案`）就紅。 |
| S7.25 | verify-m1b.ps1:400 | REAL | 混類別沒被判 Mixed 就紅。 |
| S7.26 | verify-m1b.ps1:401 | WEAK | `-not (Test-Log 'notice=-')` 太鬆：任何 notice（含另一種失敗訊息）都算過。 |
| S7.27 | verify-m1b.ps1:402 | REAL | `targets=0` 不成立就紅（但與「沒有任何可轉換目標」那條路徑印的字串共用）。 |
| S7.28 | verify-m1b.ps1:403 | WEAK | `wheelVisibleAfter=True` 是自記旗標。 |
| S7.29 | verify-m1b.ps1:407 | REAL | 單一不支援檔沒被判 Unsupported 就紅。 |
| S7.30 | verify-m1b.ps1:408 | WEAK | 同 S7.28。 |
| S7.31 | verify-m1b.ps1:417 | REAL | hover 自測的每一扇區斷言失敗就 exit 6。 |
| S7.32 | verify-m1b.ps1:418 | WEAK | 同 S7.31 判定的第二個頻道。 |
| S7.33 | verify-m1b.ps1:429 | **DEAD** | `$hits`（`hover sector` 行數）等於 `$expected`（從同一份日誌中同一份扇區清單算出的元素數），而 App 就是對同一份清單每項印一行——恆等，與 hover 對不對無關。 |
| S7.34 | verify-m1b.ps1:430 | WEAK | predicate 是「文字出現」，因此**失敗時印的 `FAIL centre disc does not move the selection` 也滿足它**（只有 S7.32 會發現）。 |
| S7.35 | verify-m1b.ps1:431 | WEAK | 同 S7.34（同一個句型陷阱）。 |
| S7.36 | verify-m1b.ps1:435 | WEAK | 同 S6.13：擊殺後 OS 必定回收，`pid=0` 只可能誤傷別人的實例。 |
| S8.0 | verify-m1b.ps1:471 | WEAK | 失敗分支。 |
| S8.1 | verify-m1b.ps1:487 | REAL | 需要真人真拖；合成輸入做不出 `OLE DragEnter`。 |
| S8.2 | verify-m1b.ps1:489 | **DEAD** | `*** INJECTED` 只在 `--wheel-payload` 分支寫出（Program.cs:833），而 S8 從不傳該旗標 → 這行永遠不存在，`-eq 0` 恆真。 |
| S8.3 | verify-m1b.ps1:490 | REAL | 真拖曳送來的清單若不等於 1 個檔就紅。 |
| S8.4 | verify-m1b.ps1:491 | REAL | 被拖的那個路徑只可能經由 payload 進入行程。 |
| S8.5 | verify-m1b.ps1:492 | REAL | 真 PNG 被誤判（或用了佔位符）就紅。 |
| S8.6 | verify-m1b.ps1:495 | REAL | 唯一替 `-notmatch` 加了正向前提（必須真的出現過來自 OLE payload 的輪盤）的檢查。 |
| S8.7 | verify-m1b.ps1:498 | REAL | 預覽沒有從真實檔名推導就紅。 |
| S8.8 | verify-m1b.ps1:502 | REAL | 兩行日誌的時戳差 > 100 ms 就紅（解析度受限於 `DateTime.Now` ~15 ms 的刻度）。 |
| S8.8 | verify-m1b.ps1:504 | WEAK | 量不到時的失敗分支。 |
| S8.9 | verify-m1b.ps1:507 | REAL | `wheel hover selection` 只在選取**真的改變**時寫（`SelectAt` 提早 return），人沒 hover 到扇區就紅。 |
| S8.10 | verify-m1b.ps1:508 | WEAK | 兩行自家日誌（視覺層收層在整個套件裡只有這個頻道，沒有任何 OS 探測）。 |
| S8.11 | verify-m1b.ps1:512 | REAL | 拖放後檔案必須逐位元相同。 |
| S8.12 | verify-m1b.ps1:518 | WEAK | 同 S6.13。 |

---

## 4 條 DEAD（違反鐵律：不可能失敗）

| id | file:line | predicate | 為什麼不可能紅 |
|---|---|---|---|
| S2.5 | verify-m1a.ps1:260 | `Check "S2.5 no hit window survives the process" ($left.Count -eq 0)` | 上一行 `$run.Process.WaitForExit()` 已確定行程結束；行程一結束，OS 就回收它擁有的一切頂層視窗，`EnumWindows` 不可能再列出該 pid 的 `ConfernHitLayer`。這是 S2 節區**唯一**的外部儀器——它死了，整個 S2 的證據就只剩 App 的自述。 |
| S5.2 | verify-m1a.ps1:487 | `Check "S5.2 no hit window survives the kill" ($left.Count -eq 0)` | `Stop-Process -Force` 之後，被殺的單一行程不可能再擁有視窗（A13 保險 #4 正是靠這個 OS 保證實作）。除非世上還有第二個 Confern 實例，否則恆 0；而 S5 開頭已 `Stop-AllSidecar`。 |
| S8.2 | verify-m1b.ps1:489 | `Check "S8.2 no injected-payload marker in this log" ((Count-Log $lines '\*\*\* INJECTED') -eq 0)` | `*** INJECTED PAYLOAD` 唯一的產生處是 `Program.cs:833`，包在 `if (opt.WheelPayload != null)` 內；`-Section S8` 從不傳 `--wheel-payload`，所以該字串永不存在，`-eq 0` 恆真。想防的假通過（注入路徑污染 S8 日誌）在此節區不可達。 |
| S7.33 | verify-m1b.ps1:429 | `Check "S7.33 every offered sector is individually reachable by hover (saw $hits of $expected)" (($expected -gt 0) -and ($hits -eq $expected))` | `$expected` 是從日誌中**第一條** `targets=[…]` 行（`presenter.Show` 印出的同一份扇區清單）算出的元素數；`$hits` 是 `hover sector` 行數，而 App（`WheelHoverSelfTest`）就是對同一份清單每項印一行。兩者同源 → 恆等，與 hover 映射對不對無關；檢查名稱宣稱的「每個扇區都可達」實際上住在 App 自己的 `Report(..., got == i)` 與 exit code（S7.31/S7.32）裡。 |

## 最嚴重盲點：視覺層收層從未被 OS 觀測

- `VisualLayer.IsVisible => _visible;`（`Visual/VisualLayer.cs:38`）——**一個自己設的 bool**。相對地，`HitLayer.IsVisible`（`Interaction/HitLayer.cs:52`）逐窗呼叫 `Native.IsWindowVisible`，類別註解還明寫「以**作業系統的真實狀態**判斷，而非自記旗標——收層斷言必須問系統」。同一份程式碼裡兩套標準。
- 兩個 runner 中，沒有任何一條檢查用 `IsWindowVisible` 驗證視覺層「收起來」。所有「輪盤消失了」的斷言都走這個自記旗標或自家日誌行：`S7.20`（:386 `wheelVisibleAfter=False`）、`S7.28`/`S7.30`（:403/:408 `wheelVisibleAfter=True`）、`S7.11`/`S7.13`（內部電池的 `presenter.IsVisible`）、`S4b.2`（:453）、`S8.10`（:508，讀 `visual layer HIDDEN` 日誌）。
- 反過來，「顯示」在兩處被外部量測（`S6.6`、`S7.4` 用 `IsWindowVisible`），所以這是單向的盲：**看得到它出現，看不到它離開**。
- 後果：輪盤若因任何原因留在使用者桌面上（本產品最災難的失效模式——一個沒人能收掉的覆蓋層），M1b 的 S6/S7/S8 可以全綠。收尾那三條（`S6.13`:270、`S7.36`:435、`S8.12`:518）用的是「殺掉行程後不該有視窗」，而殺行程必定回收視窗，所以它們接不住。`S7.7`（:298）只看 PNG 存在與大小，也不涉及視窗狀態。

---

## 五個最小破壞實驗（假想，未執行；此稽核全程唯讀）

> 每一項都只改被測程式碼的一小段，並標出「哪些節區會紅」以免誇大。

### 實驗 1 — `S2.5`（DEAD）：S2 整節可以 100% 綠而一次升降都沒發生
- **破壞**：把 `HitLayer.ShowAll()`（`HitLayer.cs:145-156`）掏成只寫 `_evidence.Line($"hit layer SHOWN …")`，不呼叫 `Native.SetWindowPos`。
- **預測**：`S2.1`=PASS（`hit.IsVisible` 為 false → `CycleTest` 回 0）、`S2.2`=PASS（仍印 50 行）、`S2.3`=PASS（`allHidden=True`）、`S2.4`=PASS（`leftoverWindows=0`）、`S2.5`=PASS。**S2 自稱的「50 次升降循環」全綠，實際上 0 次**。
- **會不會被別節區抓到**：會——`S3.3`、`S4.1`、`S5.1` 用 OS 探測可見性。但 S2 自己的 5 條檢查（含它唯一的外部儀器 S2.5）全數失效。

### 實驗 2 — `S7.20`（WEAK）：產品最災難的失效模式，兩個套件都看不到
- **破壞**：把 `VisualLayer` 的兩處 `Native.ShowWindow(_hwnd, Native.SW_HIDE)`（`VisualLayer.cs:156` 與 `:182`）改成 no-op，**保留** `_visible = false`。
- **預測**：輪盤永遠留在使用者桌面上，然而 `S7.20`=PASS、`S7.11`/`S7.13`=PASS（內部電池問的是 `presenter.IsVisible` → 同一個自記旗標，`PumpUntil(() => !presenter.IsVisible)` 立刻成立）、`S6.13`/`S7.36`/`S8.12`=PASS（行程被殺，視窗自然消失）、`S8.10`=PASS（`visual layer HIDDEN` 那行照樣寫）。
- **會不會被別節區抓到**：不會。兩個 runner 沒有任何一條用 `IsWindowVisible` 驗視覺層收層。

### 實驗 3 — `S3.5`/`S4.4`（WEAK）：保險 #1/#2 的「真的收層」只有日誌見證
- **破壞**：把 `HitLayer.StaticWndProc` 的 `WM_APP_FORCE_HIDE` 分支（`HitLayer.cs:194`）改成只寫那行日誌、不呼叫 `ShowWindow(SW_HIDE)`。
- **預測**：`S3.5`（250 ms 寬限後收層）=PASS、`S4.4`（drop 後立即收層）=PASS、`S4b.4`=PASS、`S8.10`=PASS——螢幕上那層永遠不會消失。
- **會不會被別節區抓到**：只有 `S3b.5`（:365）會，而它在另一個節區、只覆蓋 watchdog 這條原因碼。這正是「同一種效果，一份用 OS 量、四份用日誌猜」的差別。

### 實驗 4 — `S8.2`（DEAD）：斷言一個不存在的東西不存在
- **破壞**：刪掉整個 `--wheel-payload` 分支（`Program.cs:830-893`，含 `*** INJECTED PAYLOAD (diagnostic; NOT a real DragEnter)` 那行）。
- **預測**：`S8.2`=**PASS**——它檢查的字串唯一產生處就是被刪掉的那段，把產生它的東西移除，這條檢查反而更穩。

### 實驗 5 — `S6.11`（WEAK）：門檻存在，但量的區間由受測碼自己選
- **破壞**：在 `WheelDemo`（`Program.cs:950`）的 `var sw = Stopwatch.StartNew();` **之前**插入 `Thread.Sleep(500);`（或把 sleep 放進 `BuildCatalog`／`VisualLayer` 建構子——即首次顯示真正會付的成本）。
- **預測**：`S6.11`=PASS，日誌照樣印出 `in 4x ms (budget 100 ms)`，而使用者實際要等 600 ms 才看到輪盤；`S6.3`-`S6.7` 也全綠（它們在 1.8 秒後才量）。
- **對照組**：`S8.8`（:502）量的是 runner 自己讀到的兩行日誌時戳差，同一種破壞會讓它變紅 → m1b 內部對「延遲」有兩把尺，只有 S8 那把咬得住。

### 備選 — `S7.33`（DEAD）：同一份清單跟自己比對
- **破壞**：把 `WheelPresenter.SelectAt` 的 `double dy = screenY - cy;`（`WheelPresenter.cs:174`）取負號，讓每個扇區的 hover 映射都錯。
- **預測**：日誌滿是 `FAIL  hover sector i …`、App exit 6 使 `S7.31`/`S7.32` 變紅，但 `S7.33` 仍會印 `PASS … (saw 5 of 5)`——因為它數的行數與它讀的清單同源。

---

## 結論（誠實版）

這兩份套件**不是純劇場，但也遠不是它們自稱的驗收**：125 條裡約一半（63 條）是真檢查，而且真檢查落在最要緊的地方——命中層的可見性、視窗幾何、exstyle 位元、`WindowFromPoint` 穿透、權杖完整性、來源檔雜湊，以及三個內部自測電池的 exit code（那三組各自帶獨立期望值，壞掉真的會紅）；但另一半（58 條 WEAK）本質是「讀 App 自己寫的日誌」，它們大多不是空的（功能刪掉那行日誌也會消失），證明的是「App 這樣說」，不是「使用者得到什麼」，其中最嚴重的一群是**自記旗標**——`VisualLayer.IsVisible` 只是個 bool（而 `HitLayer.IsVisible` 老老實實問 OS，同一份程式碼裡兩種標準），於是「輪盤收起來了」在整個 M1b 裡從未被外部證實，而那正是本產品最災難的失效模式（卡在使用者畫面上的覆蓋層）；真正違反鐵律的 **DEAD 有四條**：`S2.5`、`S5.2`（行程已死，視窗不可能殘留）以及 `S8.2`、`S7.33`（前者斷言不存在的字串不存在，後者拿同一份清單跟自己比對），其中 `S2.5` 是 S2 節區唯一的外部儀器——這使「50 次升降循環」整節的證據百分之百來自 App 的自我敘述；更值得追究的是一種系統性模式：`--dump-monitors`、`--wheel-payload` 這些診斷路徑的結尾都是無條件 `return 0`，於是 `S1.1`、`S5.3`、`S7.14`、`S7.22` 四條「exit 0」檢查實際上只等於「沒有崩潰」，卻看起來像在驗功能；再加上 S6 的幾何檢查在正則不匹配時整段不被註冊（沒有 else，總結也只印 N/N 而不比對預期條數），一次綠燈可能少了五條檢查而沒人知道。公平地說，檔案裡有清楚的自省痕跡（S4.2 那段「曾經永遠紅」的懺悔註解、S3b.5 正確使用 OS 探測、S8.6 主動加正向前提避免 `-notmatch` 空過、`Test-LogCase` 的 case-sensitive 修正），代表作者知道這條鐵律——只是它被套用在少數幾條上，而沒有被當成整個 harness 的設計原則。修法建議：把「收層」斷言一律改成 runner 端 `IsWindowVisible` 探測（S3/S4/S6/S7/S8 各一處即可補上最大盲點）、斷言檢查註冊數（避免靜默跳過）、把 `--dump-monitors`/`--wheel-payload` 改為依實際結果回非零、並刪除或改寫 `S2.5`、`S5.2`、`S8.2`、`S7.33`。
