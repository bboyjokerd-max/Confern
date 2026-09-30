namespace Sidecar;

/// <summary>
/// 規格 §4.2.2 的五層硬保險，**唯一 owner**。
///
/// 任何收層路徑都必須經過這裡；<see cref="HitLayer"/> 的 Show/Hide 只能由本類別呼叫。
/// M05 把收層散在 Program 的三個 timer 與事件處理常式中，那是原型債：新增任何一條路徑
/// 都可能繞過保險，而這一層失效會讓整個桌面癱瘓。
///
/// 保險 #4（行程結束／當機）**不需要程式碼**：視窗屬於行程，行程消失視窗即消失，以 A13 驗收。
/// </summary>
internal sealed class ReleaseGuard : IDisposable
{
    /// <summary>
    /// 收層寬限（M0.5 實測發現）：低階滑鼠 hook 比 shell 的拖放迴圈**更早**看到 mouse-up，
    /// 立即收層會讓放手當下的 drop 落到接收層**底下**，等於每次放手都退化成保底語意。
    /// </summary>
    public const int HideGraceMs = 250;

    /// <summary>規格 §4.2.2 #3 的保底秒數。生產值固定 30 s；診斷可覆寫（並會寫進證據）。</summary>
    public const int DefaultVisibleTimeoutMs = 30_000;

    private readonly Evidence _evidence;
    private readonly HitLayer _hit;
    private readonly System.Timers.Timer _grace;
    private readonly System.Timers.Timer _watchdog;

    public bool CaptureDisabled { get; private set; }
    public int VisibleTimeoutMs { get; }
    public bool IsArmed => _watchdog.Enabled;

    public ReleaseGuard(Evidence evidence, HitLayer hit, int visibleTimeoutMs = DefaultVisibleTimeoutMs)
    {
        _evidence = evidence;
        _hit = hit;
        VisibleTimeoutMs = Math.Clamp(visibleTimeoutMs, 1_000, 300_000);

        _grace = new System.Timers.Timer(HideGraceMs) { AutoReset = false };
        _grace.Elapsed += (_, _) => HideNow(Native.HideReasonGrace);

        _watchdog = new System.Timers.Timer(VisibleTimeoutMs) { AutoReset = false };
        _watchdog.Elapsed += (_, _) => HideNow(Native.HideReasonWatchdog);
    }

    /// <summary>捕捉開始：升起接收層並重置保險。</summary>
    public void OnCaptured()
    {
        if (CaptureDisabled)
        {
            _evidence.Line("capture disabled — refusing to raise");
            return;
        }

        _hit.ShowAll();
        _grace.Stop();
        _watchdog.Stop();
        _watchdog.Start();
        _evidence.Line($"guard #3 armed ({VisibleTimeoutMs} ms)");
    }

    /// <summary>保險 #1／#2：mouse-up 或觸發鍵放開 → 寬限後收層。</summary>
    public void OnReleaseRequested(string reason)
    {
        _evidence.Line($"release requested: {reason}");
        _watchdog.Stop();
        _grace.Stop();
        _grace.Start();
    }

    /// <summary>drop 已由 shell 解析完成 → 立即收層（不寬限）。</summary>
    public void OnDrop()
    {
        _evidence.Line("drop resolved -> immediate hide");
        StopTimers();
        HideNow(Native.HideReasonDrop);
    }

    /// <summary>
    /// 使用者主動結束（Esc 取消、Enter 直接執行）：**立即**收層。
    /// 規格 §4.3：Esc 立即消失，不等淡出動畫；250 ms 寬限是為了讓放手當下的 drop
    /// 能落到接收層上，Esc／Enter 之後並沒有 drop 要等。
    /// </summary>
    public void OnUserResolved(string what)
    {
        _evidence.Line($"user resolved: {what} -> immediate hide");
        StopTimers();
        HideNow(Native.HideReasonManual);
    }

    /// <summary>保險 #5：緊急停用熱鍵（Ctrl+Alt+Shift+Q）。收層並停用捕捉直到重啟。</summary>
    public void OnKillSwitch()
    {
        CaptureDisabled = true;
        _evidence.Line("*** KILL SWITCH: capture disabled until restart ***");
        StopTimers();
        HideNow(Native.HideReasonKillSwitch);
    }

    private void StopTimers()
    {
        _watchdog.Stop();
        _grace.Stop();
    }

    /// <summary>
    /// 決定收層。實際的 ShowWindow 由 UI 執行緒在收到 <see cref="Native.WM_APP_FORCE_HIDE"/> 時執行
    /// （定時器在執行緒池執行緒上觸發，不可跨執行緒操作視窗）。
    /// </summary>
    private void HideNow(int reasonCode)
    {
        StopTimers();
        if (!_hit.IsVisible) return;          // 以作業系統真實狀態判斷
        _hit.RequestHideAll(reasonCode);
    }

    public void Dispose() => StopTimers();
}
