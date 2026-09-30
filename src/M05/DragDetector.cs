using System.Diagnostics;

namespace M05;

internal enum DetectorState { Idle, Armed, Capturing }

/// <summary>
/// 規格 §4.2 的狀態機——唯一狀態權威。
/// 只判斷「該不該升起接收層」，不碰視窗、不碰 UI。
/// </summary>
internal sealed class DragDetector
{
    private readonly Evidence _evidence;
    private readonly int _thresholdX;
    private readonly int _thresholdY;

    private DetectorState _state = DetectorState.Idle;
    private Native.POINT _armedAt;
    private bool _triggerDown;

    /// 觸發源（規格 §4.2.2 #1／#2）。Program 在此以「延遲 250 ms」收層，
    /// 讓放手當下的 drop 有時間被我們解析——立即收層會讓 drop 落到桌面底下。
    public event Action? Captured;
    public event Action<string>? ReleaseRequested;

    public uint TriggerKey { get; set; } = Native.VK_SPACE;
    public DetectorState State => _state;

    public DragDetector(Evidence evidence)
    {
        _evidence = evidence;
        _thresholdX = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXDRAG));
        _thresholdY = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYDRAG));
        _evidence.Line($"detector ready threshold=({_thresholdX},{_thresholdY}) trigger=VK_SPACE(0x20)");
    }

    public void OnKey(uint vk, bool isDown)
    {
        if (vk != TriggerKey) return;
        if (_triggerDown == isDown) return;
        _triggerDown = isDown;
        _evidence.Line($"trigger key {(isDown ? "DOWN" : "UP")} (state={_state})");
        if (!isDown && _state == DetectorState.Capturing) RequestRelease("trigger released");
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
                if (_state == DetectorState.Capturing) RequestRelease("mouse up");
                SetState(DetectorState.Idle);
                break;
        }
    }

    public void ForceRelease(string reason) => RequestRelease(reason);

    private void RequestRelease(string reason) => ReleaseRequested?.Invoke(reason);

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
        try
        {
            return string.Equals(
                Process.GetProcessById((int)pid).ProcessName,
                "explorer",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
