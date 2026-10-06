# 私有桌面（CreateDesktop）能不能當驗收的「模擬空間」？— 可行性驗證

- 日期：2026-10-06
- 專案：`C:\Users\User\Desktop\專案\tangerine-win`，分支 `master`
- HEAD：`b9d7d52`（開工時）→ 期間另一個工作流提交了 `47c93f1`（m1d S12.10），本報告的結論不受影響
- 環境：Windows 11 25H2（NT 10.0.26200），單螢幕 2560x1080，Windows PowerShell 5.1.26100.9168，`pwsh` 不存在
- 探針：`tools\probe-private-desktop.ps1`（新增，純 ASCII，可重跑）
- 這一輪**沒有**修改任何既有 `.ps1` / `.cmd` / `src\**`，也沒有 `git add` / `commit` / `reset`

---

## 0. 結論摘要

| 問題 | 結論 | 一句話 |
|---|---|---|
| Q1 私有桌面上截得到那個桌面嗎？ | **截不到（桌面層）／截得到（視窗層）** | `GetDC(NULL)`+`BitBlt`、`CreateDC("DISPLAY")`+`BitBlt`、`Graphics.CopyFromScreen` 三條在私有桌面全部失敗（error 6 / CLR_INVALID / Win32Exception）；只有 `PrintWindow(PW_RENDERFULLCONTENT)` 成功，且截到的正是那個桌面上的 marker 視窗 |
| Q2 私有桌面上的輸入會影響使用者嗎？ | **完全不會——因為作業系統直接拒絕送進去** | 使用者游標前後 `1858,1059` 不動、0 個取樣命中目標；私有桌面上 `SetCursorPos` 回 `False err=5(ACCESS_DENIED)`、`SendInput` 回 `0 err=5` |
| Q3 需要 Explorer／drop 的檢查能在那裡跑嗎？ | **drop 目標視窗可以，內建殼層沒有；真 OLE 拖放不行** | 私有桌面無任何殼層視窗；自建 drop 目標視窗成功，兩條 `WM_DROPFILES` 投遞路徑都成功；但 `DragSim` 靠 `SendInput`，在私有桌面送不進去 |

**最重要的一句話**：私有桌面之所以不會干擾使用者，不是因為輸入被「隔離到另一個桌面」，而是因為**非 input desktop 的真實輸入 API 會被 OS 拒絕**。這同時意味著：把現在會搶滑鼠的檢查搬進私有桌面，並不會讓它們跑起來——它們會變成全程 no-op。否定式斷言（例如 S4a.1「沒有任何東西被升起」）甚至會因此**假通過**。所以「開一個模擬空間自己測」在 Windows 上不是靠 private desktop 達成的。

---

## 1. Q1：私有桌面上能不能截到「那個桌面」？

### 1.1 判定方法

我用一個**只在私有桌面存在的 marker 視窗**（洋紅 `CreateSolidBrush(0x00FF00FF)`、400x300、位於 (0,0)）當試金石，然後在同一個私有桌面的行程裡試四條擷取路徑。判定標準不是單純「全黑」：

- **A**：`GetDC(NULL)` 拿到的 screen DC 能不能 `BitBlt`（回傳值＋`GetLastError`），`GetPixel` 是否回 `CLR_INVALID`；
- **B**：`CreateDC("DISPLAY")` 是不是另一條活路（`GetDeviceCaps` 是否回得出 2560x1080）；
- **C**：**產品自己用的那一行** `Graphics.CopyFromScreen`（= `GlassSurface.CaptureFromScreen` 與 `EvidenceScreenGrab` 的實作）在那裡會怎樣；
- **D**：`PrintWindow(PW_RENDERFULLCONTENT)` 這條不需要 input desktop 的路。
- 對照組：**同一行 `CopyFromScreen` 在使用者桌面**跑一次（只讀 1 個像素、不存檔），證明程式碼本身沒問題、差別只在桌面。

### 1.2 原始輸出（節錄自探針一次實際執行）

```
  desktop=DshPdProbe_0d72d835
  screen=2560x1080
  t3457384437 GetCursorPos_ret=False readonly_cursor=0,0
  windows_before=(none)
  magenta_hwnd=0x9F40750
  t3457385359 after_pump windows=class=Touch Tooltip Window visible=False | class=UAC_InputIndicatorOverlayWnd visible=True | class=UAC Input Indicator visible=True | class=DshPdProbeMagentaWnd visible=True | ...
  A_GetDC_NULL hdc=0xFFFFFFFFD901275A err=0
  A_GetPixel(200,150)=0xFFFFFFFF err=0 (0xFFFFFFFF=CLR_INVALID)
  A_BitBlt(src=screen DC of this desktop) ok=False err=6 (6=ERROR_INVALID_HANDLE)
  B_CreateDC(DISPLAY) hdc=0xC0128DA err=6
  B_GetDeviceCaps HORZRES=2560 VERTRES=1080
  B_BitBlt(src=DISPLAY DC) ok=False err=6
  C_CopyFromScreen threw=YES Win32Exception HResult=0x80004005 nativeError=6
  C_consequence=GlassSurface.CaptureFromScreen() returns null -> colour fallback; EvidenceScreenGrab() logs FAILED
  D_PrintWindow(PW_RENDERFULLCONTENT) ok=True err=6
  D_png=C:\...\cap-D-printwindow.png pixel_100_75=0x00FF00FF
      grid_samples=144 nonblack=144 pct=100

  USER desktop at the same coordinates: 200,150=0x00E3FC  100,75=0x301707  300,225=0x4B2107  390,290=0x2F1708

  --- control: the SAME product call, executed from the USER desktop ---
  user desktop: GetPixel(200,150)=0x00E3FC   CopyFromScreen(200,150)=0x00E3FC   agree=True
  Q1 VERDICT: DESKTOP_CAPTURE_FAILS__PER_WINDOW_PRINTWINDOW_WORKS
```

