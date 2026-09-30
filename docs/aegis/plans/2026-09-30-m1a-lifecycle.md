# 實作計畫：M1a — 拖曳生命週期與硬保險完整化

- Date: `2026-09-30`
- 前置：**M0.5 生死線已通過（GO）** — `docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md`
- 路線圖：`docs/aegis/plans/2026-09-30-m1-roadmap.md`
- 權威範圍：規格 §3.1、§4.1、§4.2、§4.2.2、§3.3
- 切片性質：**第一個產品切片**。原型 `src/M05` 是拋棄式的，本計畫另建產品骨架

---

## Goal

讓接收層從 M0.5 的「能命中」變成可交付的**生命週期**：多螢幕正確、五層硬保險齊備、觸發鍵可設定，並且**在任何情況下都不會困住使用者**。

**明確不是本切片的事**：輪盤 UI（M1b）、編解碼（M1c）、寫檔契約與放手語意（M1d）、行程邊界（M1e）、設定頁（M1f）。

## 本切片的核心不變式（比功能更重要）

> **M1a 不得對被拖曳的檔案做任何寫入、搬移或更名。**
>
> 驗收方式：`src/Sidecar` 中**不得出現**任何檔案搬移／寫入被拖曳檔案的路徑——`File.Move` / `File.Copy` / `File.WriteAll*` / `SHFileOperation` / `IFileOperation` 只允許出現在 `Diagnostics/Evidence.cs` 對**本程式自己的記錄檔**的寫入。放手時的行為固定為：**記錄檔案清單 → 收層**。

## Architecture

```
Sidecar.exe（常駐；非管理員；無網路）
  ├─ Interop/Native.cs            P/Invoke 與常數唯一來源（M05 的移植 + 螢幕 API）
  ├─ Diagnostics/Evidence.cs      逐行證據 + 單行原地狀態（M05 移植，路徑改為產品路徑）
  ├─ Interaction/MonitorLayout.cs 螢幕列舉與座標查詢（新）
  ├─ Interaction/HitLayer.cs      每螢幕一個命中視窗（M05 Overlay 的一般化）
  ├─ Interaction/Hooks.cs         低階滑鼠／鍵盤 hook（M05 直接移植）
  ├─ Interaction/DragDetector.cs  狀態機（M05 移植 + 觸發鍵可設定）
  ├─ Interaction/ReleaseGuard.cs  五層硬保險的**唯一 owner**（新）
  └─ Program.cs                   組裝、命令列、訊息迴圈、單一實例守衛
```

### 架構凍結：兩層式覆蓋（本切片只實作命中層）

| 層 | 樣式 | 職責 | 本切片 |
|----|------|------|--------|
| **命中層** | 原始 Win32；`WS_EX_LAYERED\|TOOLWINDOW\|NOACTIVATE\|TOPMOST`；alpha = 1/255；**絕不加** `WS_EX_TRANSPARENT`；`DragAcceptFiles` | 被拖放命中、取得檔案清單 | **本切片實作** |
| **視覺層** | 獨立視窗；每像素 alpha；**必須** `WS_EX_TRANSPARENT`（可點穿，永不與命中層搶 drop） | 畫輪盤與自繪鬼影 | M1b |

這個切分**解掉了 M0.5 留下的 WPF 風險**：`AllowsTransparency=true` 的 WPF 視窗即使被加上 `WS_EX_TRANSPARENT` 也無所謂——因為那正是視覺層想要的行為；而命中層永遠是原始 Win32、樣式由我們完全掌控。**渲染技術（WPF 或 Direct2D）故意留給 M1b 決定**，本切片只凍結上面這條約束。

## Tech Stack

| 項目 | 值 |
|------|-----|
| .NET | 8.0（SDK 8.0.425 已安裝） |
| 專案 | `net8.0-windows`、`WinExe`、`x64`、`ApplicationManifest`；**不使用 WPF**（M1a 不需要繪圖，YAGNI） |
| 建置／執行 | `dotnet build src/Sidecar/Sidecar.csproj -c Release`；常駐執行**一律以背景工作啟動** |
| 測試 | 無單元測試（見 TDD Route）；驗收靠 `tools/verify-m1a.ps1` |

## Baseline / Authority Refs

| Ref | 角色 |
|-----|------|
| `docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` §4.1／§4.2／§4.2.2／§3.3 | 權威規格 |
| `docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md` | 已驗證的機制事實（含 `exstyle=0x8080098`、`HIDE_GRACE` 的必要性） |
| `src/M05/*` | **移植來源**（拋棄式原型；產品碼不得沿用其命名空間與實驗旗標設計） |
| `docs/aegis/plans/2026-09-30-m1-roadmap.md` | 切片邊界（本計畫含一項邊界修訂，見下） |

```text
BaselineUsageDraft:
- Required baseline refs: 上述四份
- Delivered context refs: 同左
- Acknowledged before plan refs: 規格 §4.1/§4.2/§4.2.2/§3.3、M0.5 證據、M05 原始碼、路線圖
- Cited in plan refs: Goal、Compatibility、Tasks T1–T6、Verification
- Missing refs: 無
- Decision: continue
```

## 路線圖邊界修訂（本計畫提出，需寫回路線圖）

原路線圖把規格 §4.2.3（未選格式放手的 Explorer 語意還原）列入 M1a。**本計畫建議移到 M1d**，理由：

