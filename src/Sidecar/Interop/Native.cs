using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// P/Invoke 與常數的**唯一來源**（產品契約：其他檔案不得自行 DllImport）。
/// T1 只帶入骨架所需的訊息迴圈與主控台橋接；T2 補上 M05 移植與螢幕 API，
/// T3 補上視窗樣式與拖放 API。
/// </summary>
internal static class Native
{
    // ---- 訊息迴圈 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    // ---- 主控台橋接 ----
    // Sidecar 是 WinExe（不留主控台視窗：那會搶走前景焦點並破壞「前景行程＝explorer」判定）。
    // 診斷模式（--version／--dump-monitors）需要把輸出送回呼叫端的終端機，
    // 因此在那裡才接上父行程的主控台；一般常駐模式永遠不接。
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    /// <summary>接上父行程的主控台並把 stdout/stderr 重新指向它；沒有父主控台時為 no-op。</summary>
    public static bool AttachParentConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
        return true;
    }
}