### 1.3 判讀

1. **A 與 B 都失敗，且不是「全黑」**：`GetDC(NULL)` 回的 HDC 看起來有效（非 NULL），`GetDeviceCaps` 甚至回得出正確的 2560x1080，但 `BitBlt` 回 `False` 且 `GetLastError=6 = ERROR_INVALID_HANDLE`；`GetPixel` 直接回 `CLR_INVALID`。也就是說**連一個像素都讀不出來**，不是讀到黑畫面。
2. **C 就是產品那條路**：`Graphics.CopyFromScreen` 在私有桌面丟 `Win32Exception`（HResult `0x80004005`、native error 6）。依產品現有實作，這代表：
   - `GlassSurface.CaptureFromScreen()` 回 `null` → 走它本來就設計好的純色 fallback（`GlassSurface.cs:63-66` 的 `catch (Exception) { return null; }`），或改用 `GlassSurface.Synthetic(...)`。**這條路不會壞，因為它本來就為「抓不到」設計過**。
   - `EvidenceScreenGrab()`（`Program.cs:3459`）會走 catch，寫下 `pill screen grab: FAILED ... (the on-screen pixels could NOT be sampled in this session)`。**它也會誠實地承認抓不到**，不會產生空檔案假裝成功。
3. **D 是活的**：`PrintWindow(PW_RENDERFULLCONTENT)` 回 `True`，而且 144 個取樣點全部是 marker 的洋紅色 → **逐視窗擷取在私有桌面上可用**，且截到的確實是「那個桌面」的內容（洋紅視窗只存在於私有桌面；它沒有出現在使用者桌面）。
4. **對照組排除了「程式碼本身有問題」**：同一行 `CopyFromScreen` 在使用者桌面回 `0x00E3FC`，與 `GetPixel` 完全一致。

### 1.4 與外部記載一致

