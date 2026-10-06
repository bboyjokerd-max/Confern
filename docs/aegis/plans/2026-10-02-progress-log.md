# 最新進度（2026-10-02 起，每次收工更新）

> 這一篇是**給人看的儀表板**：現在做到哪、驗收數字是多少、下一步是什麼、哪些設定還沒生效。
> 權威來源仍是 repo（接手入口＝`docs/aegis/plans/2026-10-02-feature-handoff.md`），這一篇只是摘要。
> 更新方式：改完 repo → 跑 `tools\obsidian\sync.ps1`（這一篇會連同其他筆記一起鏡射過來）。

## 現在狀態（依接手順序）

| # | 工作 | 狀態 |
|---|------|------|
| ① | 失敗清單每行必須含中文原因（自動斷言） | ✅ **完成並驗收** |
| ② | S4 真實拖曳結案 | ✅ **自動面結案**（`verify-m1a.ps1` 現在會自己找到 `tools\dragsim\bin\Release\net8.0-windows\DragSim.exe`（先前每一輪都回報「需要 -DragSim」然後被當成未定項放過——工具從 9/30 就躺在 repo 裡，缺的只是沒有人把路徑傳進去）。**人工手勢（S4m）仍待你跑**——它額外覆蓋「Explorer 當來源時的觸發鍵武裝」，那是合成輸入做不到的。指令見下方 |
| ③ | 設定頁（M1f 本體） | ✅ **完成**：九列設定、zh-Hant／en、存檔即生效、診斷記錄語意已實作 |
| ④ | 托盤圖示＋開機自動啟動 | ✅ **完成並驗收**（我複核時修掉一個安全回歸，見下）；「用真滑鼠點托盤」仍未有證據 |
| ⑤ | 點擊展開失敗清單 | 🟡 **D3＋D4 完成並驗收**（面板圓角統一 `min(高度/2,18)`、極長檔名螢幕內上限＋僅檔名中間省略；`m1h` **51/51**，提交 `324e361`）；✅ **完成並驗收**（D1 專屬可點視窗、D2 游標停留不淡出；`m1h` 62/62，提交 `cb6ee5f`）。**過程中抓到一個真的產品 bug**：`WM_LBUTTONUP` 的 lParam 是**工作區座標**，第一版拿它去比 `GetWindowRect` 的螢幕矩形 ⇒ 藥丸只要不在螢幕原點附近，**每一個點擊都會被靜默丟掉**；它自己的無畫面自測用 (0,0) 當錨點剛好蓋住，是「真實合成點擊」在 (1280,516) 照出來的 |

## 驗收數字（2026-10-02 收工時，exe 時間戳 `18:50:03`）

| 套件 | 範圍 | 數字 | 註冊數斷言 |
|------|------|------|-----------|
| `tools/verify-m1a.ps1` | 生命週期、接收層、OLE 拖放（**含 S4 合成拖放**） | **48/48** | 逐節區相等 |
| `tools/verify-m1b.ps1` | 輪盤渲染與互動（**必須加 `-Section All`**） | **52/52** | 逐節區下限 |
| `tools/verify-m1c.ps1` | 格式與編解碼 | **20/20** | 相等 |
| `tools/verify-m1d.ps1` | 轉檔契約、Worker、進度 | **54/54** | 相等 |
| `tools/verify-m1f.ps1` | 設定檔、設定頁本體、診斷記錄 | **34/34** | 相等 |
| `tools/verify-m1g.ps1` | 托盤圖示、開機自動啟動、緊急停用不可被托盤解除 | **25/25** | 相等 |
| `tools/verify-m1h.ps1` | 失敗清單：資料與渲染、寬度上限、中略、圓角、可點視窗與停留 | **62/62** | 下限 |
| **合計** | | **295 條，0 fail、0 inconclusive** | |

