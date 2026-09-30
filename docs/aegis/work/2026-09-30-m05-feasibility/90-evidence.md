# M0.5 生死線 — 證據歸檔

- Date: `2026-09-30`
- 計畫：`docs/aegis/plans/2026-09-30-m05-feasibility.md`
- 規格：`docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` §4.1 / §4.2.2 / §9.2
- **判定：GO** — 允許進入 §4 實作

## 0. 環境

| 項目 | 值 |
|------|-----|
| OS | Windows 11 25H2 build 26200.9168（登錄檔 `ProductName` 仍寫 "Windows 10 Pro"，已知怪癖） |
| SDK | .NET SDK **8.0.425**（`winget install Microsoft.DotNet.SDK.8`，使用者已授權；安裝前本機只有 runtime） |
| 顯示 | 單一螢幕，虛擬螢幕 `(0,0) 2560×1080` |
| 原型 | `src/M05`（`net8.0-windows`，WinExe，原始 Win32 視窗，**不用 WPF**） |
| 驗證器 | `tools/dragsim`（結果：**不可用**，見 §4） |

建置：`dotnet build src/M05/M05.csproj -c Release` → `建置成功。0 個警告 0 個錯誤`

---

## 1. 核心命題與結果

> **命題**：全螢幕、topmost、`WS_EX_LAYERED` 且 `alpha = 1/255` 的視窗，能否被 OLE 拖放命中，並取得被拖曳檔案的清單？

**結果：可以。** 且**不需要 COM interop**——`DragAcceptFiles` + `WM_DROPFILES` 即足夠。

### 樣式實測（矩陣 #6）

```
overlay created hwnd=0x12E61A36 exstyle=0x8080098 WS_EX_TRANSPARENT=not set(OK) rect=(0,0,2560x1080) alpha=1
```

`0x8080098` 解碼：`WS_EX_NOACTIVATE`(0x08000000) + `WS_EX_LAYERED`(0x00080000) + `WS_EX_TOOLWINDOW`(0x00000080) + **`WS_EX_ACCEPTFILES`(0x00000010，由 `DragAcceptFiles` 自動加上)** + `WS_EX_TOPMOST`(0x00000008)。
**`WS_EX_TRANSPARENT`(0x20) 未被設起** —— 這是命題成立的前提條件，且已用程式在每次建立時檢查並寫入證據檔。

### 命中實測 — 生產值 `alpha = 1/255`（真實 Explorer 拖曳）

```
14:51:46.463 trigger key DOWN (state=Armed)
14:51:46.469 state Armed -> Capturing
14:51:46.478 overlay created ... alpha=1
14:51:46.479 overlay SHOWN (count=1)
14:51:49.967 release requested: mouse up
14:51:49.970 *** DROP RECEIVED: 1 file(s) ***
14:51:49.970     C:\Users\User\AppData\Local\Temp\m05\sample-drag.txt
14:51:49.971 overlay HIDDEN
...
14:51:53.881 overlay SHOWN (count=2)
14:51:56.199 release requested: mouse up
14:51:56.200 *** DROP RECEIVED: 1 file(s) ***
14:51:56.201 overlay HIDDEN
```

`overlay SHOWN=2  DROP RECEIVED=2  overlay HIDDEN=2` —— 兩次拖放全部命中，且拿到**正確的來源路徑**。

另以 `alpha=110` 的可見模式（`--alpha` 診斷旗標）由使用者目視確認：**升起範圍蓋滿整個畫面**。

```
14:42:29.887 state Armed -> Capturing
14:42:29.897 overlay SHOWN (count=1)
14:42:30.757 release requested: mouse up
14:42:31.020 overlay HIDDEN
14:42:33.037 state Armed -> Capturing
14:42:35.007 *** DROP RECEIVED: 1 file(s) ***
```
（該輪共 7 次 SHOWN，其中 4 次伴隨 `DROP RECEIVED`）

### 零侵入實測 — 未按觸發鍵（矩陣 #7）

使用者不按空白鍵、直接把 `sample-drag.txt` 拖到桌面：

```
14:52:18.467 state Idle -> Armed
14:52:19.321 state Armed -> Idle
14:52:20.837 state Idle -> Armed
14:52:20.917 state Armed -> Idle
14:52:23.547 state Idle -> Armed
14:52:23.653 state Armed -> Idle
```

**整段期間沒有任何 `overlay SHOWN`／`overlay created`**（接收層是**延遲建立**的，只在第一次進入 Capturing 時才 `CreateWindowExW`）。外部驗證：

```
桌面樣本 FOUND: C:\Users\User\Desktop\sample-drag.txt  (27 bytes)
來源 %TEMP%: 已被移走（符合同磁碟區預設行為）
```

→ 檔案由 Windows 原生執行「同磁碟區＝移動」，與未安裝時完全一致。

### 收層可靠性實測（矩陣 #5）

程式化 50 次 show/hide：

```
14:52:58.033 overlay SHOWN (count=50)
14:52:58.062 overlay HIDDEN
14:52:58.062 cycle test done: 50 cycles in 1555 ms; lastVisible=False (must be False)
M05 exit code: 0
```

外部驗證（另一個行程）：

```
FindWindowW(M05OverlayWindow) = 0            (視窗已徹底消失)
螢幕中心 (1280,540) 最上層視窗類別 = 'Chrome_RenderWidgetHostHWND'   (桌面未被遮擋)
```

另累積 **9 次真實拖曳循環**（alpha=110 七次 + alpha=1 兩次）全部乾淨收層，之後使用者仍能正常使用桌面。