1. §4.2.3 的執行必然要**動使用者的檔案**（移動／複製），與 M1d 的寫檔契約（§5）是同一類工作、同一組風險。
2. 讓**第一個產品切片**同時承擔「生命週期風險」與「資料損失風險」，違反一次只出清一類風險的原則。
3. M1a 的最強不變式是「完全沒有檔案操作」，這讓它能用最強的方式驗收（見上）。把 §4.2.3 加進來會讓這條不變式無法成立。

修訂後：**M1a 放手＝記錄 + 收層**；**M1d 改名為「放手語意與檔案契約」（§4.2.3 + §5）**，並在該切片實作 `IFileOperation`（Move/Copy）而非自行搬移——**讓 shell 自己執行它原本的語意**，這比手寫跨磁碟區／Ctrl／Shift 判斷更正確。

## Compatibility Boundary（不得破壞）

1. 不註冊 shell 擴充、不寫 `HKLM`、**以非管理員執行**（規格 §3.3、A14）。
2. 不按觸發鍵的拖曳：**位元級等價**（`overlay` 永不升起、被拖曳檔案不受影響）。
3. 被拖曳的檔案：**唯讀**（本切片完全不碰）。
4. 解除安裝後系統無殘留註冊項；本切片只寫 `%LOCALAPPDATA%\Sidecar\`（記錄檔）。
5. 接收層絕不在未捕捉時可見——**包含當機後**（視窗隨行程消失，A13）。

## TDD Route

```text
TDD Route:
- Mode: off
- Decision: skipped
- Strict authority: 使用者已就 M1c／M1d 明確授權 strict；**未涵蓋 M1a**
- Strict signals: 不適用
- Light eligibility: 不適用
- TDD-fit exception: 本切片的主要風險是「作業系統層的視窗生命週期」，單元測試無法覆蓋；
  可測的只有 MonitorLayout 的座標數學（價值低，且真值來自系統）
- Test posture: diagnostic reproduction + guided manual matrix
- Reason: 驗收對象是「真實拖曳下的行程行為」，只能以證據矩陣驗證
- Verification: tools/verify-m1a.ps1（自動斷言 + 引導人工手勢）→ 接受 A2／A6／A7／A13／A14
```

> 全域 `~/.config/aegis/config.toml` 的 `tdd_mode = "off"` **不修改**；Q4 是逐切片授權。

## Change Necessity

```text
Change Necessity:
- User-visible need: 拖曳中叫出輪盤、任意位置放手、且任何情況都不會困住桌面（§1.2、§4.2.2）
- No-change / non-code option: 無——M05 是原型：單一虛擬螢幕視窗、無多螢幕、無保險 #5、無可設定觸發鍵、含實驗旗標
- Why code change is necessary: 原型不可交付；產品骨架必須另建
- Minimum change boundary: 一個產品專案（8 檔）+ 一個驗收跑者
- Decision: code-change
```

## Files

| 動作 | 路徑 | 說明 |
|------|------|------|
| 建立 | `src/Sidecar/Sidecar.csproj` | 產品專案 |
| 建立 | `src/Sidecar/app.manifest` | PerMonitorV2 + `asInvoker`（A14） |
| 建立 | `src/Sidecar/Program.cs` | 組裝、命令列、訊息迴圈、單一實例 |
| 建立 | `src/Sidecar/Interop/Native.cs` | M05 移植 + 螢幕 API |
| 建立 | `src/Sidecar/Diagnostics/Evidence.cs` | M05 移植，路徑改產品路徑 |
| 建立 | `src/Sidecar/Interaction/MonitorLayout.cs` | 新 |
| 建立 | `src/Sidecar/Interaction/HitLayer.cs` | 由 M05 `Overlay.cs` 一般化 |
| 建立 | `src/Sidecar/Interaction/Hooks.cs` | M05 直接移植 |
| 建立 | `src/Sidecar/Interaction/DragDetector.cs` | M05 移植 + 觸發鍵可設定 |
| 建立 | `src/Sidecar/Interaction/ReleaseGuard.cs` | 新：五層保險唯一 owner |
| 建立 | `tools/verify-m1a.ps1` | 半自動驗收跑者（ASCII-only） |
| 建立 | `docs/aegis/work/2026-09-30-m1a/90-evidence.md` | 驗收證據 |
| 修改 | `docs/aegis/plans/2026-09-30-m1-roadmap.md` | 寫入邊界修訂與 Q6 |
| **刪除** | `src/M05/`、`tools/run-m05-experiment.ps1` | 退休邊界（T6） |
| **保留並整頓** | `tools/dragsim/` | T5 的 spike 推翻 M0.5 結論後改列驗收資產（見 90-evidence §7） |

## Existence Check

```text
Existence Check
- Proposed new surface: src/Sidecar（產品骨架）、tools/verify-m1a.ps1
- Existing owner / reuse candidate: src/M05（拋棄式原型）
- Why existing surface is insufficient: 原型是實驗設計（單一虛擬螢幕視窗、實驗旗標、無保險 #5、無多螢幕）；把它改成產品會把實驗債帶進產品
- Creation proof: M0.5 已驗證機制；本計畫把機制以產品契約重寫
- Entropy / retirement impact: 本切片完成時**刪除** src/M05 與 tools/run-m05-experiment.ps1（同一 PR）；不保留相容層。`tools/dragsim` 例外保留——T5 spike 證明它是可用的驗收資產，不是拋棄式原型
- Decision: add-with-proof
```

## Architecture Integrity Lens

- **不變式**：接收層只在 CAPTURING 期間可見；被拖曳檔案唯讀；非觸發拖曳零侵擾。
- **正規 owner**：`Native`＝唯一 P/Invoke 來源；`ReleaseGuard`＝**唯一**收層權威（M05 把收層邏輯散在 Program 的三個 timer 與事件處理常式中，這是原型債，本切片集中）；`HitLayer`＝視窗生命週期唯一擁有者；`MonitorLayout`＝唯一座標來源。
- **責任重疊**：M05 的 `Program.cs` 同時做組裝與保養邏輯 → 本切片把後者移出。
- **更高層 owner／契約**：五層保險本質是「單一釋放不變式」，因此由一個 owner 持有並以 `OnCaptured／OnReleaseRequested／OnDrop／OnKillSwitch` 四個入口收斂；`Program` 只負責接線。
- **退休／否證**：M05 在本切片結束時刪除；dragsim 經 T5 驗證後保留（結論反轉，見 T5 結果註記）。
- **Verdict**：通過。

## Plan Pressure Test

```text
Plan Pressure Test:
- Owner / contract / retirement: 集中收層權威；原型於本切片退休
- Architecture integrity / higher-level path: 兩層式覆蓋凍結約束、不凍結渲染技術；§4.2.3 移出以保住最強不變式
- Verification scope: 自動斷言（可自動的部分全部自動，含緊急熱鍵）＋引導人工手勢（只能人做的部分）＋殺行程（A13）
- Task executability: 新檔案全給完整程式碼；移植檔案給出精確變更點
- Pressure result: proceed
```

## Plan-Time Complexity Check

```text
Complexity Budget:
- Artifact class: 產品切片（第一個）
- Target files / artifacts: src/Sidecar 8 檔（約 900 行）+ tools/verify-m1a.ps1（約 200 行）
- Current pressure: 無（全新建）
- Projected post-change pressure: src/M05 與 tools/dragsim 刪除 → 淨增約 700 行
- Budget result: within-budget
- Planned governance: 若 M1a 完成後 src/Sidecar 超過 1200 行，M1b 必須先拆 Interaction 子資料夾再新增檔案