> 每一支都要 `-Exe <Confern.exe 的完整路徑>`。最後一次全套重跑在 exe 時間戳 `18:50:03`，
> 並且 `src\` 底下沒有任何檔案比它新、跑完沒有殘留 Confern 行程。
> **`Confern.exe` 的 sha256 不是 build 的身分證**（它是 .NET apphost，程式在 `Confern.dll`）——
> 有效的儀器是時間戳加上「沒有來源檔比 exe 新」。
> `verify-m1b.ps1` 不加 `-Section All` 只跑 13 項，「13/13 全綠」是誤判——這個坑踩過一次。

**每一支現在都會在最後斷言「已註冊的檢查數」**，所以任何一段檢查在未來重構裡靜默消失，都會讓該支變紅而不是印出 N/N。這條斷言當場證明過自己：把 `m1d` 的一條 `Check` 註解掉，同一支執行從「印 53/53、exit 0」變成「`checks=53 … FATAL: must register exactly 54`、exit 1」。
## 托盤與開機自動啟動（待辦 ④）做了什麼

- **托盤圖示**：用 Win32 `Shell_NotifyIcon` 實作，兩個尺寸的圖示都用 GDI+ 依凍結的調色盤**當場畫**——沒有素材檔、沒有新增套件。選單三項＝開啟設定／暫停轉換／結束；**雙擊＝開啟設定頁**（設定頁從此不必再靠旗標就進得去）。
- **開機自動啟動**：`HKCU\...\Run` 的唯一擁有者 `Autostart`。**只有常駐程式會套用它**；`--settings-set` 是純設定檔編輯器，永遠不碰 Run 機碼。
- **那個發現值得記下來**：規格的預設是「開機自動啟動＝開」，第一版接上去之後，**既有的驗收套件開始在你真正的 Run 機碼留下一筆 `Confern`**（指向 `bin\Release`）。這不是推論——是裝了登錄檔監看、逐秒抓出來是 `m1f --settings-ui-live` 與 `m1b --wheel-payload` 寫的。修法＝`--no-autostart`（三態：寫入／移除／完全不要碰），四支既有套件都帶上；殘留那筆已刪除，現在 m1g 會斷言「跑完一整輪，真實 Run 機碼與跑之前完全相同」。
- **我複核時修掉的安全回歸**（第一版真的出貨會出事）：托盤的「暫停」呼叫的是保險 #5 的 `OnKillSwitch()`，所以①日誌會印 `*** KILL SWITCH ***`（而 `m1a` 的 S3.8 正是在斷言那一行 ⇒ 那條斷言可以被「按一下托盤暫停」滿足）、②「繼續」無條件呼叫 `Resume()` ⇒ **按一下托盤就能解除緊急停用，與規格 §4.2.2「直到重啟」矛盾**、③托盤照「使用者的意圖」翻自己的提示文字 ⇒ 復原被拒絕時它會顯示 `running` 而捕捉其實是停用的。
  修法＝**狀態共用、復原的權力不共用**：托盤的暫停走新的 `Pause(why)`（可復原、不印 KILL SWITCH、不扣閂），`OnKillSwitch()` 才會扣下只進不退的閂，`Resume()` 遇到閂就**大聲拒絕**；托盤改成在回呼跑完後**去問真正的事實**（`try/finally`）。新增 `S15.24`／`S15.25` 兩條斷言，**它們在修改前的程式碼上會失敗**（我確認過失敗的樣子）。

## 還沒驗到的地方（誠實清單）

- **沒有人真的用滑鼠點過托盤選單**：自測走的是「與真實點擊同一條分派」，但「真的點下去」沒有獨立證據。（週末期間你不在鍵盤前，合成輸入已解禁，但一律序列化——不與任何其他工作並行。）
- **`TaskbarCreated` 重註冊**（explorer 重啟後圖示要回來）需要重啟 explorer 才驗得到，目前只有程式碼審查級的保證。
- `S15.11` 證明的是「`NIM_DELETE` 被接受」，不是「shell 真的放掉了」。
- **更正**：`S15.7` 先前被我記成「只斷言標籤是中文」的弱檢查，假故障稽核**實測**後改判為真檢查——它拿跑次留下的日誌執行那段 regex，三組標籤都匹配且 `nonAscii=True`。我的自我標註過度悲觀。
- `m1a` 的 S3.7、`m1b` 的 S6.8/S6.9 偶發不穩（依賴實際滑鼠位置與時序），重跑即通過。
- **`Confern.exe` 的 sha256 不是 build 的身分證**：它是 .NET apphost，位元組與託管程式碼無關（真正的程式在 `Confern.dll`），所以兩次不同內容的建置會得到同一個 exe 雜湊。有效的儀器是**時間戳**＋「`src\` 底下沒有比 exe 新的檔案」。我先前在提交訊息裡引用 exe 雜湊，那是錯的引用方式。\n- 我自己踩過一次：`S15.25` 第一版紅燈，查了日誌才發現是**我的斷言用錯了日誌格式**（真實格式是 `tray: paused -> tooltip '…'`），產品本身是對的。修的是斷言，不是產品。

## 假故障稽核（週末工作包 B）：哪些斷言其實永遠不會紅

三份唯讀稽核逐條讀完 **289 個檢查**，只問一句話：**把它保護的東西改壞，它真的會紅嗎？**

| 套件 | 檢查數 | 真（會紅） | 弱 | 永遠不會紅 |
|------|--------|-----------|-----|-----------|
| m1a ＋ m1b | 125 | 63 | 58 | 4 |
| m1c ＋ m1d | 74 | 42 | 25 | 7 |
| m1f ＋ m1g ＋ m1h | 90（91 次呼叫） | 57 | 32 | 1 |
| **合計** | **289** | **162（56%）** | **115** | **12** |

**12 條永遠不會紅，而它們全都貢獻了那些「全綠」。** 最該先修的四件事：

1. **`verify-m1d.ps1:266`（S12.15c）沒讀 fixture 的內容**：它數到「1」就是輸入檔自己。⇒ 在失敗
   分支裡**清空使用者的原檔**仍然會通過——規格 §5 的第一紅線（絕不危害來源檔）沒有被驗到。
2. **`VisualLayer.IsVisible` 是自記的 bool**（`HitLayer.IsVisible` 老實問 OS）⇒ 整個 m1b 沒有任何一條
   用 `IsWindowVisible` 驗「視覺層真的收起來」⇒ **輪盤卡在使用者畫面上時 S6／S7／S8 全綠**。
3. **自我評估式斷言**：`checks=(\d+) failed=0 ok=True` **接受 0 條**（m1g S15.13、m1h S16.44）。
4. **拿 0 當成功**：`S15.16` 的 `$leftBehind -eq 0`——`Enable()` 完全不寫登錄檔也會過。

修復清單（含每一項「怎麼證明修好了」）：`docs/aegis/work/2026-10-02-falsifiability/fix-plan.md`。
完整逐條表格：同目錄的三份 `*-checks.md`（290 列，含 file:line 與一句理由）。

> **兩份稽核回頭核對時改正了自己的彙總數字**（逐條判定都對、只有總計錯）。這比原始數字更值得記：
> 一份不會更正自己的稽核，其錯數字會被後人當成事實引用下去。

### 因此新增的兩條專案慣例

1. **每一支套件最後都要斷言「已註冊的檢查數 == 預期條數」。** 少了它，任何一條檢查都可以在未來的
   重構裡靜默消失（`verify-m1b.ps1` 的 S6.3–S6.7 就會整段不註冊），而總結依然印「全綠」。
2. **新檢查一律附加在最後，不得插入中間。** 這一輪就發生過一次：D3/D4 把 19 條新檢查插進
   `S16.24–S16.43`，把舊的 `S16.24–S16.30` 擠成 `S16.44–S16.50`，於是所有引用那些編號的文件
   **默默指到別的東西**。檢查 id 是被人引用一輩子的位址。
## 修復了什麼（週末工作包 F）

**最重要的一項是產品本身的修改**：`VisualLayer.IsVisible` 原本回傳的是圖層「自己記得的」bool，所以一片卡在桌面上的覆蓋層可以讓所有「有沒有收起來」的斷言保持全綠。它現在一律去問作業系統（`IsWindowVisible`），而那個欄位是被**刪掉**而不是被同步——一整家族弱檢查因此在源頭變成強檢查。

其餘（P0＋P1 全數關閉）：
- **來源檔神聖**：`m1d S12.15c` 原本只數檔案、從不讀內容，所以在失敗分支清空使用者的原檔也會通過。現在先記下來源檔的 SHA256＋mtime＋長度，跑完斷言三者不變，且要求失敗真的發生、原因真的進到日誌。
- **死檢查復活**：`m1a S2.5` 原本在行程已經結束後才看，永遠量不到東西；現在直接跑 50 次循環並每 15ms 取樣命中層的 HWND（實測 live=159、visible=80、最後存活期間可見=0）。
- **不再自說自話**：新增以 `PostMessageW` 強制隱藏後**去問視窗管理員**的檢查（`m1a` S3.11／S4.8、`m1b` S6.14／S7.37／S8.13）
- **自我評估式斷言**：`checks=(\d+) failed=0` 改成要求 `[1-9]\d*`——**0 條不算通過**。
- **註冊數斷言**：七支套件全部加上（見上）。
- **`S15.16` 的假綠**：舊條件是「跑完之後沒有殘留」＝什麼都沒寫也會過。現在用 .NET Registry API 即時取樣，要求真的看到那筆帶引號的絕對路徑。假故障證明：把它寫到 scratch 機碼時，舊條件是綠的。
- **`S15.24` 的敘事證據**：緊急停用的「拒絕」原本用一段偵測器形狀的日誌當證據。現在 `Resume()` 回傳是否接受、並維護單調的 `ResumeRefusals` 計數，托盤把它記進日誌。假故障證明：閂鎖拿掉後舊的那段日誌**仍然是 True**，只有新計數變成 0。

**沒有被證明到的部分（照實記錄）**：`m1b` 新增的 `S8.13` 已寫但**從未執行**（S8 需要真人做一次 OLE 拖曳）；P0-1 的來源檔三重身分只在綠燈路徑量過；P1-11 的 exit 3 分支在這台機器構造不出來；`m1b`／`m1h` 的註冊數是**下限**（抓漏抓不到多）；`S16.12` 是相對門檻，四帶同時等比例縮小仍會綠。
## 現在還沒生效的設定

| 設定 | 現況 |
|------|------|
| 開機自動啟動 | **已生效**（設定頁與托盤都會立即套用；只由常駐程式寫 Run 機碼） |
| 語言 | 只影響**設定頁自己**的語言；不影響日誌 |
| 緊急停用熱鍵 | 規格 §8 的值（`Ctrl+Alt+Shift+Q`）仍保留但**未實作**；它本來就是組合鍵 |
| 命名衝突 | 規格 MVP 固定值（自動加序號），刻意不開放 |

## 週末無人值守計畫（2026-10-02 晚間起，全做已核准）

使用者已核准全部四項並透過 Tailscale 遠端查看進度。以下是工作包與狀態；**每完成一片就 commit ＋ 更新本檔 ＋ 同步 Obsidian**。

| # | 工作包 | 狀態 |
|---|--------|------|
| A | 基線：八支套件全套回歸（建立「改動前」的對照） | ✅ **完成**：m1f 34、m1b 50、m1c 20、m1d 54、m1g 25、m1h 31、m1a 38＋S4 未定 |
| B | 假故障稽核 ×3（逐條斷言問「目標改壞時它會不會紅」，唯讀） | ✅ **完成**：289 條全部分類，逐條表格已進 repo |
| C | 待辦 ⑤ **D3＋D4**：面板圓角統一 `min(高度/2, 18px)`、極長檔名螢幕內上限＋僅檔名中間省略 | ✅ **完成並驗收**（`324e361`） |
| D | 待辦 ⑤ **D1＋D2**：藥丸改用專屬可點視窗（吃掉那一下點擊）、失敗藥丸游標停留時不淡出 | ✅ **完成並驗收**（`cb6ee5f`） |
| E | **DragSim**：讓 S4 可在無人時自動驗證（待辦 ② 軟體面結案） | ✅ **完成**：`0eadf41` 自動尋找 ＋ **實跑 46/46 全綠**（`docs/aegis/work/2026-10-02-s4-closure/`）。工具從 9/30 就在 repo 裡，缺的只是沒有人把路徑傳進去 |
| F | 套用 B 的稽核發現（修掉假綠／永不可能失敗的檢查，弱的誠實記錄） | ✅ **P0 5/5 ＋ P1 11/11 完成並驗收**（`685ccb5`）：12 條永遠不會紅的檢查已修到會紅、六次假故障每次先量到紅再還原成綠。P2（命名與清理）**明列為未做**——它是整潔度，不是證據品質 |
| G | 收尾：最終全套回歸、文件、Obsidian、完成通知 | 待辦 |

### D1 的設計（讀完程式碼後定案）

`Program.cs:673` 顯示**藥丸早就用自己的 `VisualLayer` 實例**，不與輪盤共用（輪盤是 `Program.cs:670`
那個）。所以 D1 是**局部改動**：

- 抽出一個只有 6 個成員的小介面（`Show`／`Hide`／`IsVisible`／`Frames`／`StartTick`／`TickRequested`），
  由現有 `VisualLayer`（原封不動）與新的 `PillWindow` 共同實作 ⇒ `ProgressPresenter` 只依賴介面。
- `PillWindow`＝頂層 `WS_POPUP` ＋ `WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`，**不加**
  `WS_EX_TRANSPARENT`（那正是目前穿透的原因）；`WM_LBUTTONUP` → `ToggleDetail()`、
  `WM_MOUSEACTIVATE` 回 `MA_NOACTIVATE`（不搶焦點）。
- **幾何仍然只有一份**：視窗位置＝`PillRect`，由 `ProgressPresenter` 決定，不另立第二份。
- 副作用是好的：有了真視窗，**D2 的「游標停在藥丸上」才第一次變成可觀測狀態**
  （穿透圖層收不到滑鼠事件，所以 D2 在舊架構下根本無法實作）。
- 展開時同一個視窗長大（一份幾何、一份 z-order），並在那裡做 D4 的螢幕內上限。

### 不做的事（週末期間的硬邊界）

不動系統設定（含電源）、不重啟 DSH、不安裝任何東西、不碰 `HKCU\...\Run`、不改凍結的視覺設計
（`WheelDesign` 的值、光暈、扇形圓角）、不做任何需要使用者判斷外觀的決定。
合成輸入（滑鼠／鍵盤）只在確定使用者不在鍵盤前時執行。

## 失敗清單（待辦 ⑤）：四個決定你都核准了，D3＋D4 已做完

已完成的是一半，而且是**不需要你決定**的那一半：資料（檔名＋原因，單一擁有者）與渲染
（`ComposeDetail`、寬度依文字量測、上限 8 列＋`…還有 N 筆`、沿用凍結的錯誤紅與字型），
外加無畫面 dump 與新套件 `m1h` 31/31。**點擊尚未接上任何地方**——因為接法有兩種，代價不同。

| # | 決定 | 選項 | 我的建議 |
|---|------|------|----------|
| D1 | 那一下點擊怎麼收到 | **A**：藥丸改用專屬可點視窗（吃掉點擊，不會順便點到桌面圖示）／**B**：走現成的全域滑鼠鉤子（不能攔阻 ⇒ 點藥丸會同時點到下面的東西） | **A**（B 會動到「零侵入」這條核心不變式） |
| D2 | 有失敗時藥丸停留 | 游標停在藥丸上就不淡出（離開後照原本 5 秒）／維持一律 5 秒 | **前者**（5 秒內要點到一顆小藥丸，實際上很難點到）。**2026-10-05 使用者真人實測後再改：一律 2 秒**，見文末修訂 |
| D3 | 面板圓角 | 統一成 `min(高度/2, 18px)`（1 列仍是膠囊、多列是圓角矩形）／維持現狀（8 列時只是圓角矩形、與 1 列不一致） | **統一** |
| D4 | 極長檔名的上限 | 面板寬度上限＝螢幕寬−邊距，**只**把檔名中間省略（原因永遠完整）／維持現狀（會撐出螢幕） | **加上限** |

成功路徑（`✓ N` 兩秒淡出）**四個決定都不影響**。

面板實際長相（我請視覺模型看過，也自己核對過：無缺字、無裁切、檔名與原因都完整）：

- `docs\aegis\work\2026-10-02-failure-list\panel-selftest-many.png`（8 列＋`…還有 1 筆`，就是 D3 講的那張）
- `docs\aegis\work\2026-10-02-failure-list\panel-selftest-short.png`（1 列，膠囊）
- `docs\aegis\work\2026-10-02-failure-list\panel-selftest-pill-long.png`（藥丸被長訊息撐寬，§7.1 的行為）

> 視覺模型另外說「這顆紅太刺眼、與冷鋼／薄荷定位不符」——**我不採納**：那是凍結的
> `WheelDesign.NoticeFill`，全產品錯誤色只有這一個來源，一致性是刻意設計。不知情的審美意見不是證據。

## 你現在可以做的事

**看托盤與設定頁**（正常啟動就會出現托盤圖示，雙擊圖示即開設定頁；關掉程式圖示就消失）：

```powershell
cd C:\Users\User\Desktop\專案\tangerine-win
.\src\Sidecar\bin\Release\net8.0-windows\Confern.exe --settings-ui
```

畫面存檔（含中英兩版）在 repo：`docs\aegis\work\2026-10-02-settings-page\`。

**人工驗證真實拖曳**（需要你自己用滑鼠；S4b 會真的轉檔，請用可丟棄的資料夾）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1a.ps1 `
  -Exe src\Sidecar\bin\Release\net8.0-windows\Confern.exe `
  -Section S4m -ArchiveDir docs\aegis\work\2026-10-02-s4-closure
```

