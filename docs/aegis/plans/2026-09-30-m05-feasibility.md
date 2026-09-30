# 實作計畫：M0.5 可行性驗證（拖曳攔截生死線）

- Date: `2026-09-30`
- 依據規格：`docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md`（**已通過使用者審閱**）
- 範圍：**只做生死線驗證**，不實作任何轉檔、輪盤、設定

---

## Goal

證明規格 §4.1 的核心假設成立：**一個全螢幕、topmost、`WS_EX_LAYERED` 且 alpha = 1/255 的視窗，能被 OLE 拖放命中，並取得被拖曳檔案的清單。**

**通過** → 允許進入 M1（輪盤 + 圖片轉檔）。
**任一失敗** → 退回路線 C（浮動接收目標），並回頭重新協商規格 §3.1 的「任意位置放手」。

## Architecture

```
M05.exe（本計畫的主角，原型）
  ├─ Native.cs        P/Invoke 與常數（單一來源）
  ├─ Overlay.cs       接收層視窗：建立/顯示/隱藏 + WM_DROPFILES
  ├─ Hooks.cs         低階滑鼠/鍵盤 hook（WH_MOUSE_LL / WH_KEYBOARD_LL）
  ├─ DragDetector.cs  規格 §4.2 的狀態機（IDLE→ARMED→CAPTURING）
  ├─ Evidence.cs      單行原地狀態輸出 + 逐行證據檔
  └─ Program.cs       訊息迴圈、命令列模式、實驗控制

DragSim.exe（自動化驗證器，獨立行程）
  └─ 以 OLE DoDragDrop 拖曳一個真實檔案，並用 SendInput 移動/放開滑鼠
```

**刻意不用 WPF**：`Window`／`AllowsTransparency` 會自行決定延伸樣式，可能加上致命的 `WS_EX_TRANSPARENT`；本計畫必須**完全掌控視窗樣式**，因此用原始 Win32 視窗。M1 才處理繪圖。

## Tech Stack

| 項目 | 版本／說明 |
|------|------------|
| .NET SDK | **8.0.x（尚未安裝，需使用者同意後安裝；Task 0）** |
| M05 | `net8.0-windows`，`WinExe`＋自建訊息迴圈，`AllowUnsafeBlocks` 不需要 |
| DragSim | `net8.0-windows`，`UseWindowsForms=true`（只為了 `DoDragDrop` 與 `DataObject`） |
| 建置 | `dotnet build -c Release` |
| 執行 | 常駐行程**一律用背景工作啟動，不得同步啟動**（本機既有鐵律） |

## Baseline / Authority Refs

| Ref | 角色 |
|-----|------|
| `docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` §4.1、§4.2、§9.2 | 權威規格；本計畫的驗收來源 |
| `00-teardown-and-windows-port.md` §4 路線比較 | 決策背景（為何不做路線 A） |
| `docs/aegis/BASELINE-GOVERNANCE.md` | 工作區治理 |

```text
BaselineUsageDraft:
- Required baseline refs: 上述三份
- Delivered context refs: 同左
- Acknowledged before plan refs: 規格 §4.1 / §4.2 / §9.2、teardown §4
- Cited in plan refs: Goal、Compatibility、Tasks 1–5、Verification
- Missing refs: 標的產品 App Store 截圖（取得與否不影響本計畫）
- Decision: continue
```

## Compatibility Boundary（本計畫不得破壞）

1. **不註冊任何全系統 shell 擴充**、不寫 `HKLM`、不需管理員。
2. **不影響未按觸發鍵的拖曳**：M05 的接收層只在使用者按住空白鍵時存在。
3. 原型**只讀取**檔案清單，**不寫入、不搬移、不修改**任何被拖曳的檔案。
4. 所有實驗檔案放在 `%TEMP%\m05\`，不在使用者資料夾留下垃圾。

## TDD Route

```text
TDD Route:
- Mode: off（~/.config/aegis/config.toml: tdd_mode = "off"）
- Decision: skipped
- Strict authority: not applicable
- Strict signals: 不適用（本計畫是可行性實驗，不是行為變更）
- Light eligibility: 不適用
- TDD-fit exception: 本計畫的產物是「實驗證據」而非「行為」；
  可重現性由 DragSim 自動化提供，比單元測試更直接
- Test posture: diagnostic reproduction（DragSim 是可重複執行的診斷重現器）
- Reason: 這一步的目的是取得「是/否」的物理事實，不是保護既有行為
- Verification: 見下方 Verification（自動化 DragSim + 人工確認）
```

## Change Necessity

```text
Change Necessity:
- User-visible need: 拖曳中叫出輪盤、任意位置放手（規格 §1.2）
- No-change / non-code option: 無——純文件無法回答「系統是否會命中這個視窗」
- Why code change is necessary: 這是作業系統層的物理行為，只能實測
- Minimum change boundary: 一個原型行程（M05）+ 一個自動化驗證器（DragSim）
- Decision: code-change
```

## Files

| 動作 | 路徑 |
|------|------|
| 建立 | `src/M05/M05.csproj` |
| 建立 | `src/M05/Native.cs` |
| 建立 | `src/M05/Evidence.cs` |
| 建立 | `src/M05/Overlay.cs` |
| 建立 | `src/M05/Hooks.cs` |
| 建立 | `src/M05/DragDetector.cs` |
| 建立 | `src/M05/Program.cs` |
| 建立 | `tools/dragsim/DragSim.csproj` |
| 建立 | `tools/dragsim/Program.cs` |
| 建立 | `docs/aegis/work/2026-09-30-m05-feasibility/`（實驗證據） |

## Existence Check

```text
Existence Check
- Proposed new surface: M05 原型行程、DragSim 驗證器
- Existing owner / reuse candidate: 無（greenfield）
- Why existing surface is insufficient: 系統沒有可借用的「拖曳中攔截」owner
- Creation proof: 本計畫本身就是 proof；任務失敗時整個 surface 被刪除，不留相容層
- Entropy / retirement impact: 原型**不得**演化成產品骨架——M1 會另建 `src/Sidecar`；
  M05 的 `--force-overlay` 與 DragSim 只服務實驗