Plan-Time Complexity Check:
- Target files: 上述
- Existing size / shape signals: M05 共約 600 行、7 檔，已證明單一職責切分可行
- Owner fit: 每個檔案單一職責；ReleaseGuard 是把 M05 的散落邏輯收斂，不是新增抽象層
- Add-in-place risk: 低；**最大的誘惑是「順手把輪盤畫出來」**——那是 M1b，本切片不得引入任何繪圖
- Better file boundary: 維持 8 檔；不建立 Manager／Helper／Service
- Recommendation: add owner file
```

---

## Tasks

### T1 — 產品骨架與建置

**Files**：`src/Sidecar/Sidecar.csproj`、`src/Sidecar/app.manifest`、`src/Sidecar/Diagnostics/Evidence.cs`、`src/Sidecar/Program.cs`（骨架）

**Steps**

1. `src/Sidecar/Sidecar.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12.0</LangVersion>
    <AssemblyName>Sidecar</AssemblyName>
    <RootNamespace>Sidecar</RootNamespace>
    <StartupObject>Sidecar.Program</StartupObject>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

2. `src/Sidecar/app.manifest`（與 M05 的關鍵差異：**明示 `asInvoker`**，對應 A14）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="0.1.0.0" name="Sidecar.app" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

3. `src/Sidecar/Diagnostics/Evidence.cs`：**移植 `src/M05/Evidence.cs`**，三處變更：
   - 預設路徑改為 `%LOCALAPPDATA%\Sidecar\logs\sidecar.log`（可由 `--log <path>` 覆寫）。
   - 增加 `public string Path { get; }`（已於 M05 存在，保留）。
   - 移除 `_quiet` 的 console 分支依賴（WinExe 下為 no-op），改為保留簽章以免呼叫端改動。
   - 保留「`Line()` 逐行寫檔、`Status()` 單行原地覆寫」的分離（使用者輸出慣例）。

4. `src/Sidecar/Program.cs`：**完整命令列介面先凍結**（驗收跑者依賴它）：

```csharp
namespace Sidecar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);

        if (opt.ShowVersion)
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        }

        // 單一實例守衛（A13：被強制結束後必須能立刻重啟）
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Sidecar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Console.Error.WriteLine("Sidecar is already running.");
            return 3;
        }

        return App.Run(opt);
    }
}

internal sealed class Options
{
    public bool ShowVersion;
    public bool DumpMonitors;
    public bool ForceRaiseHitLayer;
    public string? LogPath;
    public uint TriggerKey = 0x20;      // VK_SPACE（Q1：預設空白鍵、可設定）
    public int CycleTest;
    public bool Quiet;

    public static Options Parse(string[] args) { /* --version --dump-monitors --raise --log --trigger-key --cycle-test --quiet --watchdog-seconds --assume-explorer-foreground */ }
}
```