- S4a：**不按**觸發鍵的拖曳 3 次 → 應該完全沒有任何反應
- S4b：**按住空白鍵**拖曳 3 次 → 輪盤出現 → 移到目標格式 → **放開滑鼠** → 真的轉檔

**重驗 m1a 的緊急停用路徑**：✅ **已完成**（趁你離開的空檔跑，38/38）——這是我那次修改最後一個沒有真實證據的部分，現在補上了。

跑之前先跟我說一聲：建置會殺掉行程並鎖住 exe，我不能同時在建置。

---

## 2026-10-05 修訂：藥丸停留時間統一 2 秒（使用者決定）

**來源**：使用者今天自己做真人拖曳驗收與失敗清單示範後反映「藥丸有點久才消失」。

**當場量到的事實**（`%TEMP%\confern-stuck-pill.log`，不是猜的）：

| 情況 | 量到的值 |
|---|---|
| 失敗藥丸 | 游標離開後 5 秒 ＋ 淡出 0.4 秒 ＝ **5.42 秒**（`hiding after 5422 ms, hold=5000`） |
| 按開清單之後 | 從按下那一下重新計時，總計 **6.79 秒** |
| 成功藥丸（13:53 那次） | **2.41 秒**（`hiding after 2407 ms, hold=2000`） |

**我先前兩個假設都被日誌推翻**（記下來，因為這是「儀器優先於推論」的實例）：

1. 「游標離開事件沒送達」→ 錯：`WM_MOUSELEAVE` 每次都到（hover true/false 各 10 次）。
2. 「失敗數累積，所以後面的成功藥丸也用 5 秒」→ 錯：那次重現的**兩次拖曳都是真的失敗**
   （`worker result: ok=False error=Unsupported` ×2），而 `worker summary` 每次都從 `✓ 0 / ✗ 1` 重新起算。

**決定**：使用者說「2 秒就好」⇒ 成功、失敗、展開後離開，一律 2 秒。

**這是改回規格，不是改規格**：規格 §4.4 只寫「完成後 2 秒淡出」；5 秒是實作自己加的
（`FailureHoldMs`），handoff 當年也記成「程式對有失敗的情況已經留 5 秒」。

**程式改動**：`ProgressPresenter.cs` 刪掉 `FailureHoldMs`，`HoldFor()` 只剩
`_hovered ? -1 : FadeMs` 一條規則。刻意**刪掉常數而不是留著**：留一個永遠等於 `FadeMs` 的常數，
就是稽核在抓的那種「永遠不會被選到的死設定」。D2 的 hover 規則完全不動。

**假故障演練**（每一條都是當場量到的紅，不是文字描述）：

