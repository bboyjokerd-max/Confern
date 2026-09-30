# M1a 拖曳生命週期與五層硬保險 — 證據歸檔

- Date: `2026-09-30`
- 計畫：`docs/aegis/plans/2026-09-30-m1a-lifecycle.md`
- 規格：`docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` §4.2.2 / §9.2 / §9.3 / §13
- 交付對應：**A2／A6／A7／A13／A14**
- 跑者：`tools/verify-m1a.ps1`（純 ASCII；分段 S0–S5；`INCONCLUSIVE` 與 `FAIL` 分開，見 §6）
- 原始日誌：`docs/aegis/work/2026-09-30-m1a-lifecycle/logs/`

## 0. 判定

**PASS — 45/45 自動檢查通過**（`-Section All`，連續四次執行結果一致）。

M1a 交付物 = 生命週期（升起／收層）與五層硬保險。其中「收層」全部收斂到 `ReleaseGuard` 單一權威；
`HitLayer` **不再自己收層**（M0.5 原型的技術債已清除）。

兩項**未由自動化涵蓋**，理由與殘餘見 §5、§6：A7 的真實非觸發拖曳、A2 的來源／位置多樣性。

## 1. 環境

| 項目 | 值 |
|------|-----|
| OS | Windows 11 25H2 build 26200.9168 |
| SDK | .NET SDK 8.0.425 |
| 顯示 | 單一螢幕，虛擬螢幕 `(0,0) 2560×1080`（工作區 2560×1032） |
| 版本 | `Sidecar 0.1.0`（`AssemblyVersion` 會補成 `0.1.0.0`，故讀 `InformationalVersion`） |
| 建置 | `dotnet build src/Sidecar/Sidecar.csproj -c Release` → `0 個警告 0 個錯誤` |
| 合成拖曳器 | `tools/dragsim`（`--drag` 模式，見 §4） |

## 2. 檢查分佈

| 區段 | 內容 | 檢查數 | 結果 |
|------|------|--------|------|
| S0 | 非管理員（A14） | 2 | 全過 |
| S1 | 螢幕／視窗幾何／樣式／閒置不可見 | 11 | 全過 |
| S2 | 50 次程式化升降循環（A6） | 5 | 全過 |
| S3 | 合成生命週期 + 保險 #1／#2／#5 | 11 | 全過 |
| S3b | 保險 #3 看門狗（含診斷覆寫） | 5 | 全過 |
| S4 | 合成 OLE 拖放命中接收層（A2 機制） | 7 | 全過 |
| S5 | 強制結束 + 重啟乾淨（A13） | 4 | 全過 |

## 3. 關鍵證據（節錄自實際日誌）

樣式與幾何（S1；`EnumWindows` + `GetClassNameW` + `GetWindowRect` 直讀，非應用自述）：

```
hit#0 hwnd=0xB80586 exstyle=0x8080098 WS_EX_TRANSPARENT=not set(OK) rect=(0,0,2560x1080) alpha=1 visible=False
hit layer ready: 1 window(s), allHidden=True (must be True)
```

S1 同時以**作業系統視角**獨立斷言：每個螢幕恰好一個 `SidecarHitLayer` 視窗、幾何與螢幕一致、
閒置時 `IsWindowVisible=False`、`WS_EX_TRANSPARENT`(0x20) 未設起。這四項都不是讀應用自己的日誌。

50 次升降循環（S2，A6）：

```
cycle test done: 50 cycles; allHidden=True
hit layer disposed; leftoverWindows=0
```

生命週期與保險 #1／#2／#5（S3）——注意第二輪的 `Idle -> Armed` 證明狀態機**可重複**，不是一次性的：