> `Options.Parse` 支援 `--trigger-key` 接受 `0xNN` 或 `VK_*` 名稱（例如 `VK_SPACE`、`VK_CAPITAL`、`VK_TAB`）。設定頁（M1f）之後寫入同一個值，因此本切片**不得**把 `VK_SPACE` 硬編碼進 `DragDetector`。

5. `App.Run(opt)` 本步驟先做骨架：建立 `Evidence`、若 `--dump-monitors` 則列印並結束、否則跑訊息迴圈。

**Verification**
```powershell
dotnet build src/Sidecar/Sidecar.csproj -c Release
dotnet run --project src/Sidecar -c Release -- --version
```
**預期**：`建置成功。0 個警告 0 個錯誤`；`--version` 印出 `0.1.0`；`%LOCALAPPDATA%\Sidecar\logs\sidecar.log` 被建立。

---

### T2 — 螢幕幾何（MonitorLayout）

**Files**：`src/Sidecar/Interop/Native.cs`（M05 移植 + 新增）、`src/Sidecar/Interaction/MonitorLayout.cs`

**Why**：M0.5 用**單一**視窗覆蓋整個虛擬螢幕。產品必須每螢幕一視窗，才能在混合 DPI 下正確定位輪盤（R1）。

**Steps**

1. `Native.cs`：移植 `src/M05/Native.cs` 全文，然後**新增**：

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

[DllImport("user32.dll")]
public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
[DllImport("user32.dll")]
public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
[DllImport("user32.dll")]
public static extern bool IsWindowVisible(IntPtr hWnd);

public const uint MONITOR_DEFAULTTONEAREST = 2;
public const uint MONITORINFOF_PRIMARY = 1;
```

2. `src/Sidecar/Interaction/MonitorLayout.cs`：

```csharp
using System.Runtime.InteropServices;

namespace Sidecar;

internal sealed record MonitorEntry(int Index, string Device, Native.RECT Bounds, Native.RECT Work, bool IsPrimary)
{
    public int Width => Bounds.Right - Bounds.Left;
    public int Height => Bounds.Bottom - Bounds.Top;
}

/// 唯一座標來源。所有幾何都取自系統，不得自行推算虛擬螢幕聯集。
internal static class MonitorLayout
{
    /// 列舉所有螢幕。程序為 PerMonitorV2，因此座標為**實體像素**。
    public static MonitorEntry[] Enumerate()
    {
        var list = new List<(IntPtr h, Native.MONITORINFOEX mi)>();
        Native.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var mi = new Native.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (Native.GetMonitorInfoW(hMon, ref mi)) list.Add((hMon, mi));
            return true;   // 繼續列舉
        };
        if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
            throw new InvalidOperationException("EnumDisplayMonitors failed: " + Marshal.GetLastWin32Error());
        GC.KeepAlive(proc);

        // 主螢幕排第一，其餘依左上角排序，確保 Index 穩定
        return list
            .Select(x => new
            {
                x.mi,
                Primary = (x.mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0
            })
            .OrderByDescending(x => x.Primary)
            .ThenBy(x => x.mi.rcMonitor.Left)
            .ThenBy(x => x.mi.rcMonitor.Top)
            .Select((x, i) => new MonitorEntry(i, x.mi.szDevice, x.mi.rcMonitor, x.mi.rcWork, x.Primary))
            .ToArray();
    }

    /// 游標所在的螢幕（找不到時回主螢幕）。
    public static MonitorEntry Nearest(int x, int y, MonitorEntry[] all)
    {
        var pt = new Native.POINT { X = x, Y = y };
        IntPtr h = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>(),
            szDevice = string.Empty
        };
        if (h != IntPtr.Zero && Native.GetMonitorInfoW(h, ref mi))
        {
            var hit = all.FirstOrDefault(m => m.Device == mi.szDevice);
            if (hit is not null) return hit;
        }
        return all.First(m => m.IsPrimary);
    }
}
```

3. 在 `App.Run` 接上 `--dump-monitors`：

```csharp
if (opt.DumpMonitors)
{
    foreach (var m in MonitorLayout.Enumerate())
        evidence.Line($"monitor#{m.Index} {m.Device} bounds=({m.Bounds.Left},{m.Bounds.Top},{m.Width}x{m.Height}) " +
                      $"work=({m.Work.Left},{m.Work.Top},{m.Work.Right - m.Work.Left}x{m.Work.Bottom - m.Work.Top}) " +
                      $"primary={m.IsPrimary}");
    return 0;
}
```

**Verification**
```powershell
dotnet run --project src/Sidecar -c Release -- --dump-monitors
```
**預期**：每個螢幕一行；`primary=True` 恰有一個；`bounds` 與「設定 → 顯示器」的解析度一致。

> ⚠️ **本機只有一個螢幕**：這一步在驗收上只能證明「單螢幕正確」。多螢幕與混合 DPI 仍是 R1，**需有第二螢幕的機器**才能關閉。跑者必須把這列為「未驗證」而非「通過」。

---

### T3 — 命中層（每螢幕一視窗，預先建立且隱藏）

**Files**：`src/Sidecar/Interaction/HitLayer.cs`

**Why**：M0.5 的 `Overlay.cs` 已經被實測驗證，這裡只做兩件必要的一般化：**每螢幕一視窗**、以及**啟動時預先建立但保持隱藏**（規格 §9.3 要求升起 < 30 ms；隱藏視窗不被 `WindowFromPoint` 命中，因此不影響 A7）。

**Steps**

1. `src/Sidecar/Interaction/HitLayer.cs`：以 `src/M05/Overlay.cs` 為基礎，變更如下：
   - 建構子接受 `MonitorEntry[]`，為**每個**螢幕建立一個視窗（`ClassName` 固定 `SidecarHitLayer`；`CreateWindowExW` 使用該螢幕的 `Bounds`）。
   - `EnsureCreated()` 在**啟動時**呼叫一次；視窗建立後立即 `ShowWindow(SW_HIDE)`，且**保留不銷毀**。
   - `ShowAll()` / `HideAll()`：對所有視窗批次 `SetWindowPos(..., SWP_SHOWWINDOW|SWP_NOACTIVATE)` / `ShowWindow(SW_HIDE)`。
   - `IsVisible`：以 `Native.IsWindowVisible` 對**所有**視窗的結果聚合（而非自記旗標）——這樣「收層」的斷言是對**作業系統真實狀態**。
   - `FilesDropped` 事件：任一視窗收到 `WM_DROPFILES` 時觸發，附帶**該視窗所屬螢幕索引**（M1b 需要知道輪盤畫在哪個螢幕）。
   - 每個視窗建立後，**逐視窗**斷言延伸樣式：

```csharp
long ex = Native.GetWindowLongW(hwnd, Native.GwlExStyle);
bool bad = (ex & Native.WsExTransparentBit) != 0;
_evidence.Line($"hit#{index} hwnd=0x{hwnd.ToInt64():X} exstyle=0x{ex:X} " +
               $"WS_EX_TRANSPARENT={(bad ? "SET(FATAL)" : "not set(OK)")} " +
               $"rect=({b.Left},{b.Top},{b.Right - b.Left}x{b.Bottom - b.Top}) alpha={_alpha}");
