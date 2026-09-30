using System.Diagnostics;

namespace Sidecar;

internal enum DetectorState { Idle, Armed, Capturing }

/// <summary>
/// 規格 §4.2 的狀態機——**唯一狀態權威**。
/// 只判斷「該不該升起接收層」，不碰視窗、不碰 UI、不碰收層。
/// </summary>
internal sealed class DragDetector
{
    private readonly Evidence _evidence;
    private readonly int _thresholdX;
    private readonly int _thresholdY;
    private readonly bool _assumeExplorerForeground;

    private DetectorState _state = DetectorState.Idle;
    private Native.POINT _armedAt;
    private bool _triggerDown;

    /// <summary>規格 §4.2.2 #1／#2 的觸發源。收層一律由 <see cref="ReleaseGuard"/> 決定（含 250 ms 寬限）。</summary>
    public event Action? Captured;
    public event Action<string>? Released;

    /// <summary>觸發鍵。由建構子注入（Q1：預設空白鍵、可由設定覆寫），此處不得硬編碼。</summary>
    public uint TriggerKey { get; }

    public bool IsDisabled { get; private set; }
    public DetectorState State => _state;

    public DragDetector(Evidence evidence, uint triggerKey, bool assumeExplorerForeground = false)
    {
        _evidence = evidence;
        TriggerKey = triggerKey;
        _assumeExplorerForeground = assumeExplorerForeground;
        _thresholdX = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXDRAG));
        _thresholdY = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYDRAG));
        _evidence.Line($"detector ready threshold=({_thresholdX},{_thresholdY}) trigger=0x{triggerKey:X2}");
        if (assumeExplorerForeground)
        {
            // 診斷用：讓自動化驗收不必為了「前景必須是 explorer」去最小化使用者的視窗。
            // 這會停用「非觸發拖曳零侵入」的第一道保證，因此一律留下痕跡、永不預設開啟。
            _evidence.Line("detector WARNING: foreground check DISABLED (--assume-explorer-foreground); " +
                           "zero-interference precondition is NOT being verified in this run");
        }
    }

    /// <summary>緊急停用（保險 #5）：此後不再進入 Armed／Capturing，直到重啟。</summary>
    public void Disable()
    {
        IsDisabled = true;
        _triggerDown = false;
        SetState(DetectorState.Idle);
        _evidence.Line("detector DISABLED (no capture until restart)");
    }

    public void OnKey(uint vk, bool isDown)
    {
        if (vk != TriggerKey) return;
        if (_triggerDown == isDown) return;
        _triggerDown = isDown;
        _evidence.Line($"trigger key {(isDown ? "DOWN" : "UP")} (state={_state}, disabled={IsDisabled})");
        if (isDown && IsDisabled) return;
        if (!isDown && _state == DetectorState.Capturing) RequestRelease("trigger released");
    }

    public void OnMouse(uint msg, Native.POINT pt)
    {
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
                if (!IsDisabled && (_assumeExplorerForeground || IsExplorerForeground()))
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

    private void RequestRelease(string reason) => Released?.Invoke(reason);

    private void SetState(DetectorState next)
    {
        if (_state == next) return;
        _evidence.Line($"state {_state} -> {next}");
        _state = next;
    }

    /// <summary>只有檔案總管前台的左鍵按下才進入 Armed——這是「非觸發拖曳零侵入」的第一道保證。</summary>
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