- Decision: add-with-proof
```

## Architecture Integrity Lens

- **不變式**：接收層只在觸發鍵按著時存在；非觸發拖曳零侵入。
- **正規 owner**：`Native` 是 P/Invoke 的唯一來源；`DragDetector` 是狀態機的唯一權威；`Overlay` 是視窗生命週期的唯一擁有者。
- **責任重疊**：無——`Program` 只做組裝與訊息迴圈，不含邏輯。
- **是否有一層更高的 owner／契約可簡化**：有，且必須寫進計畫——**我們不需要 `IDropTarget::DragOver` 來取得游標位置與修飾鍵**：游標位置由低階滑鼠 hook 提供，觸發鍵由低階鍵盤 hook 提供。因此 **`WM_DROPFILES`（legacy 路徑，無需 COM interop）+ 兩個 hook** 可能就足以支撐完整設計。先在 Task 3 驗最便宜的路，失敗才走 Task 4 的 OLE `IDropTarget`。
- **退休／否證**：若 `WM_DROPFILES` 與 OLE 兩條路都收不到，代表假設不成立 → 退回路線 C。
- **Verdict**：通過（owner 清楚、且找到更便宜的驗證路徑）。

## Plan Pressure Test

```text
Plan Pressure Test:
- Owner / contract / retirement: 清楚；原型不得演化為產品骨架（已寫入 Existence Check）
- Architecture integrity / higher-level path: 已找到更便宜的 WM_DROPFILES＋hook 路徑（見上）
- Verification scope: 自動化（DragSim）＋人工（真實 Explorer 拖曳）雙軌，且可重複
- Task executability: 每個 Task 都給了完整程式碼與確切指令
- Pressure result: proceed
```

## Plan-Time Complexity Check

```text
Complexity Budget:
- Artifact class: 原型行程（拋棄式）
- Target files / artifacts: src/M05/*（7 檔，合計約 600 行）、tools/dragsim/*（2 檔，約 150 行）
- Current pressure: 無（全部新建）
- Projected post-change pressure: 無（原型不被 M1 重用）
- Budget result: within-budget
- Planned governance: 原型標記為實驗；M1 另建產品骨架

Plan-Time Complexity Check:
- Target files: 上述
- Existing size / shape signals: 無既有程式碼
- Owner fit: Native / Overlay / Hooks / DragDetector 各自單一職責
- Add-in-place risk: 低；但必須抗拒「順手把轉檔也寫進去」——那是 M1
- Better file boundary: 保持 7 檔；不建立 Service/Manager/Helper 之類的抽象
- Recommendation: add owner file
```

---

## Tasks

### Task 0 — 環境前置（**需使用者同意**）

**Files**：無（系統層）

**Why**：本機只有 .NET runtime，沒有 SDK，無法建置。

**Steps**
1. **先問使用者**，取得明確同意後才執行：
   ```powershell
   winget install --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
   ```
2. 驗證：
   ```powershell
   dotnet --list-sdks
   ```
   **預期輸出**：至少一行 `8.0.xxx [C:\Program Files\dotnet\sdk]`。
3. 若 `winget` 不可用，改用官方安裝程式（需使用者手動下載）並**停在這裡等使用者**。

> 若使用者不同意安裝 → **整份計畫停止**（`blocked`），不得改用其他語言偷跑。

---

### Task 1 — 建立 M05 專案骨架

**Files**
- 建立 `src/M05/M05.csproj`
- 建立 `src/M05/Native.cs`
- 建立 `src/M05/Evidence.cs`

**Why**：先把「與作業系統對話」和「留下證據」這兩塊釘死，後面的實驗才有可信的紀錄。

**Steps**

1. `src/M05/M05.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12.0</LangVersion>
    <AssemblyName>M05</AssemblyName>
    <RootNamespace>M05</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
</Project>
```

建立 `src/M05/app.manifest`（PerMonitorV2 DPI 感知，避免座標被系統縮放搞混）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

2. `src/M05/Native.cs`（**P/Invoke 的唯一來源**）：

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace M05;

internal static class Native
{
    // ---- 視窗樣式 ----
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    // 刻意不使用 WS_EX_TRANSPARENT (0x00000020)：
    // 加上它會讓 WindowFromPoint 跳過本視窗，整個機制立即失效。
    // Task 4 的驗證 #6 會實際檢查這個位元沒被設起來。

    public const int LWA_ALPHA = 0x2;
    public const byte OverlayAlpha = 1;          // 1/255：視覺不可見、仍可被命中
    public const int GwlExStyle = -20;
    public const long WsExTransparentBit = 0x00000020L;

    // ---- 視窗訊息 ----
    public const uint WM_DROPFILES = 0x0233;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;

    // ---- Hook ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    // ---- 系統度量 ----
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // ---- 虛擬鍵 ----
    public const uint VK_SPACE = 0x20;
    public const uint VK_ESCAPE = 0x1B;
    public const uint VK_F9 = 0x78;
    public const uint VK_Q = 0x51;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_MENU = 0x12;

    // ---- 視窗位置 ----
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint DRAGQUERY_COUNT = 0xFFFFFFFF;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool DragAcceptFiles(IntPtr hWnd, bool fAccept);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
    [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);
}
```

3. `src/M05/Evidence.cs`（**證據是本計畫的產物**）：

```csharp
namespace M05;

/// 狀態列採「原地覆寫單行」（使用者明確要求的輸出慣例），
/// 逐行事件寫入 evidence 檔，兩者分離。
internal sealed class Evidence : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _quiet;

    public Evidence(string path, bool quiet)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _quiet = quiet;
        Line($"=== M05 session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
    }

    /// 逐行事件：寫檔（必要時也寫 stdout）
    public void Line(string text)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _writer.WriteLine($"{stamp} {text}");
        if (!_quiet) Console.Error.WriteLine($"{stamp} {text}");
    }

    /// 單行狀態：只寫 stdout，原地覆寫
    public void Status(string text)
    {
        Console.Error.Write("\r" + text.PadRight(78));
    }

    public void ClearStatus() => Console.Error.Write("\r" + new string(' ', 78) + "\r");

    public void Dispose()
    {
        Line("=== session end ===");
        _writer.Dispose();
    }
}
```

4. 驗證：
   ```powershell
   dotnet build src/M05/M05.csproj -c Release
   ```
   **預期**：`Build succeeded`（此時尚無 Program.cs，會失敗 → 見 Task 1 步驟 5）

5. 補一個最小 `src/M05/Program.cs` 讓 Task 1 能單獨建置成功：

```csharp
namespace M05;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var evidence = new Evidence(
            Path.Combine(Path.GetTempPath(), "m05", "m05.log"),
            quiet: args.Contains("--quiet"));
        evidence.Line("Task 1 skeleton: ok");
        Console.Error.WriteLine();
        return 0;
    }
}
```

**Verification**
```powershell
dotnet build src/M05/M05.csproj -c Release
dotnet run --project src/M05 -c Release
```
**預期**：`Build succeeded`；`%TEMP%\m05\m05.log` 出現且含 `Task 1 skeleton: ok`。

---

### Task 2 — 接收層視窗（含四層硬保險）

**Files**
- 建立 `src/M05/Overlay.cs`
- 修改 `src/M05/Program.cs`（接上 `--force-overlay`）

**Why**：這是生死線的主角。它必須能被命中，也**必須收得回去**——收不回去就是桌面癱瘓。

**Steps**

1. `src/M05/Overlay.cs`：

```csharp
using System.Text;

namespace M05;

/// 全螢幕接收層。單一職責：視窗生命週期 + 拖放接收。
internal sealed class Overlay : IDisposable
{
    private const string ClassName = "M05OverlayWindow";

    private static Overlay? _self;              // 供靜態 WndProc 轉發
    private static Native.WndProcDelegate? _proc;   // 保留委派，避免被 GC 回收

    private IntPtr _hwnd = IntPtr.Zero;
    private readonly Evidence _evidence;
    private int _showCount;

    public event Action<string[]>? FilesDropped;

    public Overlay(Evidence evidence) => _evidence = evidence;

    public bool IsVisible { get; private set; }

    public void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;

        _self = this;
        _proc = StaticWndProc;
        IntPtr hInst = Native.GetModuleHandleW(null);

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst,
            lpszClassName = ClassName
        };
        Native.RegisterClassExW(ref wc);   // 重複註冊會回 0，可忽略

        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            ClassName, "M05 overlay", Native.WS_POPUP,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowExW failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        Native.DragAcceptFiles(_hwnd, true);
        Native.SetLayeredWindowAttributes(_hwnd, 0, Native.OverlayAlpha, Native.LWA_ALPHA);

        long ex = Native.GetWindowLongW(_hwnd, Native.GwlExStyle);
        bool transparentBitSet = (ex & Native.WsExTransparentBit) != 0;
        _evidence.Line($"overlay created hwnd=0x{_hwnd.ToInt64():X} exstyle=0x{ex:X} " +
                       $"WS_EX_TRANSPARENT={(transparentBitSet ? "SET(致命)" : "not set(正確)")} " +
                       $"rect=({x},{y},{w}x{h}) alpha={Native.OverlayAlpha}");

        if (transparentBitSet)
            _evidence.Line("FATAL: WS_EX_TRANSPARENT is set — the drop hit-test WILL be skipped.");
    }

    public void Show()
    {
        EnsureCreated();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        IsVisible = true;
        _showCount++;
        _evidence.Line($"overlay SHOWN (count={_showCount})");
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        if (IsVisible) _evidence.Line("overlay HIDDEN");
        IsVisible = false;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_self is not null && msg == Native.WM_DROPFILES)
        {
            _self.OnDropFiles(wParam);
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnDropFiles(IntPtr hDrop)
    {
        try
        {
            uint count = Native.DragQueryFileW(hDrop, Native.DRAGQUERY_COUNT, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint len = Native.DragQueryFileW(hDrop, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                Native.DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                paths.Add(sb.ToString());
            }
            _evidence.Line($"*** DROP RECEIVED: {count} file(s) ***");
            foreach (var p in paths) _evidence.Line($"    {p}");
            FilesDropped?.Invoke(paths.ToArray());
        }
        catch (Exception ex)
        {
            _evidence.Line("drop handler error: " + ex.Message);
        }
        finally
        {
            Native.DragFinish(hDrop);
            Hide();   // 硬保險：收到 drop 立即收層
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _self = null;
    }
}
```

2. 修改 `src/M05/Program.cs`：加入 `--force-overlay`（升起後 **10 秒** 自動收起，作為硬保險 #3 的現場驗證）：

```csharp
namespace M05;

internal static class Program
{
    private const int OverlayTimeoutMs = 30_000;

    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        bool force = args.Contains("--force-overlay");
        using var evidence = new Evidence(
            Path.Combine(Path.GetTempPath(), "m05", "m05.log"), quiet);

        using var overlay = new Overlay(evidence);
        overlay.FilesDropped += paths => evidence.Status($"DROP OK: {paths.Length} file(s)");

        // 硬保險 #3：逾時強制收層
        using var watchdog = new System.Timers.Timer(OverlayTimeoutMs) { AutoReset = false };
        watchdog.Elapsed += (_, _) =>
        {
            if (overlay.IsVisible)
            {
                evidence.Line($"WATCHDOG: overlay visible > {OverlayTimeoutMs} ms -> force hide");
                overlay.Hide();
            }
        };

        if (force)
        {
            evidence.Line("mode: --force-overlay (hook/detector bypassed)");
            overlay.Show();
            watchdog.Start();
            return RunMessageLoop(evidence);
        }

        evidence.Line("mode: normal (no detector wired yet — Task 3)");
        return RunMessageLoop(evidence);
    }

    private static int RunMessageLoop(Evidence evidence)
    {
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        evidence.Line("message loop exited");
        return 0;
    }
}
```

3. **人工驗證硬保險**：執行 `--force-overlay` 後，**立刻試著點擊桌面圖示**。
   - 預期：點得到（因為 alpha=1/255 不可見，且**沒有** `WS_EX_TRANSPARENT` → 點擊其實會被接收層吃掉）。
   - ⚠️ **這是預期中的行為**：接收層升起時本來就會吃掉滑鼠輸入。這是為什麼它只能在「按住觸發鍵的拖曳中」短暫存在。若此時點不到桌面，**是正常的**，30 秒後會自動恢復。
   - **若 30 秒後仍未恢復** → 記錄為缺陷，回到硬保險設計。

**Verification**
```powershell
dotnet build src/M05/M05.csproj -c Release
dotnet run --project src/M05 -c Release -- --force-overlay
```
**預期**（`%TEMP%\m05\m05.log`）：
```
overlay created hwnd=0x... exstyle=0x... WS_EX_TRANSPARENT=not set(正確) rect=(0,0,3840x1080) alpha=1
mode: --force-overlay (hook/detector bypassed)
overlay SHOWN (count=1)
```
且 30 秒後出現 `WATCHDOG: overlay visible > 30000 ms -> force hide` 與 `overlay HIDDEN`。

---

### Task 3 — 偵測層：低階 hook + 狀態機

**Files**
- 建立 `src/M05/Hooks.cs`
- 建立 `src/M05/DragDetector.cs`
- 修改 `src/M05/Program.cs`（接上偵測與收層保險）

**Why**：沒有偵測，接收層就不知道何時該出現。此任務同時實作規格 §4.2.2 的硬保險 #1、#2、#5。

**Steps**

1. `src/M05/Hooks.cs`：

```csharp
using System.Runtime.InteropServices;

namespace M05;

/// 低階全域 hook。注意：WH_MOUSE_LL / WH_KEYBOARD_LL 不需要管理員權限。
internal sealed class Hooks : IDisposable
{
    private IntPtr _mouseHook, _keyboardHook;
    private Native.HookProc? _mouseProc, _keyProc;   // 保留委派避免 GC

    public event Action<uint, Native.POINT>? MouseEvent;   // wParam, 游標
    public event Action<uint, bool>? KeyEvent;             // vkCode, isDown

    public void Install(Evidence evidence)
    {
        IntPtr hInst = Native.GetModuleHandleW(null);

        _mouseProc = MouseCallback;
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, hInst, 0);
        if (_mouseHook == IntPtr.Zero)
            evidence.Line("HOOK FAIL mouse: " + Marshal.GetLastWin32Error());

        _keyProc = KeyboardCallback;
        _keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyProc, hInst, 0);
        if (_keyboardHook == IntPtr.Zero)
            evidence.Line("HOOK FAIL keyboard: " + Marshal.GetLastWin32Error());

        evidence.Line($"hooks installed mouse=0x{_mouseHook.ToInt64():X} keyboard=0x{_keyboardHook.ToInt64():X}");
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            MouseEvent?.Invoke((uint)wParam.ToInt64(), info.pt);
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            uint m = (uint)wParam.ToInt64();
            bool isDown = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
            bool isUp = m == Native.WM_KEYUP || m == Native.WM_SYSKEYUP;
            if (isDown || isUp) KeyEvent?.Invoke(info.vkCode, isDown);
        }
        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }
}
```

2. `src/M05/DragDetector.cs`（**狀態機唯一權威**，對應規格 §4.2）：

```csharp
using System.Diagnostics;

namespace M05;

internal enum DetectorState { Idle, Armed, Capturing }

internal sealed class DragDetector
{
    private readonly Evidence _evidence;
    private readonly int _thresholdX;
    private readonly int _thresholdY;

    private DetectorState _state = DetectorState.Idle;
    private Native.POINT _armedAt;
    private bool _triggerDown;

    public event Action? Captured;
    public event Action<string>? Released;

    public uint TriggerKey { get; set; } = Native.VK_SPACE;

    public DragDetector(Evidence evidence)
    {
        _evidence = evidence;
        _thresholdX = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXDRAG));
        _thresholdY = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYDRAG));
        _evidence.Line($"detector ready threshold=({_thresholdX},{_thresholdY}) trigger=VK_SPACE");
    }

    public void OnKey(uint vk, bool isDown)
    {
        if (vk != TriggerKey) return;
        if (_triggerDown == isDown) return;
        _triggerDown = isDown;
        _evidence.Line($"trigger key {(isDown ? "DOWN" : "UP")} (state={_state})");
        if (!isDown && _state == DetectorState.Capturing) Release("trigger released");
    }

    public void OnMouse(uint msg, Native.POINT pt)
    {
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
                if (IsExplorerForeground())
                {
                    _armedAt = pt;
                    SetState(DetectorState.Armed);
                }
                break;

            case Native.WM_MOUSEMOVE:
                if (_state == DetectorState.Armed && _triggerDown)
                {
                    int dx = Math.Abs(pt.X - _armedAt.X);
                    int dy = Math.Abs(pt.Y - _armedAt.Y);
                    if (dx > _thresholdX || dy > _thresholdY)
                    {
                        SetState(DetectorState.Capturing);
                        Captured?.Invoke();
                    }
                }
                break;

            case Native.WM_LBUTTONUP:
            case Native.WM_RBUTTONUP:
                // 硬保險 #1：任何滑鼠放開一律收層
                if (_state == DetectorState.Capturing) Release("mouse up");
                SetState(DetectorState.Idle);
                break;
        }
    }

    public void ForceRelease(string reason) => Release(reason);

    private void Release(string reason)
    {
        SetState(DetectorState.Idle);
        Released?.Invoke(reason);
    }

    private void SetState(DetectorState next)
    {
        if (_state == next) return;
        _evidence.Line($"state {_state} -> {next}");
        _state = next;
    }

    private static bool IsExplorerForeground()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        try { return string.Equals(Process.GetProcessById((int)pid).ProcessName, "explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
```

3. 修改 `src/M05/Program.cs`：接上 hook、偵測器、硬保險 #1/#2/#5，並移除 Task 2 的測試用行為。

```csharp
namespace M05;

internal static class Program
{
    private const int OverlayTimeoutMs = 30_000;

    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        bool force = args.Contains("--force-overlay");
        using var evidence = new Evidence(Path.Combine(Path.GetTempPath(), "m05", "m05.log"), quiet);

        using var overlay = new Overlay(evidence);
        using var hooks = new Hooks();
        var detector = new DragDetector(evidence);

        overlay.FilesDropped += paths => evidence.Status($"DROP OK: {paths.Length}");

        detector.Captured += () =>
        {
            overlay.Show();
            watchdog.Stop();
            watchdog.Start();     // 每次升起重新計時（硬保險 #3）
        };
        detector.Released += reason =>
        {
            evidence.Line($"release: {reason}");
            overlay.Hide();
            watchdog.Stop();
        };

        hooks.MouseEvent += detector.OnMouse;
        hooks.KeyEvent += detector.OnKey;

        // 硬保險 #3：逾時強制收層
        using var watchdog = new System.Timers.Timer(OverlayTimeoutMs) { AutoReset = false };
        watchdog.Elapsed += (_, _) =>
        {
            if (!overlay.IsVisible) return;
            evidence.Line($"WATCHDOG: overlay > {OverlayTimeoutMs} ms -> force hide");
            detector.ForceRelease("watchdog");
        };

        if (force)
        {
            evidence.Line("mode: --force-overlay (hooks NOT used)");
            overlay.Show();
            watchdog.Start();
        }
        else
        {
            evidence.Line("mode: normal");
            hooks.Install(evidence);
        }

        return RunMessageLoop(evidence);
    }

    private static int RunMessageLoop(Evidence evidence)
    {
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        evidence.Line("message loop exited");
        return 0;
    }
}
```

> **硬保險 #5（緊急停用熱鍵）刻意不在本計畫實作**：M0.5 只有 30 秒逾時與放開即收，已足以安全實驗。留在 M1（規格 §4.2.2 的完整實作）。

**Verification**
```powershell
dotnet build src/M05/M05.csproj -c Release
```
然後**以背景工作**啟動（**不得同步啟動常駐程序**）：
```powershell
dotnet run --project src/M05 -c Release
```
**人工步驟**：從檔案總管拖曳任一個檔案，過程中按住**空白鍵**，在桌面任意處放開。
**預期**（`%TEMP%\m05\m05.log`）：
```
hooks installed mouse=0x... keyboard=0x...
trigger key DOWN (state=Idle)
state Idle -> Armed
trigger key DOWN 之前應先出現 state Idle -> Armed（順序視操作而定）
state Armed -> Capturing
overlay SHOWN (count=1)
*** DROP RECEIVED: 1 file(s) ***
    C:\...\某檔.txt
overlay HIDDEN
```
> 若 `DROP RECEIVED` 沒出現 → 這是**生死線失敗**，進入 Task 4。

---

### Task 4 — 自動化驗證器 DragSim（並在必要時走 OLE 後備）

**Files**
- 建立 `tools/dragsim/DragSim.csproj`
- 建立 `tools/dragsim/Program.cs`

**Why**：讓「命中與否」可**重複**驗證，不依賴人工手勢；也是 OLE `IDropTarget` 後備路線的驅動器。

**Steps**

1. `tools/dragsim/DragSim.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>DragSim</AssemblyName>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
</Project>
```

2. `tools/dragsim/Program.cs`：

```csharp
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static void MoveTo(int x, int y)
    {
        int sw = Screen.PrimaryScreen!.Bounds.Width;
        int sh = Screen.PrimaryScreen!.Bounds.Height;
        var input = new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT
            {
                dx = (int)(x * 65535.0 / (sw - 1)),
                dy = (int)(y * 65535.0 / (sh - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void Button(uint flag)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flag } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // 用法: DragSim <檔案路徑> <目標X> <目標Y> [起點X] [起點Y]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: DragSim <file> <targetX> <targetY> [startX] [startY]");
            return 2;
        }
        string file = args[0];
        int tx = int.Parse(args[1]), ty = int.Parse(args[2]);
        int sx = args.Length > 3 ? int.Parse(args[3]) : Math.Max(2, tx - 400);
        int sy = args.Length > 4 ? int.Parse(args[4]) : ty;

        if (!File.Exists(file)) { Console.Error.WriteLine("no such file: " + file); return 2; }

        Console.WriteLine($"DragSim: file={file} start=({sx},{sy}) target=({tx},{ty})");

        var data = new DataObject(DataFormats.FileDrop, new[] { file });
        DragDropEffects effect = DragDropEffects.None;

        var dragThread = new Thread(() =>
        {
            var owner = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                Size = new Size(1, 1)
            };
            owner.Show();
            try { effect = owner.DoDragDrop(data, DragDropEffects.Copy); }
            catch (Exception ex) { Console.Error.WriteLine("DoDragDrop threw: " + ex.Message); }
            finally { owner.Dispose(); }
        });
        dragThread.SetApartmentState(ApartmentState.STA);
        dragThread.Start();

        MoveTo(sx, sy);
        Thread.Sleep(300);
        Button(MOUSEEVENTF_LEFTDOWN);      // 讓拖放迴圈認為按鍵是壓下的
        Thread.Sleep(300);

        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(sx + (tx - sx) * i / steps, sy + (ty - sy) * i / steps);
            Thread.Sleep(60);
        }
        Thread.Sleep(500);                 // 停在目標上，讓接收層有機會收到 DragOver/Drop
        Button(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(300);

        if (!dragThread.Join(5000)) Console.Error.WriteLine("warning: drag thread did not finish");
        Console.WriteLine("drageffect=" + effect);
        return 0;
    }
}
```

3. 執行實驗（**M05 需先以背景工作啟動並處於 `--force-overlay`**）：

```powershell
$f = Join-Path $env:TEMP 'm05\sample-drag.txt'
Set-Content -LiteralPath $f -Value 'M05 drag target sample' -Encoding UTF8
dotnet build tools/dragsim/DragSim.csproj -c Release
dotnet run --project tools/dragsim -c Release -- $f 960 540
```

**預期**：`m05.log` 出現 `*** DROP RECEIVED: 1 file(s) ***` 且路徑為 `sample-drag.txt`。

4. **若步驟 3 沒有收到 drop**：判定「`WM_DROPFILES` legacy 路徑不適用」，**不要**改走殼層擴充（規格 D2 已否決）。改為在 `Overlay.cs` 加入最小 OLE `IDropTarget`（COM interop：`RegisterDragDrop(hwnd, new DropTarget())`，`IDropTarget.Drop` 由 `IDataObject.GetData(CF_HDROP)` 取得 `DVASPECT_CONTENT` / `TYMED_HGLOBAL` 後以 `DragQueryFileW` 解析），並在 Task 5 重跑同一組實驗。
   **注意**：這是本計畫唯一允許的範圍擴張，且**不改變**架構決策。

**Verification**
```powershell
dotnet build tools/dragsim/DragSim.csproj -c Release
# M05 已在背景執行（--force-overlay）
dotnet run --project tools/dragsim -c Release -- <temp樣本檔> 960 540
```
**預期**：見上。若失敗，走步驟 4 並記錄原因。

---

### Task 5 — 判定與證據歸檔

**Files**
- 建立 `docs/aegis/work/2026-09-30-m05-feasibility/20-checkpoint.md`
- 建立 `docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md`

**Why**：生死線的結論必須留下可稽核的證據，且結論會**改變規格**（失敗 → 退路線 C）。

**Steps**

1. 依下表跑完整實驗矩陣，每列都記錄在 `90-evidence.md`：

| # | 情境 | 執行方式 | 通過條件 |
|---|------|----------|----------|
| 1 | 桌面放手 | DragSim → 桌面空白處 | 收到 drop |
| 2 | 檔案總管視窗內放手 | DragSim → 資料夾視窗中心 | 收到 drop |
| 3 | 第二螢幕放手 | DragSim → 第二螢幕座標 | 收到 drop |
| 4 | 真實 Explorer 拖曳（人工） | 手動拖曳 + 空白鍵 | 收到 drop |
| 5 | 收層可靠性 | 連續 50 次觸發／放棄 | 桌面可正常點擊，且 `overlay HIDDEN` 出現 50 次 |
| 6 | 樣式檢查 | 讀 `m05.log` 第一行 | `WS_EX_TRANSPARENT=not set(正確)` |
| 7 | 非觸發拖曳零侵入 | 不按空白鍵拖曳檔案 | 檔案正常移動，`m05.log` 無 `overlay SHOWN` |
| 8 | IME 開啟時空白鍵（規格附錄 C #3） | 切到注音輸入法後重做 #4 | 仍能觸發（記錄實測結果，不設通過門檻） |

2. 判定：
   - **#1–#5、#6、#7 全通過** → **GO**：更新規格 §9.2 標記為已驗證，並建立 M1 計畫。
   - **#1–#3、#4 任一失敗，但 Task 4 步驟 4 的 OLE 路徑成功** → **GO（改用 OLE 路徑）**：更新規格 §4.1（把 `WM_DROPFILES` 改為 OLE `IDropTarget`）。
   - **兩種機制都收不到** → **NO-GO**：退回路線 C（浮動接收目標），更新規格 §3.1／§4 並重新協商「任意位置放手」。
   - **#5 失敗（桌面卡住）** → **NO-GO**：安全性缺陷優先於功能。

3. 更新 `docs/aegis/specs/2026-09-30-sidecar-drag-convert-design.md` 的 §9.2 為實測結果（**這是唯一允許修改規格的任務**）。

**Verification**
```powershell
python "C:/Users/User/.dsh/profiles/web/node_modules/aegis/scripts/aegis-workspace.py" check --root "C:\Users\User\Desktop\專案\tangerine-win"
```
**預期**：`Aegis workspace check passed`。

---

## Execution Readiness View

```text
Execution Readiness View:
- Intent Lock: 證明 alpha=1/255 layered 接收層可被拖放命中並取得檔案清單（規格 §4.1/§9.2）
- Scope Fence: 只建 M05 原型與 DragSim；不實作轉檔、輪盤、設定、發行
- Baseline Lock: 規格 §4.1/§4.2/§9.2 已讀並引用；無程式碼基線（greenfield）
- Approved Behavior: 觸發鍵=空白鍵；任意位置放手；收層四層保險（本計畫實作 #1/#2/#3）
- Owner / Contract Constraints: Native=唯一 P/Invoke 來源；DragDetector=唯一狀態權威；Overlay=視窗唯一擁有者
- Compatibility Boundary: 不安裝 shell 擴充、不寫 HKLM、不需管理員、不修改被拖曳的檔案
- Retirement Boundary: 原型不得演化為產品骨架；M1 另建 src/Sidecar；M05 的 --force-overlay 只服務實驗
- Task Batches: T0（環境，需同意）→ T1（骨架）→ T2（接收層）→ T3（偵測）→ T4（自動化）→ T5（判定）
- Test Obligations: DragSim 的 8 項實驗矩陣；TDD Route=skipped（診斷重現非行為測試）
- Review Gates: T0 需使用者同意；T5 的 GO/NO-GO 需使用者確認
- Drift / Rewind Rules: 若需引入 OLE IDropTarget，僅限 Task 4 步驟 4；任何殼層擴充需求 → 停止並回設計
- Evidence Required Before Completion: m05.log（含樣式檢查行）＋ 8 項矩陣結果 ＋ 判定結論
- Advisory Boundary: 本視圖為方法包執行指引，非 GateDecision、非完成授權
```

## Risks

| # | 風險 | 機率 | 影響 | 對策 |
|---|------|------|------|------|
| 1 | alpha=1/255 的視窗被拖放迴圈跳過 | 中 | 生死線失敗 | 立即改測 OLE `IDropTarget`（Task 4 步驟 4）；仍失敗 → 路線 C |
| 2 | `WM_DROPFILES` legacy 路徑不觸發 | 中 | 需多寫 COM interop | 已備妥 OLE 後備路徑；不影響架構決策 |
| 3 | DragSim 的合成拖曳無法重現真實拖曳 | 中 | 自動化證據不足 | 以人工拖曳（矩陣 #4）交叉驗證；DragSim 只是加速器 |
| 4 | 接收層收不回去 | 低 | 桌面癱瘓 | 三層保險（mouse-up／放開觸發鍵／30 秒逾時）；矩陣 #5 專測 |
| 5 | WPF 的 `AllowsTransparency` 會加入 `WS_EX_TRANSPARENT` | 未定 | M1 若用 WPF 可能整個失效 | 本計畫刻意不用 WPF；M1 設計輪盤前先單獨驗證此點 |
| 6 | PerMonitorV2 座標與 DragSim 的絕對座標不一致 | 低 | 座標偏移 | 目標點以滑鼠實際位置紀錄，不信任傳入值 |
| 7 | hook 被其他軟體攔截（防毒、IME 工具） | 低 | 偵測失效 | 記錄 `HOOK FAIL` 行；矩陣 #8 順帶觀察 |

## Retirement

- 本計畫**沒有**要被取代的舊路徑（greenfield）。
- **明確的未來退休項**：`--force-overlay` 與整個 `DragSim` 只服務生死線；M1 建立後，`src/M05` 應整包刪除（不保留相容層、不演化為產品骨架）。
- 觸發條件：M1 計畫的第一個 commit 時刪除 `src/M05` 或將其標記 `experiments/` 並排除於建置。

## ADR 訊號（保留供後續 `recording-architecture-decisions`）

| # | 決策面 | 若本計畫通過則可固化為 |
|---|--------|------------------------|
| ADR-1 | 以全螢幕 layered 接收層取代 shell 擴充 | **成立**（實測證據齊備後） |
| ADR-4 | UI ↔ Worker 契約 | 本計畫不涉及 |

## Self-Review

| 檢查 | 結果 |
|------|------|
| 規格覆蓋 | §4.1 → T2/T4；§4.2 → T3；§4.2.2 #1/#2/#3 → T2/T3；§9.2 → T4/T5。§4.2.2 #4（行程結束）由作業系統天然滿足；#5 明確延後至 M1（已註記） |
| Placeholder | 無 TBD；每個 Task 都有完整程式碼與確切指令 |
| 型別一致性 | `Evidence`／`Overlay`／`Hooks`／`DragDetector` 的方法簽章在各 Task 間一致；`Native` 常數名稱一致 |
| 相容性 | 不裝 shell 擴充、不寫 HKLM、不需管理員、不只讀不改檔；矩陣 #7 專測零侵入 |
| Change necessity | 已記錄：這是作業系統物理行為，純文件無法回答 |
| 存在性 | M05／DragSim 為 add-with-proof，且已定義退休觸發 |
| 複雜度 | within-budget；抗拒「順手寫轉檔」 |
| 架構完整性 | 已找到更高層簡化（`WM_DROPFILES` + hook 取代 OLE DragOver 依賴），並轉為先驗順序 |
| 驗證 | 全部為確切指令；DragSim 可重複執行 |
| ADR／雙軌 | ADR-1 訊號保留；無 repair/retirement 雙軌需求 |

---

## 執行偏差記錄（執行時新增，2026-09-30）

**結果：生死線通過（GO）。** 完整證據：`docs/aegis/work/2026-09-30-m05-feasibility/90-evidence.md`。

| # | 計畫原文 | 實際執行 | 理由與影響 |
|---|----------|----------|------------|
| 1 | Task 4 把 `DragSim` 當「讓命中可重複驗證」的自動化器 | `DragSim` **不可用**：兩版手勢都無法產生真實 OLE 拖曳，**對照組**（已知可收拖放的 WinForms 視窗）同樣收不到 `DragEnter` | 合成 `DoDragDrop` 無法重現真實拖曳（計畫 Risk #3 命中）。改由**人工矩陣**主導，`--target` 對照組模式保留為證據。M1 需另尋自動化方案（殘餘項 R4） |
| 2 | Task 2 的 `Program.cs` 在事件處理常式之後宣告 `watchdog` | 宣告順序改到處理常式**之前** | C# 不允許 lambda 捕捉其後才宣告的區域變數 |
| 3 | Task 2/Task 3 的保險 #1／#2 為「立即收層」 | 改為 `HIDE_GRACE = 250 ms` 延遲收層 | 低階滑鼠 hook 比 shell 拖放迴圈更早看到 `mouse-up`；立即隱藏會讓放手當下的 drop 落到接收層底下。**這是規格級發現，已寫回 §4.2.2** |
| 4 | Task 5 的矩陣 #3（第二螢幕） | 本機只有單一 2560×1080 螢幕 → N/A | 降級為殘餘項 R1，帶進 M1 |
| 5 | 計畫未預期 | 新增 `--alpha N` 診斷旗標與 `--cycle-test N` 模式 | `--alpha` 讓人工測試能**目視**確認接收層是否升起（生產值不可見）；`--cycle-test` 讓矩陣 #5 的 50 次循環可自動執行。兩者都**只服務實驗**，仍列入退休清單 |
| 6 | Task 0 | .NET SDK 8.0.425 已安裝（使用者授權） | 與計畫一致，無偏差 |

**退休邊界不變**：`src/M05` 與 `tools/dragsim` 都是拋棄式原型，M1 建立時整包刪除。