if (bad) throw new InvalidOperationException($"hit layer #{index} has WS_EX_TRANSPARENT — mechanism is dead");
```

   - **`WS_EX_TRANSPARENT` 存在時直接丟例外**（M05 只記錄；產品必須讓它不可能被忽略）。
   - `WM_DROPFILES` 處理：`DragQueryFileW` 取清單 → 記錄 → `DragFinish` → 觸發事件。**不得有任何檔案操作**（本切片不變式）。
   - 保留 `RequestHide()`（跨執行緒以 `PostMessageW(WM_APP_FORCE_HIDE)` 收層）。

2. 接上 `--raise`（診斷用，等同 M05 的 `--force-overlay`）與 `--cycle-test N`（對**所有**視窗做 N 次 show/hide，最後斷言 `IsVisible == false` 並以 exit code 表達）。

**Verification**
```powershell
dotnet build src/Sidecar/Sidecar.csproj -c Release
dotnet run --project src/Sidecar -c Release -- --raise            # 背景工作啟動
dotnet run --project src/Sidecar -c Release -- --cycle-test 50
```
**預期（log）**：
```
hit#0 hwnd=0x... exstyle=0x8080098 WS_EX_TRANSPARENT=not set(OK) rect=(0,0,2560x1080) alpha=1
cycle test done: 50 cycles; allHidden=True (must be True)
```
exit code `0`。**另需程式化斷言**：閒置（未捕捉）時 `IsWindowVisible` 對所有命中視窗皆為 false。

---

### T4 — 生命週期與五層硬保險（單一 owner）

**Files**：`src/Sidecar/Interaction/Hooks.cs`、`DragDetector.cs`、`ReleaseGuard.cs`、`Program.cs`（接線）

**Why**：M05 把收層散在 `Program.cs` 的三個 timer 與事件處理常式中。產品必須**只有一個**收層權威，否則任何新增路徑都可能繞過保險——而這一層失效會癱瘓桌面。

**Steps**

1. `Hooks.cs`：**直接移植** `src/M05/Hooks.cs`（改命名空間）。不變更行為。
2. `DragDetector.cs`：移植，兩處變更：
   - `TriggerKey` 由建構子注入（**不得**有 `= VK_SPACE` 的硬編碼預設以外的判斷邏輯；預設值只作為 CLI 未指定時的 fallback）。
   - `ReleaseRequested` 改名為 `Released`，且**只回報意圖**（reason 字串），不呼叫任何視窗 API——收層一律經 `ReleaseGuard`。
   - 新增 `public void Disable()` / `public bool IsDisabled`，供緊急停用使用。
3. `src/Sidecar/Interaction/ReleaseGuard.cs`（**新，唯一收層權威**）：

```csharp
namespace Sidecar;

/// 規格 §4.2.2 的五層硬保險，唯一 owner。
/// 任何收層路徑都必須經過這裡；HitLayer 只能被本類別呼叫 Show/Hide。
internal sealed class ReleaseGuard : IDisposable
{
    /// 收層寬限（M0.5 實測發現）：低階滑鼠 hook 比 shell 拖放迴圈更早看到 mouse-up，
    /// 立即收層會讓放手當下的 drop 落到接收層底下。
    public const int HideGraceMs = 250;
    public const int VisibleTimeoutMs = 30_000;

    private readonly Evidence _evidence;
    private readonly HitLayer _hit;
    private readonly System.Timers.Timer _grace;
    private readonly System.Timers.Timer _watchdog;

    public bool CaptureDisabled { get; private set; }

