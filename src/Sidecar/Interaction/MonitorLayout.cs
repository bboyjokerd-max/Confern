using System.Runtime.InteropServices;

namespace Sidecar;

internal sealed record MonitorEntry(int Index, string Device, Native.RECT Bounds, Native.RECT Work, bool IsPrimary)
{
    public int Width => Bounds.Right - Bounds.Left;
    public int Height => Bounds.Bottom - Bounds.Top;
}

/// <summary>
/// **唯一座標來源**。所有幾何都取自系統，不得自行推算虛擬螢幕聯集。
/// 行程為 PerMonitorV2（見 app.manifest），因此這裡的座標是**實體像素**。
/// </summary>
internal static class MonitorLayout
{
    public static MonitorEntry[] Enumerate()
    {
        var raw = new List<(IntPtr Handle, Native.MONITORINFOEX Info)>();

        Native.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var mi = new Native.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (Native.GetMonitorInfoW(hMon, ref mi)) raw.Add((hMon, mi));
            return true;   // 繼續列舉
        };

        if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
            throw new InvalidOperationException(
                "EnumDisplayMonitors failed: " + Marshal.GetLastWin32Error());
        GC.KeepAlive(proc);

        if (raw.Count == 0)
            throw new InvalidOperationException("EnumDisplayMonitors returned no monitors");

        // 主螢幕排第一，其餘依左上角排序 → Index 穩定（每次呼叫結果一致）
        return raw
            .Select(x => new { x.Info, Primary = (x.Info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0 })
            .OrderByDescending(x => x.Primary)
            .ThenBy(x => x.Info.rcMonitor.Left)
            .ThenBy(x => x.Info.rcMonitor.Top)
            .Select((x, i) => new MonitorEntry(i, x.Info.szDevice, x.Info.rcMonitor, x.Info.rcWork, x.Primary))
            .ToArray();
    }

    /// <summary>游標所在的螢幕；查不到時退回主螢幕。</summary>
    public static MonitorEntry Nearest(int x, int y, MonitorEntry[] all)
    {
        var pt = new Native.POINT { X = x, Y = y };
        IntPtr handle = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        if (handle != IntPtr.Zero)
        {
            var mi = new Native.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (Native.GetMonitorInfoW(handle, ref mi))
            {
                MonitorEntry? hit = all.FirstOrDefault(m => m.Device == mi.szDevice);
                if (hit is not null) return hit;
            }
        }
        return all.First(m => m.IsPrimary);
    }
}