| 演的故障 | 量到的結果 |
|---|---|
| 改碼但斷言仍釘舊值 `hold=5000` | **FAIL S12.15d**，`checks=54 pass=53 fail=1`，exit=1 |
| 斷言上界改成 `1999`（不可能滿足） | **FAIL S12.15d**，`checks=54 pass=53 fail=1`，exit=1 |
| 還原後 | **PASS S12.15d**，`elapsed=2395 ms`，`54/54`，exit=0；還原後檔案 hash 與原檔相同 |

**回歸**（同一個 exe，時間戳 `2026-10-05 14:18:43`，序列執行、無殘留行程）：

| 套件 | 結果 |
|---|---|
| m1d | **54/54**（S12.15c 的來源不變式全程綠） |
| m1h | **62/62**（它的藥丸壽命斷言是**相對的**——讀程式自報的 `holdMs`——所以自動吸收這次改值） |
| m1a | **48/48**（零侵入與生命週期不受影響） |

**順手補強**：稽核早就把 `S12.15d` 記成 WEAK（「抓了 `elapsed` 卻從不比」）。這次改成同時比對
`elapsed`（2000..3200 ms）並把量到的值印在證據欄 ⇒ 升級成 REAL；稽核表已同步記回。

**自己踩到的地雷（值得留著）**：我第一版把中文註解加進 `verify-m1d.ps1`，而那個檔案開頭就寫著
「keep this file ASCII-only：BOM-less 的 .ps1 裡有 CJK 會讓 PS 5.1 誤判下一行」。CP950 的雙位元組
把換行吃掉，`Check 'S12.15d'` 被併進註解、**根本沒執行**，於是出現 `checks=53 expected=54`。
是週末加的那條「報名數斷言」抓到的——**它今天證明了自己的價值**。已改回 ASCII 註解（非 ASCII 0 個）。

---

## 2026-10-05（下午）：M3 開工 ＋ 歸檔整理

使用者對週末成果的評價是「我本來期望你週末可以完成更多功能開發，結果幾乎沒有變動」——
這個評價成立，帳目記在這裡：週末唯一的功能項是待辦 ⑤（D1–D4，而且全在失敗路徑），
其餘都是稽核與驗收。**新的排程原則：長時段優先做看得見的功能切片，驗收類工作要事前明講。**

### M3 工具輪盤：計畫與第一塊地基已入庫

| 產出 | 內容 |
|---|---|
| 計畫 | `docs/aegis/plans/2026-10-05-m3-tools-wheel.md`——範圍、架構決定、T1–T8 可失敗驗收、分刀、風險，以及 **§8 執行計畫**（誰跑哪個檔、什麼順序） |
| 程式 | `src/Sidecar/Tools/IToolCatalog.cs` ＋ `Tools/ToolCatalog.cs`：工具知識的**唯一權威**（比照 `IFormatCatalog` 的分工，UI 只拿到字串） |
| 建置 | 綠，exe 時間戳 `14:18:43 → 14:35:07`，0 個原始碼比 exe 新 |

**目前只有一個工具：`ROTATE`（順時針 90 度）。** 選它的理由：沒有新相依、沒有授權問題，
而且驗收可以寫成客觀斷言（非方形圖轉完長寬必須對調，不必肉眼看圖）。
輪盤**只畫已實作的工具**——介面不誠實是這個專案明文拒絕的事。

**⚠ 現在還看不到任何東西**：第二層輪盤的繪製與 `空白鍵+Ctrl` 的觸發還沒接。
下一刀（A）做完才會出現「拖曳時按 Ctrl 看到第二層輪盤」，再下一刀（B）才是「圖真的轉了」。

### 歸檔整理（**沒有刪任何東西**）

使用者指示「先歸檔整理／歸檔」，所以三組東西是**搬進 `archive/`** 而不是刪除。
搬移時先算整組 sha256、搬完再比對：**25/25、24/24、3/3 完全一致**，舊位置已清空。

| 搬入 | 內容 | 關鍵事實 |
|---|---|---|
| `archive/glass-spike/` | 那次量到 16.67 ms 的**實際二進位**＋被取代的舊跑紀錄 | 結論的正式出處 `REPORT.md` 與 spike 原始碼**留在原處**（`tools/glass-spike/`），仍可重建 |
| `archive/m05-prototype/` | M0.5 原型的建置產物 | **原始碼不在歸檔裡也不需要**：git 歷史有（退休提交 `e56f7e1` 之前的 8 個檔案，`git show e56f7e1^:src/M05/Program.cs`）。刻意不放第二份副本 |
| `archive/s4m-test-output/` | 你 2026-10-05 真人拖曳產出的 3 張成品 | 副證；主要證據是 `docs/aegis/work/2026-10-02-s4-closure/` 的日誌 |

**刻意沒有動的**：`tools/dragsim/bin`——`verify-m1a.ps1:43` 會自動偵測那個 `DragSim.exe`，
刪了 S4 會退回 INCONCLUSIVE（**正是週末才修掉的問題**）。`docs/aegis/work/**` 是證據；
`.gitignore` 排除 `*.log` 是「本機保留、不入庫」的政策，不是垃圾。

**我自己中途的一個誤判（記下來）**：我一度以為 `src/M05` 裡有「未入庫、不可還原的原始碼」而發警報，
查證後是誤讀——那 3 個 `.cs` 是 `obj/` 的 MSBuild 產生檔，原始碼早在 `e56f7e1` 就退休了。
教訓：**用副檔名分組統計檔案＝不可靠的推論**，要指名道姓看檔案。

### 提交清單

| commit | 內容 |
|---|---|
| `c11371b` | 藥丸停留時間統一 2 秒（改回規格 §4.4）＋ S12.15d 由 WEAK 補強成 REAL |
| `1c2872c` | 歸檔整理＋M3 計畫＋`Tools/{IToolCatalog,ToolCatalog}.cs` 入庫 |
| `archive/` | 已追蹤：`README.md`、`run-output.txt`、3 張測試輸出（`bin/`／`obj/` 依 `.gitignore` 留本機） |

### 你現在可以做的事