```
state Idle -> Armed
trigger key DOWN (state=Armed, disabled=False)
state Armed -> Capturing
hit layer SHOWN (count=1, windows=1)
trigger key UP (state=Capturing, disabled=False)
release requested: trigger released
hit layer HIDDEN (250 ms grace elapsed)
release requested: mouse up
state Capturing -> Idle
state Idle -> Armed                    <-- 第二輪重新武裝
state Armed -> Capturing
hit layer SHOWN (count=2, windows=1)
release requested: mouse up
state Capturing -> Idle
hit layer HIDDEN (250 ms grace elapsed)
trigger key DOWN (state=Idle, disabled=True)     <-- 緊急停用後不再進入 Armed/Capturing
```

保險 #3 看門狗（S3b）：

```
guard #3 armed (30000 ms)              <-- 預設值確實是規格固定的 30 s
guard #3 armed (3000 ms)               <-- --watchdog-seconds 3 的診斷覆寫一律寫進證據
hit layer HIDDEN (guard #3 watchdog)
```

真實 OLE 拖放命中（S4，A2 的機制）：

```
hit layer SHOWN (count=1, windows=1)
guard #3 armed (120000 ms)
*** DROP RECEIVED on hit#0: 1 file(s) ***
hit layer HIDDEN (drop resolved)
```

S4 另斷言**來源檔案未被碰過**（SHA256 與 mtime 皆不變）——這是 M1a「不對被拖曳的檔案做任何檔案操作」
這條不變式的直接證據，也是規格「來源檔案唯讀」的第一次自動化守門。

強制結束與重啟（S5，A13）：

```
S5.1 raised before the kill                 PASS
S5.2 no hit window survives the kill        PASS   （以 OS 列舉斷言，非應用自述）
S5.3 restart works right after the kill     PASS
S5.4 new instance runs normally             PASS
```

## 4. 五層硬保險對照

| # | 保險 | 實作位置 | 驗證方式 | 結果 |
|---|------|----------|----------|------|
| #1 | 滑鼠放開即收層 | `DragDetector.WM_LBUTTONUP` → `ReleaseGuard` | 合成手勢 | 通過 |
| #2 | 觸發鍵放開即收層 | `DragDetector.OnKey` → `ReleaseGuard` | 合成手勢 | 通過 |
| #3 | 30 s 看門狗 | `ReleaseGuard` 計時器 → `WM_APP_FORCE_HIDE` | 實際等待逾時 | 通過（預設值與觸發皆驗） |
| #4 | 行程死亡 | 作業系統原生（不需程式碼） | S5 強制結束 | 通過 |
| #5 | 緊急停用 `Ctrl+Alt+Shift+Q` | `Hooks.KeyEvent` → `Detector.Disable()` | 合成按鍵 | 通過 |

保險 #3 的計時器在執行緒池執行緒上觸發，**不得跨執行緒呼叫 `ShowWindow`**；因此它以
`WM_APP_FORCE_HIDE`（wParam 帶原因碼）把收層交還 UI 執行緒。這條原本只是設計約束，現在有實際逾時證據。

## 5. 驗收條件對照

| 條件 | 原文 | M1a 狀態 | 證據 |
|------|------|----------|------|
| **A6** | 50 次循環後桌面與檔案總管完全可點擊 | **通過** | S2：50 次後 `allHidden=True`、無殘留視窗、行程結束後 OS 列舉為 0 |
| **A13** | 強制結束行程 → 重啟後無卡住的接收層、無殘檔、可正常運作 | **通過** | S5.1–S5.4 |
| **A14** | 全程以非管理員帳號執行即通過以上全部 | **通過** | S0.1 工作階段為 medium（`S-1-16-8192`）；S0.2 應用行程權杖完整性 RID = `0x2000`（medium，非提權）。原以「exe 旁有 `.manifest`」為檢查方式是**錯的儀器**——清單是被內嵌成 Win32 資源的，改為直接量測執行中行程 |
| **A2** | 在桌面、資料夾視窗、網路磁碟、第二螢幕放手 → 皆成功攔截並取得檔案清單 | **機制通過；位置多樣性未驗** | S4 以真實 OLE 拖放命中接收層並取得正確路徑。但本機只有單一螢幕、無網路磁碟，且接收層覆蓋整顆螢幕（位置差異不改變機制）→ 逐一位置驗證留給 M1f |
| **A7** | 不按觸發鍵的拖曳行為與未安裝時一致 | **未完成（需人工）** | 已排除「未觸發卻升起」：S1.7（閒置時 OS 認定不可見）+ S3（`Armed` 需左鍵按下、`Capturing` 需觸發鍵按著）。但「行為與未安裝時位元等價」需真實手勢比對，見 §6 |