    public ReleaseGuard(Evidence evidence, HitLayer hit)
    {
        _evidence = evidence;
        _hit = hit;
        _grace = new System.Timers.Timer(HideGraceMs) { AutoReset = false };
        _watchdog = new System.Timers.Timer(VisibleTimeoutMs) { AutoReset = false };
        _grace.Elapsed += (_, _) => HideNow("grace elapsed");
        _watchdog.Elapsed += (_, _) => { if (_hit.IsVisible) HideNow($"guard #3: visible > {VisibleTimeoutMs} ms"); };
    }

    /// 捕捉開始：升起並重置保險
    public void OnCaptured()
    {
        if (CaptureDisabled) { _evidence.Line("capture disabled — refusing to raise"); return; }
        _hit.ShowAll();
        _grace.Stop();
        _watchdog.Stop();
        _watchdog.Start();
        _evidence.Line($"hit layer SHOWN (guard #3 armed, {VisibleTimeoutMs} ms)");
    }

    /// 保險 #1／#2：mouse-up 或觸發鍵放開 → 寬限後收層
    public void OnReleaseRequested(string reason)
    {
        _evidence.Line($"release requested: {reason}");
        _watchdog.Stop();
        _grace.Stop();
        _grace.Start();
    }

    /// drop 已由 shell 解析完成 → 立即收層（不寬限）
    public void OnDrop()
    {
        _evidence.Line("drop resolved -> immediate hide");
        HideNow("drop");
    }

    /// 保險 #5：緊急停用熱鍵（Ctrl+Alt+Shift+Q）
    public void OnKillSwitch()
    {
        CaptureDisabled = true;
        _evidence.Line("*** KILL SWITCH: capture disabled until restart ***");
        HideNow("kill switch");
    }

    private void HideNow(string reason)
    {
        _watchdog.Stop();
        _grace.Stop();
        if (!_hit.IsVisible) return;                 // 以 OS 真實狀態判斷
        _hit.HideAll();
        _evidence.Line($"hit layer HIDDEN ({reason}); allHidden={!_hit.IsVisible}");
    }

    public void Dispose() { _watchdog.Dispose(); _grace.Dispose(); }
}
```

4. `Program.cs` 接線（**收層只從這裡流向 `ReleaseGuard`**）：

```csharp
using var evidence = new Evidence(opt.LogPath ?? DefaultLogPath(), opt.Quiet);
using var hit = new HitLayer(evidence, Monitors, alpha: 1);
using var hooks = new Hooks();
using var guard = new ReleaseGuard(evidence, hit);
var detector = new DragDetector(evidence) { TriggerKey = opt.TriggerKey };

hit.FilesDropped += (index, paths) =>
{
    evidence.Line($"*** DROP RECEIVED on hit#{index}: {paths.Length} file(s) ***");
    foreach (var p in paths) evidence.Line($"    {p}");
    evidence.Status($"DROP: {paths.Length} file(s) — M1a records only, no file operation");
    guard.OnDrop();                                   // 立即收層
};

detector.Captured += guard.OnCaptured;
detector.Released += guard.OnReleaseRequested;        // 保險 #1／#2

hooks.MouseEvent += detector.OnMouse;
hooks.KeyEvent += (vk, down) =>
{
    if (down && vk == Native.VK_Q && IsCtrlAltShiftDown())
    {
        guard.OnKillSwitch();                         // 保險 #5
        detector.Disable();
        return;
    }
    detector.OnKey(vk, down);
};