- **什麼都不用按**：M3 的 A 刀還沒交付，現在開 Confern 看不到第二層輪盤。
- 想先看舊的失敗清單功能：`_S4m_test\run-failure-demo.cmd`（失敗藥丸現在 2 秒就走）。
- 想做首發驗收：`docs/TEST-PLAN-M1-first-run.md` 的 17 項，以及 S4m 的 S4a（**不按**觸發鍵拖 3 次）。
  這兩件需要你的手；`drop-here\` 已經清空，可以直接拖。

## 2026-10-05（下半場）M3：Shift 即時切層、輸出位置 C2、以及使用者自己的實測結果

### 使用者自己的實測（他自己的日誌為證，17:22）
- 測試 A：拖曳 → **格式輪盤**出現 → 產出 `測試圖-100x60.jpg` ✓
- 測試 B：**Shift 顯示前判定正確** — `wheel shown ... source=JPG targets=[ROTATE] mode=tool` ✓
- 他隨後按了 Esc 取消（沒有提交）。
⇒ **「Shift 顯示前判定」這條路徑，已經是真實按鍵驗證過的**；
  「輪盤顯示期間按 Shift 切層」那一行仍未驗（見下方測試工具的限制）。

### 已完成並提交
- `b7c8ec4` **A2**：Shift 即時切層。三個設計要點：`RenderWheelLayer` 是唯一一處真的畫輪盤
  （初始顯示與切層共用）；鉤子的 Shift 分支必須寫在 `isDown` 判斷**之外**（放開也要送進來，
  否則切不回格式層）；自己抓到一個 bug——工具清單原本只在 Shift 按著時才算，那樣
  「先顯示格式層、之後才按 Shift」會找不到工具可切。
- `29b9b80` **C2**：`NamingPolicy.ResolveOutputPath(source, ext, targetDirectory = null)`。
  null＝來源旁邊且逐位元不變（已用 `--rotate90` 與 `--convert` 兩條無頭路徑回歸驗證）；
  指定的資料夾不存在時**回退到來源旁邊並說出來**（靜默的回退會讓人以為設定生效了卻找不到檔案）。
  路徑的唯一權威仍然只有 `NamingPolicy`。

### 未完成（下一輪照這個順序）
- **C1**：`Settings.OutputFolder`（來源旁邊／桌面／指定）＋設定頁欄位＋持久化。
- **C3**：job 訊息加 `outputDir`、`WorkerProtocol.Version` 升到 3、worker 傳給引擎、
  輪盤顯示目的地（與來源不同時顯示資料夾名；中心孔只有 94 px，只放資料夾名，全路徑進日誌）。
- **D**：把今天的量測變成套件斷言（含 Shift 切層那一格）＋假故障演練。

### 測試工具的限制（照實記，免得下一個人重踩）
- 合成輸入**是有效的**：17:25:57 的 `trigger key DOWN/UP` 就是我注入的空白鍵。
- 但 DragSim 的拖曳時機抓不準（它要先開一個來源視窗才開始拖），而且**用位移量讀日誌不可靠**
  → 「Shift 顯示期間切層」兩次都沒抓到。判定：**INCONCLUSIVE，是測試工具的問題，不是產品**。
- 要驗那一格最省事的是人手按一次（約 2 秒）：拖曳 → 格式層出現 → 按 Shift →
  應出現 `wheel layer switch: format -> tool (shift down)`，放開再一行 `tool -> format (shift up)`。

### 環境備註
- 建置前必須先結束 Confern（它會鎖住 exe，build 會以 MSB3027 失敗）。我曾經因此誤殺使用者
  正在測的實例——**只殺自己啟動的那一個 PID**（`Start-Process -PassThru`）。

## 2026-10-05（第三段）C1a 完成，以及 C3 的做法決定

### 已完成並提交
- `31b1248` **C1a**：`Settings.OutputFolder`（空字串／`source`＝來源旁邊、`desktop`＝桌面、
  其他＝資料夾路徑）＋ `Settings.ResolveOutputDirectory()`（**回傳 null ＝來源旁邊**，正好是
  引擎 `targetDirectory` 要的預設值，所以「設定→引擎」的對應只發生在這一處）。
  `Load`／`Save` 是整份序列化，新欄位自動往返，讀寫一行都不用改。
- `rotate90` 診斷改走設定 → 它同時成為「設定 → NamingPolicy → 寫檔」整條鏈的可無頭驗證路徑。
- 驗證（`--settings-file` 指向臨時設定檔，兩次都當場量）：設定指向存在的資料夾 → 輸出真的落在
  該資料夾；指向不存在的資料夾 → 留下 `output dir: '...' unavailable -> fell back to the source
  folder` 一行且輸出落回來源旁邊。兩次的來源 SHA256 都沒變。

### 狀態誠實聲明
⚠ **設定目前只有「診斷路徑」看得到**。真正的 UI 路徑是「UI → 管道 → worker → 引擎」，
而 `outputDir` 還沒有經過管道——所以現在改設定檔，**從輪盤操作時不會生效**。
這是下一步（C3）要收掉的，而且必須**先於**把它做成設定頁欄位（不然就是「存得下但沒生效」）。

### C3 的做法決定（下一輪照這個做）
原計畫 §10.3 寫的是「job 訊息加 `outputDir`、協議升到 3」。實作前重新想過，改採**命令列**
而不是 job 訊息，理由：
- 這個值是**每個 worker 行程一個**（設定檔不會在一批轉檔中途改變），不是「每個工作一個」——
  放進 job 訊息等於把常數當變數傳，而且逼著升協議版本（雙方必須同時換，風險比較大）。
- 命令列參數是 worker **啟動時**就固定的，語意更準確，也讓 `--worker` 自己就能被測試
  （不必先建立一個假的 job）。
- 代價：worker 多一個參數解析（`--output-dir <path>`）。仍然只有一個來源——UI 讀設定檔，
  再傳給它啟動的 worker；**不讓 worker 自己去讀設定檔**（那會變成兩個擁有者，設定檔中途改變
  時兩邊會看到不同的值）。

切片順序：① worker 接受 `--output-dir` 並傳進 `Convert`／`Rotate90`；② client 啟動 worker 時
帶上它（來源仍是 UI 的設定）；③ 輪盤上顯示目的地（與來源不同時顯示資料夾名，全路徑進日誌）；
④ 設定頁欄位；⑤ 套件斷言。

### 工具教訓（兩條，都踩過）
1. **PowerShell 5.1 會把多行字串按換行拆開**再傳給原生程式，所以 commit 訊息裡只要有一行以
   連字號開頭（例如 `--rotate90 ...`），git 就會把它當成選項並失敗。改用 `git commit -F <檔案>`。
2. 訊息檔用 `Set-Content -Encoding UTF8` 會**帶 BOM**（標題會多一個看不見的字元）。改用
   `[System.IO.File]::WriteAllText($p, $msg, (New-Object System.Text.UTF8Encoding($false)))`。

## 2026-10-05（第四段）輸出位置整條鏈完成；並抓到一條被我弄紅卻沒人發現的套件檢查

### 使用者要求的「輸出位置」到這裡全部完成
- `31b1248` C1a（設定值＋解析）、`394e64d` C3（經 worker 到引擎、協議升到 3）、
  `37593ad` C1b＋C3b（設定頁欄位、輪盤上看見目的地）。
- 設定頁新增「輸出資料夾」一列：來源檔旁邊（預設）／桌面／自訂路徑；寫進檔案的一律是
  `""`、`"desktop"` 或實際路徑，不是中文標籤。相對路徑會被拒（頁面即時擋、命令列 exit 4）。
- 輪盤的檔名預覽會顯示目的地（`m3c-sample.jpg → dest`），`ComposePreviewText()` 是那一行的
  唯一組裝點。**目的地就是來源旁邊時，畫面與改動前逐位元相同**（三態 frame SHA256 一致，
  連改動前的 exe 都比對過）。
- ⚠ 這個切片唯一動到的既定外觀：設定頁視窗高度 780 → 820。理由不是隨手加的——加上第十列後
  780 真的放不下（量到捲軸軌道 `#CDCDCD`、卡片寬度少 17px），改 820 後三種狀態都掃不到捲軸。
  **若使用者不接受，這是唯一要回退的地方**（其餘都是純附加）。

### 我先前留下的錯誤（已修）
- `tools\verify-m1d.ps1` 的 `S12.10f` 把 worker 的 readiness 行寫死成 `protocolVersion=1`。
  協議在今晚由 1 → 2 → 3，所以**從 `877a2dd` 起這條就是紅的，而且一直沒被發現**
  （我先前沒跑過完整套件）。已改成比對 readiness 行本身、不綁版本，並在註解寫明原因。
  ⚠ 教訓：升協議這種全域契約改動，**當晚就該跑一次全套件**，不能只跑自己碰過的診斷路徑。

### 套件現況（2026-10-05 第四段，序列跑完）
`m1a` 48/48、`m1b` 14/14（S6）、`m1c` 20/20、`m1d` 54/54（修好後）、`m1f` 34/34、
`m1g` 25/25、`m1h` 62/62。全部 0 殘留行程。`m1b` 的 S8 需要真人拖曳，未跑。

### 工具教訓（新增）
1. **這台機器沒有 `pwsh`**——實際是 Windows PowerShell 5.1，所以要跑 `.ps1` 請用
   `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <path>`（在子行程跑，避免套件裡的
   `exit` 把呼叫端的指令一起帶走）。