### 狀態機實測

規格 §4.2 的 `Idle → Armed → Capturing → Idle` 全程正確，且守衛有效：

- `state Idle -> Armed` 只在**前景行程＝explorer.exe** 的左鍵按下時發生（log 中大量 Armed→Idle 未進入 Capturing，證明未按觸發鍵時不誤觸）。
- 觸發鍵放開即 `release requested: trigger released`（例：`14:43:12.615 trigger key UP (state=Capturing)`）。
- 前景判定實測有效：啟動後第一次 `Idle -> Armed` 緊接在 Explorer 視窗開啟後出現。

---

## 2. 實驗矩陣總表

| # | 情境 | 結果 | 證據位置 |
|---|------|------|----------|
| 1 | 桌面放手 | ✅ 通過 | §1 命中實測（放手點＝桌面空白處） |
| 2 | 檔案總管視窗內放手 | ⚠️ 未單獨測（同一命中機制；本機 `explorer` 為 foreground 的判定已驗證） | 殘餘項 R1 附帶 |
| 3 | 第二螢幕放手 | ⏭ **N/A** — 本機只有一個螢幕 | 殘餘項 R1 |
| 4 | 真實 Explorer 拖曳（人工） | ✅ 通過 | §1（共 6 次命中） |
| 5 | 收層可靠性 50 次循環 | ✅ 通過 | §1 |
| 6 | 樣式檢查（無 `WS_EX_TRANSPARENT`） | ✅ 通過 | §1 |
| 7 | 非觸發拖曳零侵入 | ✅ 通過 | §1 |
| 8 | IME 開啟時空白鍵 | ⚠️ 未做確定性測試（空白鍵在本機被 hook 正常捕捉） | 殘餘項 R3 |

---

## 3. 執行中發現並修正的缺陷

| # | 缺陷 | 症狀 | 修正 |
|---|------|------|------|
| D1 | `DragAcceptFiles` 被宣告在 `user32.dll` | `EntryPointNotFoundException`，接收層完全無法建立（第一次實驗看起來像「命題失敗」，其實是儀器壞掉） | 改為 `shell32.dll` |
| D2 | `mouse-up` 立即收層會與 shell 的 drop 解析搶時間 | 低階滑鼠 hook 比 shell 拖放迴圈**更早**看到 `mouse-up`；立即隱藏會讓放手當下的 drop 落到接收層**底下**，等於每次放手都退化成 §4.2.3 保底語意 | 改為 `HIDE_GRACE = 250 ms` 延遲收層；真正的 drop 一到就立刻收。已寫回規格 §4.2.2 |
| D3 | C# 區域變數在 lambda 之後宣告 | `CS0841`（無法在被宣告前使用區域變數） | 把 `watchdog` / `hideGrace` 移到事件處理常式之前宣告 |
| D4 | 自動化拖曳驗證器 `DragSim` 無效 | 見 §4 | 放棄自動化，改人工矩陣（計畫 Risk #3 命中） |

**D1 的教訓值得記住**：一個壞掉的儀器會產生一個看起來像是「假設被否證」的結果。若當時沒有跑對照組，就會錯誤地把整個 C+ 路線判死。

## 4. `DragSim` 自動化驗證器：失敗，已放棄

- 兩版手勢都無法產生真實 OLE 拖曳：v1（隱藏的來源視窗、直接呼叫 `DoDragDrop`）、v2（可見來源視窗 + 先位移再啟動 `DoDragDrop`、`GetAsyncKeyState(LBUTTON)=0x8001` 確認按鍵為壓下）。
- **對照組**（一個已知可正常接收 OLE 拖放的 WinForms 視窗，位於目標座標上）也**收不到 `DragEnter`**：`CONTROL RESULT` 從未出現，`drageffect=None`。
- 結論：問題在**合成拖曳器**，不在接收層命題。自動化路徑不可用；`--target` 對照組模式保留在程式碼中作為證據。
- **影響**：目前沒有自動化回歸，M1 每次都要人工拖曳驗收（殘餘項 R4）。

## 5. 對規格的實質影響（已寫回）

| 規格位置 | 變更 |
|----------|------|
| §4.1 | 標記為**已驗證**；把 `IDataObject` / `IDropTarget::DragOver` 的描述改為實測可用的 `DragAcceptFiles` + `WM_DROPFILES`；註明不需 `grfKeyState`（游標與觸發鍵來自兩個低階 hook） |
| §4.2.2 | 保險 #1／#2 由「立即隱藏」改為「延遲 `HIDE_GRACE`（250 ms）隱藏」，並記錄原因 |
| §9.2 | 由「待驗證」改為 **✅ 通過（GO）**，逐列填入實測結果，並新增 R1–R4 殘餘項 |

## 6. 未驗證／殘餘（不阻擋 §4 開工，但必須帶進 M1）

| # | 項目 | 處置 |
|---|------|------|
| R1 | 第二螢幕、Per-Monitor DPI | M1 第一切片：每螢幕一視窗 + PerMonitorV2 座標換算 |
| R2 | 保險 #4／#5（行程結束、緊急停用熱鍵）與 Wheel 階段收層 | M1 實作完整五層 |
| R3 | IME／注音下的觸發鍵 | M1 以「觸發鍵可自訂」為出口；上線前補矩陣 |
| R4 | 無自動化拖曳回歸 | 另尋方案（UI Automation 驅動真實滑鼠）或維持人工矩陣 |
| R5 | z-order 與系統拖曳殘影（矩陣 #2 項） | M1 繪製輪盤後判定 |