hit.EnsureCreated();                                  // 預先建立、保持隱藏
if (!opt.ForceRaiseHitLayer) hooks.Install(evidence);
else { evidence.Line("--raise: hooks NOT installed"); guard.OnCaptured(); }
```

其中 `IsCtrlAltShiftDown()` 以 `GetAsyncKeyState(VK_CONTROL|VK_MENU|VK_SHIFT)` 的高位判斷（三個都按著才算）。

> **保險 #4（行程結束／當機）不需要程式碼**：視窗屬於行程，行程消失視窗即消失。它以 A13 驗收（T6），不是靠實作。

**Verification**（本任務需要人工手勢）
```powershell
dotnet build src/Sidecar/Sidecar.csproj -c Release
dotnet run --project src/Sidecar -c Release        # 背景工作啟動
```
1. 拖曳時按住觸發鍵並放手 → log 應出現 `DROP RECEIVED` 後 `hit layer HIDDEN (drop)`。
2. 觸發後放開觸發鍵（不放手）→ `release requested: trigger released` → 寬限後 `HIDDEN`。
3. 觸發後按 `Ctrl+Alt+Shift+Q` → `*** KILL SWITCH ***`，之後**任何拖曳都不再升起**（log 出現 `capture disabled — refusing to raise`）。

---

### T5 — 驗收跑者（半自動）與 R4 的一次有界嘗試

**Files**：`tools/verify-m1a.ps1`

**Why**：M0.5 認為合成拖曳不可用，但**只有拖曳手勢**需要人。其餘（啟動、螢幕、樣式、50 循環、緊急熱鍵、殺行程、非管理員）都能自動斷言。把「手勢」與「判定」分開，人工只負責動手。

> **結果（T5 執行後修正）**：M0.5 的前提是錯的。T5 的有界 spike 證明合成 OLE 拖曳**可用**
> （`DoDragDrop` 必須在同執行緒的 `WM_LBUTTONDOWN` 處理常式內同步呼叫），因此跑者升級為
> **`tools/verify-m1a.ps1`：43+ 項全自動檢查**，其中 S4 以真實拖放命中接收層。人工區段
> （`-Section S4m`）只保留 A7 與 A2 的位置多樣性。詳見 `90-evidence.md` §7。

**Steps**

1. `tools/verify-m1a.ps1`（**ASCII-only**，全部參數化），四個區段：
   - **S1 自動**：建置 → `--dump-monitors` → 斷言每視窗 `WS_EX_TRANSPARENT=not set(OK)`、`primary=True` 恰一個 → 閒置時所有命中視窗 `IsWindowVisible=false`（以 `FindWindowExW` 列舉類別 `SidecarHitLayer` 後逐個查）。
   - **S2 自動**：`--cycle-test 50` → exit code 0 且 log 出現 `allHidden=True`。
   - **S3 自動**：以 `SendInput` 送 `Ctrl+Alt+Shift+Q` → 斷言 `KILL SWITCH` 且之後 `--raise` 無效（`refusing to raise`）。
   - **S4 人工（引導）**：依序提示使用者完成三個手勢，每個手勢後**自動斷言 log**：
     | 手勢 | 斷言 |
     |------|------|
     | 拖檔 + 按住觸發鍵 + 在桌面放手 | `DROP RECEIVED` 出現且路徑等於樣本檔；之後 `HIDDEN (drop)` |
     | 拖檔但不按觸發鍵 | **不得**出現任何 `hit layer SHOWN`（A7）；樣本檔行為由使用者目視確認 |
     | 觸發後不放手、直接殺行程（工作管理員）→ 重啟 | 重啟後立刻 `--dump-monitors` 成功、且無殘留視窗（A13） |
   - 全部結束後輸出 `PASS/FAIL` 摘要，並把 log 複製到 `docs/aegis/work/2026-09-30-m1a/`。

2. **R4 的一次有界嘗試**（時間盒：一次實作 + 一次執行，失敗即停）：把 M0.5 的失敗原因收斂到一個假設——「`DoDragDrop` 必須在**同一執行緒**回應 `WM_LBUTTONDOWN` 時呼叫」。寫一個 60 行的 spike：視窗收到 `WM_LBUTTONDOWN` 後**在同一處理常式內**呼叫 `DoDragDrop`，其餘手勢不變。
   - **成功** → 把自動拖曳併入 S4 並在證據中記錄。
   - **失敗** → 在證據中記錄「已排除的假設」，並將 R4 的處置改為「人工矩陣」（Q6 預設值），**不再繼續投資**。

**Verification**
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-m1a.ps1 -ProjectRoot "C:\Users\User\Desktop\專案\tangerine-win"
```
**預期**：S1–S3 全 PASS；S4 由使用者完成後全 PASS；摘要 `M1a PASS (A2, A6, A7, A13, A14)`。

---

### T6 — 驗收、證據歸檔與原型退休

**Files**：`docs/aegis/work/2026-09-30-m1a-lifecycle/90-evidence.md`、刪除 `src/M05`、`tools/run-m05-experiment.ps1`、修改路線圖

**Steps**

1. 對照規格 §13 逐項填寫（**未驗證項必須寫「未驗證」，不得寫「通過」**）：

| 驗收 | 條件 | 本切片 |
|------|------|--------|
| A2 | 桌面／資料夾／網路磁碟／第二螢幕放手皆攔截成功 | 桌面 ✅；資料夾 ✅；**第二螢幕與網路磁碟＝未驗證（R1）** |
| A6 | 50 次循環後桌面完全可點擊 | ✅（S2 + S4） |
| A7 | 未按觸發鍵的拖曳與未安裝時一致 | ✅（S1 + S4 手勢） |
| A13 | 強制結束後重啟無卡層、無殘檔 | ✅（S4） |
| A14 | 非管理員帳號通過 | ✅（manifest `asInvoker` + 以當前一般使用者執行） |
| A1／A3／A4／A5／A8–A12 | — | M1b／M1c／M1d／M1f |

2. **刪除原型**：`git rm -r src/M05`、`git rm tools/run-m05-experiment.ps1`（Existence Check 的退休承諾）。**`tools/dragsim` 不刪**——T5 spike 已證明它是可用的合成拖曳器，且 `verify-m1a.ps1` 的 S4 依賴它。刪除後 `dotnet build` 必須仍成功，且 `verify-m1a.ps1` 不得引用任何 M05 路徑。
3. 把「路線圖邊界修訂」與 Q6 寫回 `2026-09-30-m1-roadmap.md`。

**Verification**
```powershell
python "C:/Users/User/.dsh/profiles/web/node_modules/aegis/scripts/aegis-workspace.py" check --root "C:\Users\User\Desktop\專案\tangerine-win"
dotnet build src/Sidecar/Sidecar.csproj -c Release
```
**預期**：`Aegis workspace check passed`；`建置成功`；`git status` 乾淨。

---

## Execution Readiness View