2. 套件都需要 `-Exe <Confern.exe 路徑>`（必填）；只有 `m1b` 另外宣告了 `-DragSim`。
3. 讀套件輸出請用 `Select-Object -Last N`（`-First` 會提早殺掉上游並回傳錯誤的結束碼）。
4. 子代理實作＋協調者複驗這條路在這兩輪很有效（它能用自己的新上下文讀我讀不起的檔案），
   但**動 git 的仍然只有協調者**，而且回報必須附原始日誌行；這一輪它還抓到我給錯的
   日誌路徑（worker 日誌是 `%LOCALAPPDATA%\Confern\worker.log`，沒有 `logs\` 那一層）。

### 下一步（順序）
1. **Shift 顯示期間切層**那一行仍是 INCONCLUSIVE（測試工具時機問題）。最省事的驗法是
   請使用者拖曳後按一下 Shift（約 2 秒），或把它寫成套件斷言。
2. 續做 M3 的下一個工具（計畫 §1 的相依清單）。

## 2026-10-05（第五段）Shift 即時切層：查清楚為什麼無頭測不到，以及要怎麼才測得到

試了五輪合成驗證，全部 INCONCLUSIVE。不是白試——原因查清楚了，而且發現一個結構性的事實。

1. **合成輸入是有效的**：注入的空白鍵在日誌留下 `trigger key DOWN/UP`；而且全專案找不到
   `LLKHF_INJECTED` 之類的過濾——產品**沒有**刻意拒絕合成按鍵。
2. **`--raise` 診斷模式會送達真的 OLE 拖放**（`OLE DragEnter` → `wheel shown ... mode=format`），
   但**它不安裝鍵盤鉤子**（只有常駐路徑裝），所以在那裡注入 Shift 沒有聽眾。
3. **常駐路徑有鉤子，但合成拖放送不達**：日誌停在 `trigger key DOWN (state=Idle)`，沒有
   `OLE DragEnter`——只按觸發鍵不足以進入 Armed/Capturing。
   （m1a 的 S4 之所以成功，正是因為它用 `--raise`，也就是沒有鉤子的那一條。）
⇒ **結構性結論：目前專案沒有任何一條無頭路徑同時具備「鍵盤鉤子」與「送達的拖放」**，
   所以「輪盤顯示期間按 Shift 切層」這一格只有**真人按鍵**能驗。要讓它可自動化，需要一個
   新診斷模式（`--raise` 但**同時**安裝鉤子）——那是一個小而明確的後續切片，已列入下一步。
   ⚠ 在此之前，不要再重複這五輪的做法（DragSim＋注入）去撞這一格。

附帶學到的兩件事：
- 常駐路徑**不理 `--log`**（只有診斷路徑吃它），所以要讀預設位置
  `%LOCALAPPDATA%\Confern\logs\confern.log`；用時間戳前綴過濾「這一輪」的行，比用位移量可靠。
- 這台機器沒有 `pwsh`；套件要用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` 跑。

## 2026-10-05（第六段）M3 工具②「去 metadata」完成——以及一個推翻我自己指示的量測

`f5add4b`。M3 清單裡第二個不需要新相依的工具（第一個是已完成的旋轉）。

### 最重要的一件事：我的指示錯了，是量測把它推翻的
我原本要它照工具①的形狀做「解碼 → 處理 → 寫」。它**先量才動手**，量到 Q100 的 JPEG 重編碼會
改動 48x28 那張圖 5376 個位元組裡的 **949** 個、單通道最大差 **3** ⇒ 達不到「像素必須完全相同」
這條驗收。而 EXIF 正好只長在 JPEG 上，等於**在最需要它的格式上做不到**。
⇒ 所以改成分兩條路（**先寫進規格 §9.4 才寫程式**）：
- **JPEG 走容器層移除**：丟掉 APP1／APP2／APP3-13／APP15／COM，保留 APP0(JFIF) 與
  APP14(Adobe 色彩轉換旗標)，**熵編碼的掃描資料一位元組都不動** ⇒ 像素逐位元組相同。
- **其他格式走解碼重編碼**（與工具①同一條）。無損容器逐像素相同；WebP 有損會重新編碼。
- 唯一動到像素的情況：來源 EXIF 方向 != 1 時**先把方向烤進像素再丟 tag**（否則圖會躺下），寫進日誌。
- 被拒絕的選項也記在規格裡：全面重編碼、全面容器手術、WIC 的 `CreateForTranscoding`
  （查證 .NET 8 的 WPF `PresentationCore` **沒有**這個 API）、只去 EXIF 保留 ICC。

### 可複用的量測事實（以後不要再猜）
- **JPEG 在 Q100 下重編碼不是無損**：949/5376 位元組會變、單通道最大差 3。要位元組級保證
  就必須走容器層。這一條同時解釋了為什麼工具①（旋轉）在 JPEG 上是重編碼——那裡可以接受，
  因為它的產物本來就是新位置上的像素。
- 用 System.Drawing 讀回帶 EXIF 的 JPEG，會看到 JPEG 自己的量化表被當成 property item
  （tag 0x5090／0x5091）；它們不是使用者資料，去 metadata 之後仍在是**正確**的。

### 驗證（判準是當場量到的，不是宣稱）
fixture 先證明**有** metadata（raw segments 有 APP1/EXIF、APP2/ICC 3144B、COM；EXIF 讀到 6 個
property item）→ 處理後同一種讀法全消失（剩下的兩個是量化表）→ 逐像素 5376 bytes、0 個不同、
最大差 0（JPEG 與 PNG 兩條路都量）→ 來源 SHA256 與 mtime 未變 → 輸出資料夾兩種都量
（不給＝來源旁邊、給了＝指定資料夾）→ 真的走過管道（`op=strip-metadata`，產物 SHA256 與
診斷路徑完全相同＝兩條入口同一顆引擎）→ 格式層畫面用 HEAD 版 exe 做 A/B，dump 逐位元相同。
套件：m1a 48/48、m1b 14/14、m1d 54/54、m1f 34/34。
**複驗（我做的）**：用它的 fixture 獨立重跑，輸出 SHA256 逐位元組相同、來源未動。

### 尚未驗證（誠實聲明）
BMP／TIF／GIF／WebP 的逐像素沒有實測（無損容器是推論）；EXIF 方向 2/3/4/5/7/8 沒有逐一量；
結構異常但 WIC 解得開的 JPEG 可能走退回重編碼（該分支沒造檔測）；新圖示在 17px 下的語意
偏弱（放大 3 倍才確認是「被劃掉的標籤」），語意主要由扇區文字 CLEAN 承載；
工具說明「去中繼資料」在 17px 量到 97.3px，比 ToolCatalog 註解寫的 68px 預算寬
（膠囊 129px 仍在第一層既有的 138-145px 範圍內）。

## 2026-10-05（第七段）更正上一段：Shift 即時切層其實是**真的壞掉**，現已修好並有自動化證據

上一段寫「沒有任何無頭路徑同時具備鉤子與送達的拖放，所以這一格只有真人能驗」——**那是錯的**，
而且錯得很有意思：把兩個開關一起打開（`--raise-with-hooks`）就都成立，我當時只是沒想到要一起開。
真正的原因是**產品缺陷**：

- 鉤子的 Shift 分支比對的是通用 `VK_SHIFT`（0x10），但**真實鍵盤的低階鉤子送來的是
  `VK_LSHIFT`（0xA0）／`VK_RSHIFT`（0xA1）**。所以「輪盤顯示期間按 Shift」對真人**從來沒有生效過**
  ——唯一還能用的路是「拖曳前就先按住 Shift」（顯示時用 `GetAsyncKeyState` 判定的另一條路），
  而那正是 2026-10-05 裁定要淘汰的舊行為。
- 也就是說：我前面五輪的 INCONCLUSIVE **不是測試工具的錯**，是產品真的壞了。
  我把它歸因於 harness，是我判斷錯誤——這一條要記著：**當一個功能怎麼測都測不出來時，
  下一個假設應該是「功能本身沒作用」，不是「我的工具爛」。**

修法（`3bfb44a`）：條件改成 `vk == VK_SHIFT || 0xA0 || 0xA1`，並新增
`--raise-with-hooks`（raise 的同時裝鉤子）與 `--wheel-layer-probe <png>`（唯一可自動化入口），
把鉤子回呼抽成 `WireHooks()` 全專案唯一一處，否則「探針過了」只證明探針那一份是對的。
注入必須從**另一個執行緒**做：`WH_KEYBOARD_LL` 的回呼在安裝鉤子的執行緒上執行，同執行緒送鍵
會互相等待，系統在 `LowLevelHooksTimeout` 後放行並可能移除鉤子 ⇒ 造成假陰性。

證據（探針原始日誌，`hookKeysSeen=[0xA0-down]` 正是那個 bug 的值）：
```
wheel shown ... targets=[ROTATE,CLEAN] selected=0 notice=- preview=- mode=tool
wheel layer switch: format -> tool (shift down, selection reset to first)
after injected Shift DOWN -> mode=tool hookKeyCallbacks=0->1 hookKeysSeen=[0xA0-down]
PASS  the keyboard hook callback really ran for the injected key (not a direct in-process call)
wheel layer switch: tool -> format (shift up, selection reset to first)
after injected Shift UP -> mode=format hookKeyCallbacks=1->2 hookKeysSeen=[0xA0-down,0xA0-up]
wheel-layer-probe: PASS (exit 0) hookKeyCallbacks=2
```

