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

    /// <summary>
    /// 其他覆蓋層（視覺層）。**決定權仍然只在本類別**：這裡註冊的是「誰要被收」，
    /// 不是「誰可以收」。之所以需要它：先前只看命中層，所以「命中層沒升起、但視覺層已顯示」
    /// 的情況沒有任何一條路徑收得掉（卡住的覆蓋層），連 watchdog 也救不了。
    /// </summary>
    private readonly List<(Func<bool> IsVisible, Action<int> RequestHide)> _overlays = new();

    public bool CaptureDisabled { get; private set; }
    public int VisibleTimeoutMs { get; private set; }
    public bool IsArmed => _watchdog.Enabled;

    /// <summary>逾時的夾制規則**只有這一處**（建構子與熱套用都走它），否則兩邊會漂移。</summary>
    private static int ClampTimeout(int ms) => Math.Clamp(ms, 1_000, 300_000);

    public ReleaseGuard(Evidence evidence, HitLayer hit, int visibleTimeoutMs = DefaultVisibleTimeoutMs)
    {
        _evidence = evidence;
        _hit = hit;
        VisibleTimeoutMs = ClampTimeout(visibleTimeoutMs);

        _grace = new System.Timers.Timer(HideGraceMs) { AutoReset = false };
        _grace.Elapsed += (_, _) => HideNow(Native.HideReasonGrace);

        _watchdog = new System.Timers.Timer(VisibleTimeoutMs) { AutoReset = false };
        _watchdog.Elapsed += (_, _) => HideNow(Native.HideReasonWatchdog);
    }

    /// <summary>
    /// 把一個覆蓋層納入收層廣播。**這不會新增第二個權威**——只有本類別會呼叫它。
    /// </summary>
    public void AddOverlay(Func<bool> isVisible, Action<int> requestHide) =>
        _overlays.Add((isVisible, requestHide));

    /// <summary>
    /// 換接收層逾時（設定頁按下「儲存」時走這裡）。若此刻正好武裝中，watchdog 要用新值**重新起算**，
    /// 否則使用者的感覺是「改了沒用」。
    /// </summary>
    public void SetVisibleTimeout(int ms)
    {
        int clamped = ClampTimeout(ms);
        if (clamped == VisibleTimeoutMs) return;
        int previous = VisibleTimeoutMs;
        VisibleTimeoutMs = clamped;
        _watchdog.Interval = clamped;
        bool rearmed = _watchdog.Enabled;
        if (rearmed) { _watchdog.Stop(); _watchdog.Start(); }
        _evidence.Line($"guard visible timeout changed: {previous} ms -> {clamped} ms" +
                       (rearmed ? " (watchdog restarted with the new value)" : ""));
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

    /// <summary>
    /// 保險 #5 是否已經扣下。**一旦 true 就永不清除**（直到行程結束）——「直到重啟」這句話的實作。
    ///
    /// 托盤的暫停／繼續共用 <see cref="CaptureDisabled"/> 這一個狀態（見 <see cref="Pause"/>），
    /// 但**不可以**共用復原的權力：少了這個閂，使用者按下緊急停用之後再點一次托盤的
    /// 「繼續轉換」，就會悄悄把捕捉重新武裝起來，而規格 §4.2.2 的保險 #5 明文是「直到重啟」。
    /// 那會讓一條安全保證只剩文件上有寫。
    /// </summary>
    public bool KillSwitchLatched { get; private set; }

    /// <summary>保險 #5：緊急停用熱鍵（Ctrl+Alt+Shift+Q）。收層並停用捕捉直到重啟。</summary>
    public void OnKillSwitch()
    {
        KillSwitchLatched = true;
        _evidence.Line("*** KILL SWITCH: capture disabled until restart ***");
        SetCaptureDisabled(true, "kill switch", Native.HideReasonKillSwitch);
    }

    /// <summary>
    /// 暫停捕捉（托盤的「暫停轉換」）。與保險 #5 是**同一個狀態**，但**不是同一個語意**：
    /// 這是可以復原的，而且不會留下 <see cref="KillSwitchLatched"/>。
    ///
    /// 為什麼不能直接叫 <see cref="OnKillSwitch"/>（第一版就是那樣寫的）：那會讓托盤的暫停
    /// 印出 `*** KILL SWITCH ***`——一來日誌說謊（m1a 的 S3.8 正是斷言那一行，於是那條斷言
    /// 會被托盤的暫停滿足），二來「直到重啟」與「可以再按繼續」兩個說法互相矛盾。
    /// </summary>
    public void Pause(string why)
    {
        if (CaptureDisabled)
        {
            _evidence.Line($"guard: pause requested ({why}) but capture was already disabled; nothing to do");
            return;
        }
        SetCaptureDisabled(true, why, Native.HideReasonManual);
    }

    /// <summary>
    /// 重新允許捕捉（托盤的「繼續轉換」）。**緊急停用之後一律拒絕**。
    ///
    /// 為什麼被拒絕時要大聲寫日誌：使用者按了按鈕卻什麼都沒發生，正是他最需要日誌解釋的時刻；
    /// 沉默會讓他以為程式壞了，而不是「保險 #5 照規格要求重啟才復原」。
    ///
    /// 回傳值與 <see cref="ResumeRefusals"/>（2026-10-02 可證偽性稽核 P1-8）：日誌行只能證明
    /// 「我們這樣說」，驗收端讀 `CaptureDisabled` 也證明不了什麼——拒絕與否它都是 true。
    /// 「守衛拒絕過幾次」是**唯一**能讓「拒絕真的發生過」變成可觀測事實的東西：
    /// 把 <see cref="KillSwitchLatched"/> 這道閂拿掉，這個計數就會停在 0。
    /// </summary>
    public bool Resume()
    {
        if (KillSwitchLatched)
        {
            ResumeRefusals++;
            _evidence.Line("guard: REFUSED to resume: the kill switch (insurance #5) latched capture off " +
                           "until restart; restart the program to capture again");
            return false;
        }
        if (!CaptureDisabled)
        {
            _evidence.Line("guard: resume requested but capture was not disabled; nothing to do");
            return true;
        }
        SetCaptureDisabled(false, "tray resume", Native.HideReasonManual);
        return true;
    }

    /// <summary>
    /// 保險 #5 閂上之後，<see cref="Resume"/> 被拒絕的次數。單調遞增，不會被重設。
    /// 驗收端用它把「守衛拒絕復原」變成事實而不是敘事（P1-8）。
    /// </summary>
    public int ResumeRefusals { get; private set; }

    /// <summary>
    /// 停用或復原捕捉的**唯一實作**（兩條路徑共用，否則兩個方向會漂移）。
    /// 停用時一定要收層：留下一個沒有人能收掉的覆蓋層才是真正的災難。
    /// </summary>
    private void SetCaptureDisabled(bool disabled, string why, int hideReason)
    {
        CaptureDisabled = disabled;
        _evidence.Line(disabled
            ? $"guard: capture DISABLED ({why})"
            : $"guard: capture ENABLED again ({why})");
        if (!disabled) return;

        StopTimers();
        HideNow(hideReason);
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
        if (!_hit.IsVisible && !AnyOverlayVisible()) return;   // 以作業系統真實狀態判斷
        _hit.RequestHideAll(reasonCode);
        foreach ((Func<bool> _, Action<int> requestHide) in _overlays) requestHide(reasonCode);
    }

    private bool AnyOverlayVisible()
    {
        foreach ((Func<bool> isVisible, Action<int> _) in _overlays)
            if (isVisible()) return true;
        return false;
    }

    public void Dispose() => StopTimers();
}