```text
Execution Readiness View:
- Intent Lock: 把接收層從「能命中」做成可交付且絕不困住使用者的生命週期（規格 §4.2、§4.2.2）
- Scope Fence: 只做 T1–T6；不畫輪盤（M1b）、不編解碼（M1c）、不碰檔案（M1d）、不做設定頁（M1f）
- Baseline Lock: 規格 §4.1/§4.2/§4.2.2/§3.3 + M0.5 證據 + M05 原型碼 + 路線圖
- Approved Behavior: 觸發鍵預設空白鍵且可設定（Q1）；放手語意移至 M1d（本切片放手＝記錄＋收層）；非管理員（A14）
- Owner / Contract Constraints: Native＝唯一 P/Invoke；ReleaseGuard＝唯一收層權威；HitLayer＝視窗唯一擁有者；MonitorLayout＝唯一座標來源
- Compatibility Boundary: 不裝 shell 擴充、不寫 HKLM、非管理員、被拖曳檔案唯讀、非觸發拖曳位元級等價
- Retirement Boundary: 本切片結束時刪除 src/M05 與 tools/run-m05-experiment.ps1（dragsim 保留為驗收資產）；產品碼不得沿用原型命名空間與實驗旗標
- Task Batches: T1 骨架 → T2 螢幕 → T3 命中層 → T4 生命週期 → T5 跑者 → T6 驗收與退休
- Test Obligations: TDD Route=off（strict 僅授權 M1c／M1d）；驗收＝verify-m1a.ps1 的 S1–S4
- Review Gates: T4 完成後需人工手勢確認；T6 的 A2「未驗證項」需使用者知悉
- Drift / Rewind Rules: 若為通過驗收而引入檔案操作或繪圖 → 立即停止並回到切片邊界
- Evidence Required Before Completion: 跑者摘要 PASS、完整 log、A2 的未驗證聲明、原型刪除後的建置成功
- Advisory Boundary: 本視圖為方法包執行指引，非 GateDecision、非完成授權
```

## Risks

| # | 風險 | 機率 | 影響 | 對策 |
|---|------|------|------|------|
| 1 | 預先建立的隱藏視窗仍被拖放命中（破壞 A7） | 低 | 零侵入不成立 | S1 直接斷言 `IsWindowVisible=false`；S4 手勢實測。若成立 → 退回「延遲建立」（M0.5 已測 9 ms，仍符合 §9.3） |
| 2 | 多螢幕／混合 DPI 未驗證卻被當成通過 | 中 | 多螢幕使用者體驗錯誤 | T6 明文寫「未驗證」；禁止以單螢幕結果推論 |
| 3 | 緊急熱鍵被其他程式佔用 | 低 | 保險 #5 失效 | 本機制走自己的鍵盤 hook，不依賴 `RegisterHotKey`，無佔用衝突 |
| 4 | 收層權威被繞過（有人在 `Program` 直接呼叫 `HitLayer.HideAll`） | 中 | 保險形同虛設 | `HitLayer.HideAll` 僅允許 `ReleaseGuard` 呼叫；以程式碼審查檢查，並在 `ReleaseGuard` 註解載明 |
| 5 | R4 spike 又失敗而拖垮時程 | 中 | 工時浪費 | 已設時間盒（一次實作 + 一次執行），失敗即記錄並停 |
| 6 | 刪除 `src/M05` 後才發現需要它的某段程式 | 低 | 短暫返工 | M05 仍在 git 歷史中；必要時 `git show` 取回，不得恢復成常駐目錄 |

## Retirement

- **本切片內執行**：`src/M05/`、`tools/run-m05-experiment.ps1` 於 T6 刪除；`tools/dragsim/` 保留（T5 反轉）。
- **未來退休**：`--raise` 與 `--cycle-test` 是診斷旗標，M1f 交付前必須移除或收進「診斷模式」；`HitLayer` 的 alpha 參數在 M1b 之後固定為 1，不得作為使用者選項。

## Self-Review

| 檢查 | 結果 |
|------|------|
| 規格覆蓋 | §4.1（T3）／§4.2（T4）／§4.2.2 五層（T4；#4 由 OS 天然滿足，以 A13 驗收）／§3.3（T3、T6）／§9.3 升起時間（T3 預先建立） |
| 範圍外洩 | 輪盤、編解碼、寫檔、設定頁**均不在本切片**；§4.2.3 已明文移出並修訂路線圖 |
| Placeholder | 無 TBD；新檔案給完整程式碼，移植檔案給精確變更點 |
| 型別一致性 | `MonitorEntry`／`HitLayer`（`FilesDropped(int, string[])`）／`ReleaseGuard`／`DragDetector` 簽章在各任務間一致 |
| 相容性 | 非管理員、不寫 HKLM、被拖曳檔案唯讀（不變式＋驗收方式）、非觸發零侵入（S1＋S4） |
| Change necessity | 已記錄：原型不可交付，產品骨架必須另建 |
| 存在性 | add-with-proof；退休於同一 PR 執行 |
| 複雜度 | within-budget（新增約 900 行，淨增約 700 行）；最大誘惑（順手畫輪盤）已明文禁止 |
| 架構完整性 | 收層權威從 M05 的散落邏輯收斂為單一 owner；更高層契約（兩層式覆蓋）已凍結約束但保留 M1b 的渲染自由 |
| 驗證 | 全部為確切指令；自動部分不依賴人工；人工部分只承擔手勢 |
| ADR／雙軌 | 兩層式覆蓋應記為 ADR 訊號（M1b 開工前固化） |