這不是本機特例。WindowsPC-MCP 專案的 known-issue 文件記載同一個現象：`GetDC(NULL)` 對非 input desktop 回的 screen DC **不能當 BitBlt 的來源**，`GetLastError` 為 `6 = ERROR_INVALID_HANDLE`，並列出補救選項（`PrintWindow` 逐視窗、DXGI Desktop Duplication、Windows.Graphics.Capture）——其中「逐視窗 `PrintWindow`」正是我這裡量到可行的那條。
（[v0.5.2 viewer blit failure](https://github.com/ShikeChen01/WindowsPC-MCP/blob/master/docs/known-issues/v0.5.2-viewer-blit-failure.md)、[GetDC (Win32)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdc)）

### 1.5 對產品的實際影響（唯讀查證）

順帶更正一個前提：**`--wheel-dump` / `--progress-dump-detail*` 並不使用螢幕擷取**。

- `--wheel-dump` → `WheelPresenter.DumpTo()`（`WheelPresenter.cs:288`）用 `new Bitmap(...)` + `Compose()` **離屏合成**，不碰螢幕。
- `--progress-dump-detail*` → `ProgressPresenter` 同樣是自己合成點陣圖。
- 真正做螢幕擷取的只有兩處：`Program.cs:3466` 的 `EvidenceScreenGrab`（`--pill-selftest-shot`）與 `GlassSurface.cs:59` 的 `CaptureFromScreen`（玻璃背景）。

而 `--pill-selftest-shot` 產生出來的那張 PNG，目前 `verify-m1h.ps1` **只當證據、沒有對它下任何斷言**（`$pillShot` 只出現在第 784、791 行：宣告與傳參）。所以在現況下，私有桌面上「截不到桌面」不會弄紅任何一條檢查；它只會讓那一行證據變成 `FAILED`。

---

## 2. Q2：私有桌面上的輸入會不會影響使用者的桌面？

### 2.1 為什麼要設計對照組

這一題的難點是「證明沒有一個動作發生」很容易變成自說自話。所以我做了三件事：

1. **先量使用者的游標位置（唯讀）**，並且在注入前先等一個「手離開滑鼠」的窗口（基線 1 秒；若在動就最多等 45 秒）。使用者在整段期間確實在用電腦，第一次跑（14:33）1 秒內有 5 個相異位置，探針那一次就依規則跳過了注入階段；第三次跑（14:37）量到 1 個位置才放行。
2. **對照組（零風險）**：拿**同一支程式**在使用者桌面執行、把游標設到**它現在所在的位置**（零位移的 no-op），看 API 回什麼。這證明「同一段程式碼在使用者桌面是被接受的」。
3. **只送 MOVE，永不送按鍵**：整個探針沒有任何 `LEFTDOWN` / `LEFTUP`。

### 2.2 原始輸出

```
  baseline: user cursor samples over 1 s = 1858,1059
  baseline: distinct positions = 1  (1 = the hand is off the mouse)
  --- control: the same two calls run from the USER desktop, aimed at the current cursor point (a no-op move) ---
  desktop=Default
  t3457386578 GetCursorPos_ret=True cursor=1858,1059
  t3457386578 SetCursorPos(no-op, same point) ret=True err=0 cursor=1858,1059
  t3457386578 SendInput(MOVE to the same point) ret=1 err=0 cursor=1858,1059
  RESULT=on the input desktop SetCursorPos=True SendInput=1 (1 = the event was accepted, 0 = refused)
  user cursor right after the control: 1858,1059  (a no-op move cannot change it)

  READ-ONLY evidence: the private desktop reports its own cursor ret=False pos=0,0 while the user desktop reports 1858,1059 at the same time
  targets: SetCursorPos -> 2520,1040   SendInput MOVE -> 40,40   (no button event is ever sent)
  --- helper log (running on the private desktop) ---
  desktop=DshPdProbe_0d72d835
  screen=2560x1080
  t3457386625 readonly_cursor=0,0
  t3457388187 p_before=0,0
  t3457388187 after_SetCursorPos ret=False err=5 cursor=0,0
  t3457388593 SendInput_move ret=0 err=5
  t3457389000 cursor_after_sendinput=0,0

  --- user-desktop cursor samples (tick:position, only on change) ---
  3457386671:1858,1059

  user cursor before=1858,1059  after=1858,1059  distinct positions during the window=1
  samples that landed EXACTLY on the private-desktop targets: SetCursorPos target=0  SendInput target=0
  private-desktop cursor reached the SetCursorPos target: False ; reached the SendInput target: False
  Q2 VERDICT: USER_CURSOR_UNAFFECTED
```

### 2.3 判讀

1. **使用者桌面完全沒被影響**：整段注入窗口（5 ms 取樣的 6 秒迴圈）使用者游標只有 **1 個相異位置** `1858,1059`，與 before/after 完全相同；**0 個**取樣點命中私有桌面的兩個目標座標（2520,1040 與 40,40）。
2. **私有桌面上的輸入被 OS 直接拒絕**：
   - `GetCursorPos` 在私有桌面回 **False**（連自己的游標狀態都沒有，值 `0,0` 不可信）；
   - `SetCursorPos` 回 **False，`GetLastError=5 = ERROR_ACCESS_DENIED`**；
   - `SendInput` 回 **0**（= 插入 0 個事件），`GetLastError=5`。
3. **對照組排除了「API 壞掉」**：同一支程式在使用者桌面回 `SetCursorPos=True`、`SendInput=1`。差別只有桌面。
4. **因此這題的答案比預期更強、但方向不是我們要的**：私有桌面不只「不會干擾使用者」，它根本**收不到真實輸入**。要讓 `SendInput` 進私有桌面，唯一辦法是 `SwitchDesktop` 切過去——那會把使用者的畫面整個換掉、並吃掉他手上的鍵鼠輸入，正是本案要避免的事。

### 2.4 安全取捨（誠實聲明）

- 我原本的設計是「先 `SetCursorPos` 到遠角、必要時再 `SendInput`」，**萬一 OS 沒有拒絕，使用者的游標真的會被丟到角落一次**。實際執行時第一階段的 `SetCursorPos` 就回了 `False err=5`，所以第二階段的 `SendInput` 也必然是 no-op——風險在量到的當下就已經歸零。但我要誠實說：**如果只被拒一半（例如 `SetCursorPos` 成功而 `SendInput` 失敗），這個實驗就不是零風險的**。
- 目標點選在離使用者游標最遠的對角（他 `1858,1059` → 目標 `2520,1040` 與 `40,40`），並且全程 5 ms 取樣以便留下完整軌跡。
- 第一次執行時偵測到使用者在動滑鼠，依規則**跳過**注入階段（見 §5 的第一份輸出），只報純程式面的證據。
- 全程沒有送出任何滑鼠按鍵事件。

---

## 3. Q3：需要 Explorer／drop 的檢查能不能在私有桌面跑？

### 3.1 原始輸出

```
  user-desktop windows before: 945
  shell classes present on the private desktop BEFORE anything: (none)
  drop_hwnd=0x9F80750
  window_class=DshPdProbeDropWnd rect=300,300 500x400
  windows_on_this_desktop=class=DshPdProbeDropWnd visible=True | class=CicLoaderWndClass visible=False | class=MSCTFIME UI visible=False | class=IME visible=False
  windows the PROBE sees on the private desktop handle: Touch Tooltip Window|pid=22008|vis=False , UAC_InputIndicatorOverlayWnd|pid=22008|vis=True , UAC Input Indicator|pid=22008|vis=True , DshPdProbeDropWnd|pid=22008|vis=True , ...
  feed: found hwnd=0x9F80750
  feed: t3457392890 PostMessage(WM_DROPFILES) ret=True err=0 hdrop=0x1BF10008
  feed: payload=C:\...\drop-sample.txt
  cross-desktop post from the probe (user desktop -> window on the private desktop): posted ok hdrop=0x209CE870008
  --- drop target log ---
  drop log DshPdProbe_0d72d835
  t3457392906 WM_DROPFILES count=1 file[0]=C:\...\drop-sample.txt
  t3457392937 WM_DROPFILES count=1 file[0]=C:\...\drop-sample.txt
  drops received by the private-desktop window: 2 of 2 attempted (same-desktop feeder + cross-desktop post)
  foreign GUI test binary: C:\Windows\System32\charmap.exe
  private desktop windows while it runs: ... tooltips_class32|pid=26620|vis=False , #32770|pid=26620|vis=False , ComboLBox|pid=26620|vis=False , ...
  NEW windows on the USER desktop: (none)
  user-desktop windows after the whole Q3 phase: 944 (new vs before: 0)
  Q3 VERDICT: DROP_TARGET_WINDOW_CREATED ; shell present on the private desktop: NO
```

### 3.2 可以做到的部分（已量到）

1. **私有桌面沒有殼層**：在任何東西啟動前，`EnumDesktopWindows` 回空集合；啟動之後也只有我們自己行程建立的視窗（外加同一行程建立的 IME／UAC indicator 視窗）。**沒有任何 `Shell_TrayWnd` / `Progman` / `WorkerW` / `CabinetWClass`**。
2. **drop 目標視窗可以在那裡活著**：自建 `DshPdProbeDropWnd`（500x400、`DragAcceptFiles`）成功建立且 `visible=True`；而且**探針（活在使用者桌面）用 `EnumDesktopWindows(hDesk)` 也看得到它**——代表外部工具能觀測私有桌面上的視窗。
3. **drop 可以用「訊息」驅動，完全不必動真實滑鼠**，而且兩條路都通：
   - 同一桌面上的另一個行程（feeder）用真實 `HDROP` 送 `WM_DROPFILES` → 目標收到 `count=1`、路徑正確；
   - **探針自己（在使用者桌面）跨桌面 `PostMessage`** → 目標同樣收到 `count=1`、路徑正確。
4. **外來 GUI 行程可以在那裡開視窗**：`charmap.exe` 在私有桌面建立了 `tooltips_class32` / `#32770` 等視窗，而**使用者桌面的視窗數 945 → 944、新增 0 個**（少的那一個是期間自行消失的視窗）。也就是說：在私有桌面開視窗，使用者桌面上什麼都不會出現。

### 3.3 做不到 / 沒驗的部分

- **內建 Explorer 殼層：本輪沒有實測啟動**。理由是風險與收益不成比例：`explorer.exe` 有 single-instance 行為，在已經有殼層的 session 裡另起一個，可能在使用者桌面彈出一個檔案總管並搶走焦點——他正在打字。使用者不在時可以在幾秒內安全測完（指令：用 `probe-private-desktop.ps1` 的 `Start-OnPrivateDesktop` 換成 `explorer.exe`，再列舉私有桌面與使用者桌面的視窗）。
- **`DragSim` 搬不進去（確定不行，不是沒驗）**：`DragSim` 靠 `SendInput` 造出真實拖曳、且 `DoDragDrop` 必須在同一個執行緒回應 `WM_LBUTTONDOWN` 時同步呼叫（`tools\dragsim\Program.cs:9-11`）。Q2 已經量到私有桌面上 `SendInput` 回 0，所以它在那裡連「開始拖曳」都不會發生。
- **`WM_DROPFILES` ≠ 產品的 OLE 路徑**：產品已經 `RegisterDragDrop`，`verify-m1a` S4.2 斷言的字串是 `*** OLE DROP RECEIVED (IDropTarget)`，而 `verify-m1a.ps1:473-479` 自己記著「`RegisterDragDrop` 之後 `WM_DROPFILES` 再也不會到（實測）」。我上面證明的訊息層投遞**走不到那條路徑**。要無滑鼠地走到 OLE 那條，唯一辦法是在行程內直接呼叫 `DropTarget.Drop(IDataObject, ...)`（它是 public，但**目前沒有 CLI 接縫**）。

---

## 4. 唯讀盤點：目前哪些檢查真的會用真實輸入

（只做盤點，沒有修改任何既有檔案。`verify-m1d.ps1` 期間由另一個工作流改動並提交為 `47c93f1`，我沒有碰它。）

| 檔案 | 檢查代號 | 輸入類型 | 用什麼 | 能不能改用產品自己的介面／內部接縫 |
|---|---|---|---|---|
| `tools\verify-m1a.ps1` | **S3.1–S3.11** | **真實滑鼠＋鍵盤** | `[Probe]::MoveTo / Mouse(LEDOWN/LEUP) / Key / MoveRel` → `SendInput`（第 330–397 行）；含 `Ctrl+Alt+Shift+Q` 殺手鍵 | **部分可以**：`DragDetector.ForceRelease(reason)`（`DragDetector.cs:154`，public）與 `--cycle-test` / `--raise` 已覆蓋「升起／隱藏／watchdog」；但「mouse up 觸發 release」與「kill switch 真的被 OS 送到」這兩條語意目前只有真實輸入能走，而且**沒有任何 CLI 接縫**呼叫 `ForceRelease` |
| `tools\verify-m1a.ps1` | **S4.1–S4.8** | **真實 OLE 拖放** | `DragSim --drag`（`SendInput` + `DoDragDrop`），第 461 行 | **不行**：需要真實輸入（Q2 已證實私有桌面送不進去）。等價物只有行程內呼叫 `DropTarget.Drop(...)`，但沒有 CLI 接縫 |
| `tools\verify-m1a.ps1` | **S4a.1–S4a.2 / S4b.1–S4b.4** | **人工手勢（不是注入）** | 等操作者自己拖檔案（`Invoke-S4m`，第 501–550 行） | 同 S4。注意：這幾個是「人在迴路」的檢查，不是「搶滑鼠」的檢查 |
| `tools\verify-m1h.ps1` | **S16.55–S16.58** | **真實滑鼠點擊** | `[PillProbe]::Click` → `SendInput`（第 895、942 行） | **部分可以**：`--pill-selftest` 已經用 `WM_LBUTTONUP` 走真實 `WndProc`（S16.51 斷言的模式是 `pill: selftest done: checks=10 failed=0 ok=True`；本輪沒有執行它，只讀原始碼）。但 S16.55/S16.57 要證明的正是「**一顆真的 OS 滑鼠點擊**不會搶前景」，這條語意無法用合成訊息替代 |
| `tools\verify-m1h.ps1` | **S16.59 / S16.60** | **真實游標停駐（hover）** | `[PillProbe]::MoveTo`（第 986、1004、1027 行） | **不行**：hover 是 `WM_MOUSEMOVE` 的產物，`--pill-interactive` 只提供視窗，不會自己造 hover |
| `tools\verify-m1b.ps1` | — | 無 | 宣告了 `-DragSim` 參數（第 18 行）但**整支沒有用到**（死參數） | — |
| `tools\verify-m1c.ps1` | S…（`--wheel-demo` / `--wheel-dump`） | 無 | 離屏合成，只 `Start-Process` | — |
| `tools\verify-m1d.ps1` | 全部 | 無 | `Start-Process` + 讀日誌；`--wheel-payload` 走完整 UI 路徑但不需要滑鼠 | — |
| `tools\verify-m1f.ps1` | S14.x（`--settings-ui-selftest` / `-live` / `-dump`） | 無 | 走產品內部接縫：`SettingsUi.SelfTestSave / SelfTestCancel / SelfTestWindowPresent / SelfTestKillUiThread`；`--settings-ui-dump` 用 WPF `RenderTargetBitmap` 離屏渲染 | — |
| `tools\verify-m1g.ps1` | S…（托盤／autostart） | 無 | 只做唯讀觀測（`ForegroundInfo`、`WindowsOfClass`） | — |
| `tools\verify-orientation-matrix.ps1` | 全部 | 無 | `--convert`，純 CLI | — |

**產品已有的內部接縫（可以不碰真實游標走到同一條程式路徑）**：

- `--pill-selftest`：把 `WM_LBUTTONUP` 送進真實 `WndProc`（`verify-m1h` S16.51 已在用）。
- `--settings-ui-selftest` 家族：`SelfTestSave` 會**真的按下 Save 按鈕**（`SettingsUi.cs:432-500`），不經過滑鼠。
- `--cycle-test` / `--raise` / `--pill-interactive`：用計時器驅動狀態機，不需要輸入。
- `--wheel-demo --wheel-dump` / `--progress-dump-detail*` / `--settings-ui-dump`：全部是**離屏渲染**，不需要螢幕。
- `DragDetector.ForceRelease(reason)`（public，**無 CLI 接縫**）。
- `DropTarget.Drop(IDataObject, …)`（public COM 介面方法，**無 CLI 接縫**）。
- `GlassSurface.Synthetic("dark"|"light")`：可重現的背景底材，取代螢幕擷取。

---

## 5. 哪些檢查可以搬進私有桌面、哪些不行、為什麼

### 5.1 可以搬（而且真的有好處：不會在使用者桌面留下任何視窗）

- `verify-m1d`、`verify-m1g`、`verify-m1c`、`verify-orientation-matrix`：本來就不注入輸入，也不需要螢幕。
- `verify-m1f`：走 `SelfTest*` 接縫 + WPF 離屏渲染。
- `verify-m1h` 的 **S16.51–S16.54**（`--pill-selftest` 系列，離屏 dump）。
- `verify-m1a` 的 S1／S2／S3b／S5 等非注入段。
- 已量到的額外好處：私有桌面上開 GUI 行程（`charmap.exe`）在使用者桌面**新增 0 個視窗**，所以「彈窗類」的檢查也適合搬。

### 5.2 可以搬但有但書

- 任何**斷言螢幕像素**的檢查：私有桌面會失敗（Q1）。目前 `--pill-selftest-shot` 沒有被斷言，只是證據；`GlassSurface` 已有 fallback。**若未來有人把「畫面上真的有面板」升級成斷言，那條就絕對不能進私有桌面。** 替代方案是改用 `PrintWindow`（已量到可行），但只在我自繪的 GDI 視窗上驗過（見 §6）。

### 5.3 不能搬（這就是為什麼「模擬空間」救不了這件事）

- `verify-m1a` **S3.1–S3.11**：真滑鼠＋鍵盤。
- `verify-m1a` **S4.1–S4.8**：真 OLE 拖放。
- `verify-m1h` **S16.55–S16.60**：真點擊與 hover。

原因不是「私有桌面不夠好」，而是 **Windows 不讓非 input desktop 送出真實輸入**（Q2 實測 `SendInput=0 / ERROR_ACCESS_DENIED`）。在私有桌面裡，這三組檢查會變成：

- 斷言式（S3.1「有沒有被升起」）→ 會**失敗**（因為什麼都沒發生），不是假通過；
- 否定式（S4a.1「沒有任何東西被升起」）→ 會**假通過**，因為沒東西發生。

後者是比現況更糟的結果，所以不建議把這三組搬進去。

### 5.4 真正可行的三條路（本輪不實作）

1. **短期（不改產品）**：把會注入輸入的檢查標成「獨佔類」，跑之前先確認使用者閒置（或明確徵求同意），而不是設法隔離。本案的探針已經示範了「量測前的閒置窗口」怎麼寫。
2. **中期（要改產品）**：把已經存在的 public 接縫擴充成 CLI 面（例如 `--detector-force-release <reason>`、`--drop-target-selftest <file>`、`--pill-hover <ms>`），讓檢查走同一條程式路徑但不需要真實輸入。**代價是斷言語意改變**：從「OS 真的送進來」變成「我們假裝 OS 送了」，檢查名稱與報告必須明說，否則就是把一條真檢查換成一條假檢查。
3. **私有桌面的正確認知**：它適合當「**不干擾使用者桌面的視窗／行程沙箱**」（已量到：開行程、開視窗、收訊息層拖放、逐視窗 `PrintWindow` 取像素），**不適合當「真實輸入的沙箱」**。

---

## 6. 未驗證 / 不確定（誠實清單）

1. **沒有實測在私有桌面啟動 `explorer.exe`**（理由見 §3.3）。所以「能不能在那裡開檔案總管」這一題，我只回答了「那裡沒有殼層」＋「外來 GUI 行程可以開」，**沒有直接回答 Explorer 本體**。
2. **沒有測 `SwitchDesktop` 之後 `SendInput` 是否恢復**（刻意不做：會蓋掉使用者畫面並吃掉他的鍵鼠）。§2.3 的推論建立在「不切換桌面」的前提下。
3. **`SendInput` 回 0 / err=5 的機制**我判定是「非 input desktop 被拒」，但我沒有做跨 integrity level / 跨 session 的對照。最接近的對照（同一支程式在使用者桌面回 1）支持這個判定，但不是決定性證明。
4. **`PrintWindow` 只在自繪的 GDI 視窗上驗證過**。產品的 `SettingsWindow`（WPF/DWM 合成）與輪盤（layered window）是否也能在私有桌面被 `PrintWindow` 取到像素，**本輪沒測**。這是「能不能在私有桌面取到產品畫素」的關鍵落差，不能從我這次的結果外推。
5. **`WM_DROPFILES` 跨桌面成功，但沒有測 OLE `IDropTarget` 能不能被訊息驅動**——判斷是不行（需要真 `DoDragDrop`），但沒有量。
6. 私有桌面那份 2560x1080 的 PNG 是**空點陣圖**（A/B/C 三條路都拿不到畫面），不是「桌面真的是黑的」。不要把它當成「私有桌面是黑的」的證據。
7. 使用者桌面的像素值在我三次執行之間會變（他在用電腦），所以報告裡的像素數字只作「不等於 marker 洋紅」的用途。

---

## 7. 產出物與重跑方式

- 探針：`tools\probe-private-desktop.ps1`（新增；**純 ASCII**：`bytes=46980 nonAscii=0`；PowerShell 5.1 `parseErrors=0`）
- 報告：`docs\aegis\plans\2026-10-06-private-desktop-feasibility.md`（本檔）

```
cd C:\Users\User\Desktop\專案\tangerine-win
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe-private-desktop.ps1
```

可選參數：

- `-SkipInjection`：完全不做注入階段（若使用者正在用電腦，建議加上）
- `-KeepArtifacts`：保留暫存目錄（含四張擷取嘗試的 PNG 與 drop 日誌）
- `-DropTargetSeconds N`：drop 目標視窗的存活秒數（預設 12）
- `-WorkRoot <path>`：指定暫存目錄

探針自己會做的事：編譯內嵌 C# helper（`csc.exe`，不進 `src\`、不動產品建置）→ 建私有桌面 → 跑 Q1/Q2/Q3 → 殺掉自己啟動的 helper、`CloseDesktop`、刪掉暫存目錄。**不會在使用者桌面留下任何檔案**，也不會殺不屬於它的行程。

一次實際執行的完整輸出（第三次，決定性的一次，`elapsed: 13 s`）：

```
========================================================================
dsh probe: private desktop feasibility  (ASCII probe, no repo changes)
========================================================================
work dir   : C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b
desktop    : DshPdProbe_0d72d835
powerShell : 5.1.26100.9168
started    : 14:37:06.516
compiler   : csc C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe
helper exe : C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b\pd-helper.exe  (20480 bytes)
user desktop: Default  screen=2560x1080
user cursor : 1858,1059
user desktop pixels @200,150 / @100,75 / @300,225 / @390,290 = 0x00E3FC / 0x301707 / 0x4B2107 / 0x2F1708
CreateDesktop OK  handle=0xA28
windows on the fresh private desktop:

========================================================================
Q1  can a process on the private desktop capture THAT desktop?
========================================================================
  desktop=DshPdProbe_0d72d835
  screen=2560x1080
  t3457384437 GetCursorPos_ret=False readonly_cursor=0,0
  windows_before=(none)
  magenta_hwnd=0x9F40750
  t3457385359 after_pump windows=class=Touch Tooltip Window visible=False | class=UAC_InputIndicatorOverlayWnd visible=True | class=UAC Input Indicator visible=True | class=DshPdProbeMagentaWnd visible=True | class=CicLoaderWndClass visible=False | class=CiceroUIWndFrame visible=False | class=CiceroUIWndFrame visible=False | class=MSCTFIME UI visible=False | class=IME visible=False
  A_GetDC_NULL hdc=0xFFFFFFFFD901275A err=0
  A_GetPixel(200,150)=0xFFFFFFFF err=0 (0xFFFFFFFF=CLR_INVALID)
  A_BitBlt(src=screen DC of this desktop) ok=False err=6 (6=ERROR_INVALID_HANDLE)
  B_CreateDC(DISPLAY) hdc=0xC0128DA err=6
  B_GetDeviceCaps HORZRES=2560 VERTRES=1080
  B_BitBlt(src=DISPLAY DC) ok=False err=6
  C_CopyFromScreen threw=YES Win32Exception HResult=0x80004005 nativeError=6
  C_consequence=GlassSurface.CaptureFromScreen() returns null -> colour fallback; EvidenceScreenGrab() logs FAILED
  D_PrintWindow(PW_RENDERFULLCONTENT) ok=True err=6
  D_png=C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b\cap-D-printwindow.png pixel_100_75=0x00FF00FF
      grid_samples=144 nonblack=144 pct=100

  USER desktop at the same coordinates: 200,150=0x00E3FC  100,75=0x301707  300,225=0x4B2107  390,290=0x2F1708

  --- control: the SAME product call, executed from the USER desktop ---
  user desktop: GetPixel(200,150)=0x00E3FC   CopyFromScreen(200,150)=0x00E3FC   agree=True
  (the identical CopyFromScreen call therefore DOES return real pixels when the desktop is the input desktop)
  Q1 VERDICT: DESKTOP_CAPTURE_FAILS__PER_WINDOW_PRINTWINDOW_WORKS
    A GetDC(NULL)+BitBlt ok=False | B CreateDC(DISPLAY)+BitBlt ok=False | C CopyFromScreen threw=True | D PrintWindow ok=True magenta=True

========================================================================
Q2  does input issued on the private desktop reach the USER cursor?
========================================================================
  baseline: user cursor samples over 1 s = 1858,1059
  baseline: distinct positions = 1  (1 = the hand is off the mouse)
  --- control: the same two calls run from the USER desktop, aimed at the current cursor point (a no-op move) ---
  desktop=Default
  t3457386578 GetCursorPos_ret=True cursor=1858,1059
  t3457386578 SetCursorPos(no-op, same point) ret=True err=0 cursor=1858,1059
  t3457386578 SendInput(MOVE to the same point) ret=1 err=0 cursor=1858,1059
  RESULT=on the input desktop SetCursorPos=True SendInput=1 (1 = the event was accepted, 0 = refused)
  user cursor right after the control: 1858,1059  (a no-op move cannot change it)
  READ-ONLY evidence: the private desktop reports its own cursor ret=False pos=0,0 while the user desktop reports 1858,1059 at the same time
  targets: SetCursorPos -> 2520,1040   SendInput MOVE -> 40,40   (no button event is ever sent)
  --- helper log (running on the private desktop) ---
  desktop=DshPdProbe_0d72d835
  screen=2560x1080
  t3457386625 readonly_cursor=0,0
  target_setcursorpos=2520,1040
  target_sendinput=40,40
  t3457386687 go_seen
  t3457388187 p_before=0,0
  t3457388187 after_SetCursorPos ret=False err=5 cursor=0,0
  t3457388593 p_after_settle=0,0
  t3457388593 SendInput_move ret=0 err=5
  t3457389000 cursor_after_sendinput=0,0
  t3457389000 cursor_after_park=0,0
  --- user-desktop cursor samples (tick:position, only on change) ---
  3457386671:1858,1059

  user cursor before=1858,1059  after=1858,1059  distinct positions during the window=1
  samples that landed EXACTLY on the private-desktop targets: SetCursorPos target=0  SendInput target=0
  private-desktop cursor reached the SetCursorPos target: False ; reached the SendInput target: False
  Q2 VERDICT: USER_CURSOR_UNAFFECTED

========================================================================
Q3  can a shell / drop target live on the private desktop?
========================================================================
  user-desktop windows before: 945
  shell classes present on the private desktop BEFORE anything: (none)
  desktop=DshPdProbe_0d72d835
  drop_hwnd=0x9F80750
  window_class=DshPdProbeDropWnd rect=300,300 500x400
  windows_on_this_desktop=class=DshPdProbeDropWnd visible=True | class=CicLoaderWndClass visible=False | class=MSCTFIME UI visible=False | class=IME visible=False
  windows the PROBE sees on the private desktop handle: Touch Tooltip Window|pid=22008|vis=False , UAC_InputIndicatorOverlayWnd|pid=22008|vis=True , UAC Input Indicator|pid=22008|vis=True , DshPdProbeDropWnd|pid=22008|vis=True , CicLoaderWndClass|pid=22008|vis=False , CiceroUIWndFrame|pid=22008|vis=False , CiceroUIWndFrame|pid=22008|vis=False , MSCTFIME UI|pid=22008|vis=False , IME|pid=22008|vis=False
  feed: desktop=DshPdProbe_0d72d835
  feed: found hwnd=0x9F80750
  feed: t3457392890 PostMessage(WM_DROPFILES) ret=True err=0 hdrop=0x1BF10008
  feed: payload=C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b\drop-sample.txt
  cross-desktop post from the probe (user desktop -> window on the private desktop): posted ok hdrop=0x209CE870008
  --- drop target log ---
  drop log DshPdProbe_0d72d835
  t3457392906 WM_DROPFILES count=1 file[0]=C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b\drop-sample.txt
  t3457392937 WM_DROPFILES count=1 file[0]=C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b\drop-sample.txt
  drops received by the private-desktop window: 2 of 2 attempted (same-desktop feeder + cross-desktop post)
  foreign GUI test binary: C:\Windows\System32\charmap.exe
  private desktop windows while it runs: Touch Tooltip Window|pid=26620|vis=False , UAC_InputIndicatorOverlayWnd|pid=26620|vis=True , UAC Input Indicator|pid=26620|vis=True , tooltips_class32|pid=26620|vis=False , #32770|pid=26620|vis=False , ComboLBox|pid=26620|vis=False , ComboLBox|pid=26620|vis=False , ComboLBox|pid=26620|vis=False , CicLoaderWndClass|pid=26620|vis=False , CiceroUIWndFrame|pid=26620|vis=False , CiceroUIWndFrame|pid=26620|vis=False , IME|pid=26620|vis=False , MSCTFIME UI|pid=26620|vis=False
  NEW windows on the USER desktop: (none)
  user-desktop windows after the whole Q3 phase: 944 (new vs before: 0)
  Q3 VERDICT: DROP_TARGET_WINDOW_CREATED ; shell present on the private desktop: NO

========================================================================
cleanup
========================================================================
  CloseDesktop -> True
  removed C:\Users\User\AppData\Local\Temp\dsh-pd-probe-43862e0b -> still exists=False
  pd-helper processes left: 0
  elapsed: 13 s
```

第一次執行（14:33，使用者在動滑鼠的那一次）的 Q2 段，保留了「量到會干擾就跳過」的行為：

```
  baseline: user cursor samples over 1 s = 820,691 944,675 949,675 926,676 925,676
  baseline: distinct positions = 5  (1 = the hand is off the mouse)
  gate: the cursor moved during the 1 s baseline -> waiting up to 45 s for a still hand
  GATE: no still-hand window -> the injection stage is SKIPPED; only the programmatic evidence above is reported.
  Q2 VERDICT: SKIPPED_BY_GATE
```

---

## 8. 環境安全與清理紀錄

- 全程**沒有送出任何滑鼠按鍵事件**（無 `LEFTDOWN` / `LEFTUP`）。
- 唯一一次在使用者桌面上的輸入動作是對照組的**零位移 no-op**（把游標設到它當時所在的位置），前後座標不變。
- 沒有殺任何不屬於本探針的行程：只殺自己 `CreateProcess` 出來的 `pd-helper` 與 `charmap`（`Get-Process Confern` 全程為 0）。
- 沒有在使用者桌面留下任何檔案；暫存目錄跑完已刪除（`still exists=False`）。
- 對使用者桌面的唯讀存取：4 個像素的 `GetPixel`，以及一次 40x30 的 `CopyFromScreen` 到記憶體（只取樣 1 個像素、沒有寫檔）。
- 沒有修改任何既有 `.ps1` / `.cmd` / `src\**`；沒有 `git add` / `commit` / `reset --hard`。