⚠ **我自己在這一段裡又踩了一次同樣的坑**：第一次跑探針時我用 `& $exe ...` 呼叫 GUI 行程，
PowerShell **不會等待**，`$LASTEXITCODE` 拿到的是前一個指令（建置）的 0，而且我緊接著就把 fixture
刪了 ⇒ 探針回 `no such file -> exit 1`，我卻以為通過並據此提交。**GUI 行程一律用
`Start-Process -Wait -PassThru` 讀 `ExitCode`**，而且 fixture 要活到行程結束為止。
（這一條先前已經記過一次，還是再犯——所以它不是「知道」的問題，是每次都要真的照做。）

待辦：0xA0／0xA1 應該搬進 `Native` 與 `VK_SHIFT` 放在一起（金鑰常數只有一個家）。

---

## 第八段（2026-10-06 上午）：修掉「設定頁關掉後再也開不回來」＋補上一直缺的可觀測性與覆蓋

使用者實測回報：「我測試隨便點了一下整個設定頁就消失了，再去左下角（托盤）按也叫不出來。」
這一條**真的有病**，而且舊的驗收完全照不到它。

### 根因（量到的，不是推論）

`Drain()` 跑在 `DispatcherTimer` 的回呼堆疊上，而佇列裡的工作可能是**阻塞的** `SettingsWindow.ShowDialog()`（巢狀 dispatcher 迴圈）。WPF 的 `DispatcherTimer` 是「回呼返回之後」才排下一次 tick（dotnet/wpf `DispatcherTimer.FireTick`：`if (Tick != null) Tick(...); if (_isEnabled) Restart();`），所以回呼一旦不返回，計時器就永遠不再 tick ⇒ `Pending` 再也沒人抽 ⇒ 之後每一次 `Open()` 的工作項都躺在佇列裡，`IsOpen` 停在 true、視窗永遠不出現。

為什麼「第一次開沒事、第二次必死」：第一次的 `Drain()` 是在 `Dispatcher.Run()` **之前**直接呼叫的，不在回呼堆疊上，所以 modal 迴圈裡的 tick 照常觸發；第二次是在 tick 回呼裡抽到那個阻塞工作，於是 tick 從那一行之後全部消失。

量到的證據（探針逐行記 tid／drainDepth／dialogDepth，收工前全部移除）：
- 阻塞後 25 ms 的 tick 應有約 200 行，實際 **0 行**；但 dispatcher 仍在抽訊息（`BeginInvoke` 在 `dialogDepth=1` 之下照樣跑完）⇒ **不是** UI 執行緒死掉。
- modal 迴圈一退出，tick 立刻大量恢復（正證）。
- 期間視窗其實是好的（`IsLoaded=True IsVisible=True 540x820`）⇒ 問題在「再也沒人把工作抽出來」，不在畫視窗。

⚠ 走過的彎路（四個被推翻的假設，值得記下來）：UI 執行緒死掉 ✗、兩個實例互相干擾 ✗（使用者單一實例下照樣重現）、擷取開始時產品主動關頁 ✗（常駐路徑沒有任何 `SettingsUi.Close()`）、托盤守衛擋住重開 ✗。⇒ 教訓：**當症狀完全量不到時，先假設是「功能真的壞了」，並且先問「有沒有任何測試真的走過這條路」。**

### 修法（最小一刀）

`src/Sidecar/Settings/SettingsUi.cs` 的 pump：回呼**只排**一次 `Drain` 後立刻返回
`(_, _) => dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain))`。
擁有者是 pump 那一行（計時器回呼與阻塞工作耦合），`Drain()` 仍是唯一的抽乾實作。

### 同時補上兩件一直缺的東西

- **可觀測性**：設定頁執行緒的日誌佇列原本只在特定主迴圈抽乾，純托盤操作時**永遠不會**寫進日誌——使用者那一輪 `settings ui:` 的行數是 **0**，等於診斷全瞎。現在常駐路徑也會抽。
- **覆蓋**：`--settings-ui-selftest` 與 `--tray-selftest` 都**沒有**「關掉之後再開一次」這條，而且關窗走的是產品自己的 `SettingsUi.Close()`，不是使用者的方式（視窗自己的取消鈕）。現在兩者都補上，並新增「視窗真的存在」的檢查，用來區分「`IsOpen` 說開著」與「畫面真的在」。

### 驗收（提交 `f7a5051`，工作樹乾淨）

- 反向驗證：把那一行改回去，兩支自測都**紅**——`reopened=True windowPresent=False ... the settings UI thread did not pick it up`；托盤 `FAIL the open-settings command opens the page AGAIN after it was closed`。改回後綠。
- `--settings-ui-selftest`：exit 0，`ok=True (… reopened=True windowPresent=True)`。
- `--tray-selftest`：exit 0，`checks=14 failed=0`（新增的那條是「關掉後托盤能再開」）。
- 既有套件：m1f 34/34、m1g 25/25、m1d 66/66、m1a 48/48。殘留行程 0。

### 使用者現在可以做的事

1. **重測那條壞掉的路**：啟動 Confern → 托盤「開啟設定」→ 隨便點幾下 → 用視窗自己的取消鈕關掉 → **再從托盤開一次**（這就是原本叫不出來的那條）。
2. **補測①輸出位置功能**（上一輪因為設定頁消失而沒測到）：設定頁裡把「輸出資料夾」切成 來源旁邊／桌面／指定資料夾，各轉一張圖，確認成品真的落在你指定的地方（輪盤上的檔名預覽行也會顯示目的地）。
3. **補測③去中繼資料**：用**手機拍的**照片（真的帶 EXIF）轉一次，然後在檔案總管 → 右鍵 → 內容 → 詳細資料，確認拍攝資訊真的不見了；上一輪你那個測試檔本身沒有 EXIF，所以只證明「像素沒變」。

### 尚未處理（誠實記錄）

- 子代理量到但**刻意沒修**（維持最小一刀）：`EnsureUiThread` 的 `if (_thread != null) return;` 只會啟動一次——UI 執行緒若真的結束，之後的 `Open()` 會排進沒有抽乾者的佇列，外觀與本症狀一模一樣。所有實測中該執行緒都活著（`alive=True`），所以沒有把本次 bug 歸給它，列入後續追蹤。
- 子代理未能逐位元重現使用者那一輪的點擊序列（四次托盤日誌是二手資訊）；機制能完整解釋症狀。

---

## 第九段（2026-10-06 下午）：修掉「設定頁點輸入框→整窗消失、托盤再也叫不出來」——順便查清 m1d 那盞紅燈的真相

### 根因（量到的，不是推論）

使用者那兩句話是同一條因果鏈的兩端：「選『輸出位置』後，要按輸入框輸入時設定面版消失」與「之後再從托盤按『開啟設定』，還是叫不出來」。

鏈的起點在 `src/Sidecar/Sidecar.csproj`：`<InvariantGlobalization>true</InvariantGlobalization>` 讓行程**沒有真實文化**，而 WPF 的輸入層一定要真實文化。輸入框**第一次**拿到鍵盤焦點要建 caret 時，`InputLanguageSource.get_CurrentInputLanguage()` 去要 `CultureInfo(0x0404)`，在 invariant 模式下丟 `CultureNotFoundException`：

```
System.Globalization.CultureNotFoundException: Only the invariant culture is supported in
globalization-invariant mode. ... 1028 (0x0404) is an invalid culture identifier.
  at System.Windows.Input.InputLanguageSource.get_CurrentInputLanguage()
  at System.Windows.Documents.TextSelection.EnsureCaret(...)    <- 輸入框第一次取得焦點
  at System.Windows.Input.InputManager.ProcessStagingArea()
  at System.Windows.Input.KeyboardDevice.ChangeFocus(...)
  at System.Windows.UIElement.Focus()
```