## 6. 未驗證、殘餘與已知弱點

1. **A7 與 A2 位置多樣性的真實手勢驗證未執行**。跑者備有 `-Section S4m`（人工視窗：不按觸發鍵拖 3 次
   應毫無反應；按著觸發鍵拖 3 次應被攔截並立即收層），需要真人在前景為 explorer 時操作。
   **M1a 未取得這份證據，不得聲稱 A7 通過。**
2. **S3 需要 `--assume-explorer-foreground`**。自動化若必須先把 explorer 弄到前景，就得去最小化使用者的視窗，
   使測試穩定性取決於桌面上剛好有什麼（實測遇到 Telegram／Arc／Rainmeter 佔用前景）。該旗標停用
   「零侵入」的第一道保證，因此一律寫進證據、永不預設，且 S3 明確只驗生命週期、不驗前景前置條件。
   **代價：`IsExplorerForeground()` 這個述詞本身沒有自動化覆蓋**，只能靠 A7 的人工手勢。
3. **R4 的結論已被推翻**（下方 §7），但**真實 Explorer 當來源**的整條手勢鏈仍未自動化：
   合成拖曳的來源是我們自己的視窗，不是 explorer。要用合成輸入做出「explorer 當來源」，
   必須點在真實的桌面圖示上（或用 UI Automation 取得圖示座標）。
4. 第二螢幕、Per-Monitor DPI、網路磁碟於本機無法驗證（單螢幕、無共用）。
5. z-order 與 shell 拖曳影像的關係（殘餘 R5）在 M1a 未觸及，因為渲染層屬 M1b。

## 7. 推翻 M0.5 的結論（R4）

M0.5 曾判定：**「合成 `DoDragDrop` 無法產生真實拖曳」**，並因此把自動化拖曳驗證列為不可行。
該結論**是錯的**——失敗的是工具設計，不是平台限制。

- M0.5 的兩次嘗試分別從**計時器**與**其他執行緒**呼叫 `DoDragDrop`。
- M1a T5 的一次有界 spike（單一假設、單次執行）把 `DoDragDrop` 移到**同一個執行緒回應
  `WM_LBUTTONDOWN` 的處理常式內同步呼叫**，其餘手勢不變：

```
v3 spike: DoDragDrop called from inside WM_LBUTTONDOWN
WM_LBUTTONDOWN -> calling DoDragDrop synchronously
DoDragDrop returned: Copy
```

  對照組（已知可收拖放的 WinForms 視窗）同時收到：

```
CONTROL DragEnter
CONTROL DROP RECEIVED: C:\Users\User\AppData\Local\Temp\sidecar-verify\sample-drag.txt
CONTROL RESULT: PASS
```

因此：`tools/dragsim` **不是廢棄原型，而是可用的驗收資產**，並已整頓為只保留會動的機制
（`--drag` 用於合成拖放、`--target` 保留為對照組）。M0.5 的原始觀測保留在
`docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md` 的註記中，不改寫歷史。

## 8. 退休

| 項目 | 處置 | 理由 |
|------|------|------|
| `src/M05` | **刪除** | M0.5 的拋棄式原型；其結論已蒸餾進規格 §4.1／§4.2.2，程式碼不得演化為產品骨架 |
| `tools/run-m05-experiment.ps1` | **刪除** | 同上，M0.5 專用 |
| `tools/dragsim` | **保留並整頓** | §7：已證明可用，是 M1 之後的自動化回歸基礎 |
| `--raise-seconds` | 已於 T4 移除 | 由 `--watchdog-seconds` 取代，且強制寫入證據 |