例外穿過 `ShowDialog` 的 modal frame 跑掉，把設定頁那條 UI 執行緒帶走；OS 會拆掉死執行緒的視窗（所以使用者看到「連標題列都不見」），而 `EnsureUiThread` 的 `if (_thread != null) return;` 讓之後每一次 `Open()` 都只把工作排進**沒有抽乾者**的佇列——日誌上只看得到 `open requested`，沒有視窗、沒有錯誤、也沒有 `already open`。使用者自己日誌裡另一個呼叫點是同一個擁有者的另一條路：`FAILED to show the window (InvalidOperationException: Cannot find non-neutral culture related to 'en-us'.)`。

⚠ 與「輸出資料夾」那段的驗證無關：那段有 try/catch，而且例外根本沒走到那裡。

### 修法（各自最小、擁有者明確）

- `Sidecar.csproj`：`InvariantGlobalization` true→false（「這個行程有沒有真實文化」只有這裡決定）。產品自己的程式碼沒有任何一處需要不變文化：全專案的比較都用 `StringComparer.Ordinal`，使用者語言走 Win32 的 `GetUserDefaultUILanguage`。
- `SettingsUi`：`Dispatcher.Run()` 改成**可重入**迴圈——例外穿出去只記一筆，同一條執行緒繼續跑（擁有者＝這條執行緒的壽命）。**不採「重啟一條新執行緒」**：實測一個 AppDomain 只准一個 `Application`，第二條執行緒開的視窗根本不上螢幕（`IsLoaded=False IsVisible=False hwnd=(none)`），所以「重建一條」救不回來。
- `SettingsUi.EnsureUiThread`：判準改成「那條執行緒還活著嗎」（`_thread is { IsAlive: true }`），並把上一條**為什麼走了**寫進日誌。
- `SettingsUi.Open` 的 catch：把開不起來的那張視窗關掉（否則留下關不掉的幽靈窗，下一次再疊一張）。
- `_generation`：重啟後舊工作靠 generation 自我取消，所以不清理、不重編號。

### 反向驗證（各弄壞一次→紅，還原→綠）

| 弄壞什麼 | 量到什麼 |
| --- | --- |
| csproj 改回 `true` | `symptom1StillThere=False userStepsDone=1`（25 步的序列**斷在第 1 步**：`input focused=...`＝使用者點下去的那一步）、`ok=False`、exit 1 |
| 拿掉 dispatcher 迴圈的重新進入 | `threadUsable=False uiThread=dead`；重開後 `windowPresent=False`（`IsLoaded=False IsVisible=False hwnd=(none)`）、`ok=False`、exit 1 |
| 還原 | `ok=True`、exit 0 |

### 新斷言（`--settings-ui-selftest`，本段當場重跑）

- **症狀 1**：把使用者那串互動**排進 UI 執行緒的 dispatcher**（不是由呼叫端直接呼叫——例外必須穿過 `ShowDialog` 的 modal frame 才是同一個故障），並逐字記下走到哪一步。
- **症狀 2**：**故意**在 UI 執行緒丟一個穿出 `Dispatcher.Run()` 的致命例外，然後問「托盤還開不開得起來」。

實測 `ok=True`、exit 0：

```
settings ui selftest done: ok=True (opened=True singleInstance=True closed=True saved=True
  invalidSaveAccepted=False relativeOutputFolderSaveAccepted=False reopened=True windowPresent=True
  symptom1StillThere=True userStepsDone=25 symptom2FatalDelivered=True symptom2ThreadUsable=True
  symptom2Reopened=True symptom2WindowBack=True)
```

25 步裡**逐字打到中文路徑**（`C:\Confern-selftest\輸出`），每一步的 `notice` 都被記下來——那正是原本會斷掉的那一步。

### 套件（本段全部當場重跑；每次跑前 Confern 行程數 0，跑完 0）

- m1a `checks=48 pass=48 fail=0 inconclusive=0`、m1f `34/34`、m1g `25/25`，三支都 exit 0。
- **m1d：`checks=66 pass=65 fail=1 inconclusive=0`，紅的是 `S12.10i`——但這盞紅燈與本次改動無關**，見下一節。

### m1d 的 `S12.10i` 為什麼紅：它不是回歸，是**測試自己用了使用者的設定檔**

`S12.10i` 問的是「放手（mouse up）那條路徑轉出來的檔有沒有落地」，而它找檔的地方是**寫死的** `Split-Path -Parent $srcJpg`（＝`%TEMP%\m1d-verify`）。但同一支套件的 `S12.10a`–`S12.10i` 走的是 `--wheel-payload`，也就是**產品 UI 那條路**：輸出資料夾跟著 `Settings.OutputFolder`（規格 §9.5）走。而這幾條**沒有**傳 `--settings-file`，所以它繼承的是**使用者真的那一份** `%LOCALAPPDATA%\Confern\settings.json`。

量到的事實（原始證據）：

- payload 日誌：`wheel destination: outputDir=C:\Users\User\Desktop sourceFolder=...\m1d-verify wheelLabel=-> Desktop`、`ENTER: outputDir=C:\Users\User\Desktop`。
- 每跑一次 m1d，桌面就多一組 `art (N).png` / `art (N).bmp`（256054 bytes），時間戳與該次執行完全對上。
- 那份設定檔裡就是 `"OutputFolder": "desktop"`。

四個方向都量了：

| 程式碼 | `OutputFolder` | m1d 結果 |
| --- | --- | --- |
| HEAD（`ddcbe4d`，含 `d3a588c`） | `desktop` | `66/65/1` **紅**（連跑 3 次，逐次完全相同） |
| `f7a5051`（把 `d3a588c` 的三個檔暫時還原再重建） | `desktop` | `66/65/1` **紅**，同一條 `FAIL S12.10i ... [target=BMP files=0]` |
| HEAD（**同一顆二進位**） | `""`（來源旁邊） | `66/66/0` **綠** |
| HEAD（**同一顆二進位**） | `desktop`（還原回去） | `66/65/1` **紅** |

⇒ **不是回歸**。改動前的程式碼也紅，而且紅得一字不差；同一顆新二進位只把設定檔那**一個欄位**從 `desktop` 改成 `""` 就綠、改回來又紅。今晚稍早那次全綠，反推回去就是**當時那份設定檔還是「來源旁邊」**——它是在 `d3a588c` 提交之前（量到的 mtime 13:51:58，早於 14:11:51）被使用者實機驗收改成「桌面」的，紅燈是從那之後才出現的。

⚠ 上一輪子代理說「拿改動前的舊二進位跑也一模一樣失敗」，但給的數字是 `pass=64 fail=1 inconclusive=1`——**與「一模一樣」不符，那個說法不成立**；本段重做後的舊碼是 `66/65/1 inconclusive=0`，與新碼逐字相同。

這一格的**設計脆弱**照實記下來（**這一輪刻意不動它**）：它驗的其實是「成品落在來源旁邊」，而那只有在使用者的設定剛好是預設值時才成立——使用者的設定一改，同一顆好二進位就會紅。最小可行的穩定化有兩條，原則是同一個（**測試用的設定檔不可以是使用者的那一份**）：讓 payload 那幾條也自己帶一份 `--settings-file`（`S12.16a/b/c` 已經是這樣做的），或讓它去**問產品自己報的輸出位置**再找檔，而不是自己猜一個資料夾。

### 使用者實機驗收（本專案最強的證據類型）

- **輸出位置→桌面 ✅（使用者實機驗收）**：成品真的落在桌面——`art.bmp`（256054 bytes，2026-10-06 14:06:58 起，之後每次遞增 `art (N).bmp`），輪盤上的目的地行也顯示 `-> Desktop`。
- **去中繼資料 ✅（使用者實機驗收）**：用真的帶 EXIF 的照片轉過，拍攝資訊真的不見了。

### 尚未處理（誠實記錄）

- **設定頁再放大**（欄位與字級）——使用者回報版面仍偏小。
- **單一實例守衛**：兩個 Confern 同時跑＝兩套全域鉤子、兩個托盤，確實會重複處理。**未證實**是任何已知病因，但「會重複處理」本身就要擋。
- **M2 卡在 ffmpeg 授權**（LGPL/GPL 條款未定）。
- **M3 壓縮已拍板走設定頁**（預設集三種＋目標大小），原本排在「設定頁輸入框」修好之後——那個前置條件**已經解除**，可以往下做了。
