using System.Windows;

namespace Sidecar;

/// <summary>
/// 讀 QR 的文字視窗的**對外唯一入口**與生命週期擁有者（比照 <see cref="CropUi"/> 對裁切視窗、
/// <see cref="SettingsUi"/> 對設定頁的關係）。
///
/// ## 執行緒
///   它**不自己開執行緒**、也不自己建 <c>Application</c>：視窗建在 <see cref="SettingsUi"/>
///   那條共用的 WPF UI 執行緒上（一個 AppDomain 只准有一個 <c>Application</c>，
///   所以「這個行程裡怎麼開一個 WPF 視窗」只有那一套，見 SettingsUi 的說明）。
///   開窗走 <see cref="SettingsUi.PostToSharedUiThread"/>（**不等待**：開 modal 視窗要是等下去，
///   等於把主訊息迴圈凍在那裡）；自測動作走 <see cref="SettingsUi.RunOnUiThread"/>（要等）。
///
/// ## 單一實例與「關掉之後再開」
///   與 <see cref="CropUi"/> 同一條規矩（這個專案已經為它付過代價）：
///   ① 已經開著就**拒絕**第二次要求——兩個文字視窗只會讓使用者懷疑自己看到的是哪一張圖的內容；
///   ② 關掉之後要能再開，靠的是 <c>Closed</c> 事件把 <c>_isOpen</c> 與 <c>_window</c> 放掉，
///      而不是靠「呼叫 Close 的那個人記得放掉」；
///   ③ 開窗失敗**一定要 catch 並大聲記 log**：建起來但顯示不出來的視窗會變成螢幕上關不掉的幽靈。
///
/// ## 不碰使用者的滑鼠、也不碰他的螢幕
///   自測一律走 <see cref="SelfTestCopy"/>／<see cref="SelfTestClose"/> 這類接縫，
///   而它們打的是**真的按鈕**與**真的關窗動作**（見 QrTextWindow 的說明）。
/// </summary>
internal static class QrTextUi
{
    /// <summary>目前開著的文字視窗（只在共用的 UI 執行緒上存取）。</summary>
    private static QrTextWindow? _window;

    /// <summary><see cref="IsOpen"/> 的快照。UI 執行緒寫、任何執行緒讀，走 <c>volatile</c>。</summary>
    private static volatile bool _isOpen;

    /// <summary>每次開窗遞增：讓「上一輪的關閉要求」不會誤關剛開好的新視窗（與裁切視窗同一條規矩）。</summary>
    private static long _generation;

    private static readonly object Gate = new();

    public static bool IsOpen => _isOpen;

    /// <summary>
    /// 開啟文字視窗顯示 <paramref name="text"/>。**立刻返回**（不阻塞呼叫端）。
    /// 開不起來時只記 log、什麼都不做（不回饋、不丟例外）——顯示一段文字失敗不該讓整個程式倒下。
    /// </summary>
    public static void Open(string text, string sourceName, Action<string>? log)
    {
        try
        {
            if (text.Length == 0)
            {
                Log(log, "qr text ui: open rejected (there is no text to show)");
                return;
            }
            if (SettingsUi.OnSharedUiThread)
            {
                // 從 UI 執行緒自己開窗會當場自我等待（RunOnUiThread 那條路），直接拒絕並說出來。
                Log(log, "qr text ui: open rejected (called from the WPF UI thread; it would deadlock)");
                return;
            }

            lock (Gate)
            {
                if (_isOpen)
                {
                    Log(log, "qr text ui: open ignored (already open)");
                    return;
                }

                long generation = ++_generation;
                SettingsUi.EnsureSharedUiThread(log);

                bool posted = SettingsUi.PostToSharedUiThread(() =>
                {
                    // 這一輪已經被後來的 Open／Close 取代：不要開
                    if (generation != Volatile.Read(ref _generation)) return;
                    try
                    {
                        var window = new QrTextWindow(text, sourceName, log);
                        _window = window;
                        _isOpen = true;
                        window.Closed += OnTextWindowClosed;
                        window.ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        // 建起來但顯示不出來的視窗不可以留下來（與裁切視窗／設定頁同一條規矩）。
                        if (_window != null)
                        {
                            _window.Closed -= OnTextWindowClosed;
                            try
                            {
                                _window.Close();
                            }
                            catch (Exception closeFailure)
                            {
                                Log(log, $"qr text ui: could not close the window that failed to show " +
                                         $"({closeFailure.GetType().Name}: {closeFailure.Message})");
                            }
                            _window = null;
                        }
                        _isOpen = false;
                        Log(log, $"qr text ui: FAILED to show the window ({ex.GetType().Name}: {ex.Message})");
                    }
                }, log, "open the read-qr text window");

                Log(log, posted
                    ? $"qr text ui: open requested ({text.Length} chars, source={sourceName})"
                    : "qr text ui: open could not be posted (the WPF UI thread is not available)");
            }
        }
        catch (Exception ex)
        {
            // 「不靜默失敗」不等於「把例外丟回主訊息迴圈」：主程式不該因為這個視窗而倒下。
            Log(log, $"qr text ui: open failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 關閉文字視窗（如果開著）。可在任何執行緒呼叫；實際關閉動作排到 UI 執行緒。
    /// 走的是**真正的關窗動作**（<see cref="Window.Close"/>），與使用者按下標題列的 X 同一條路。
    /// </summary>
    public static void Close()
    {
        try
        {
            long generation;
            lock (Gate)
            {
                if (_generation == 0) return;
                generation = _generation;
            }
            SettingsUi.PostToSharedUiThread(() =>
            {
                if (generation != Volatile.Read(ref _generation)) return;
                _window?.Close();
            }, null, "close the read-qr text window");
        }
        catch (Exception ex)
        {
            Log(null, $"qr text ui: close failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 視窗關閉時的**唯一**清理點。少了它，「關掉之後再開」會永遠被 <c>_isOpen</c> 擋住
    /// ——而日誌上只看得到 <c>open ignored (already open)</c>，看起來像使用者的操作錯誤。
    /// </summary>
    private static void OnTextWindowClosed(object? sender, EventArgs e)
    {
        if (sender is QrTextWindow w)
        {
            try
            {
                w.Closed -= OnTextWindowClosed;
            }
            catch (Exception)
            {
                // 解不掉事件的後果只是多留一個委派，不值得讓關窗失敗。
            }
        }
        _window = null;
        _isOpen = false;
    }

    // =====================================================================================
    // 自測接縫（`--qr-open-selftest`；正式路徑不經過這裡）
    //
    // 每一條都是「排到共用的 UI 執行緒、等它做完、把結果帶回來」，而且動的**都是真物件**：
    // 真的視窗、真的複製鈕、真的關窗動作。這裡沒有一條是「測試專用的替代實作」。
    // =====================================================================================

    /// <summary>
    /// 視窗裡現在顯示的**逐字元內容**（自測的斷言對象；期望值來自產品的解碼輸出）。
    /// 沒有視窗時回傳 null——那是「沒有東西可以比對」，不是「內容是空字串」。
    /// </summary>
    public static string? SelfTestText()
    {
        string? text = null;
        SettingsUi.RunOnUiThread(5000, null, "selftest qr text", out _, () =>
        {
            text = _window?.TextContent;
            return null;
        });
        return text;
    }

    /// <summary>按下**真正的複製鈕**，回傳它寫進剪貼簿的字（沒有視窗或失敗＝空字串）。</summary>
    public static string SelfTestCopy(int timeoutMs, out string detail, Action<string>? log)
    {
        string copied = "";
        string report = "(nothing was copied)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest qr copy", out string failure, () =>
        {
            QrTextWindow? window = _window;
            if (window == null) return "(no text window is open)";
            window.SelfTestCopy();
            copied = window.CopiedText;
            report = $"the copy button put {copied.Length} chars on the clipboard";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, "qr text ui: " + detail);
        return copied;
    }

    /// <summary>把焦點給文字框（證明「唯讀但可選取」那條路是活的）。</summary>
    public static bool SelfTestFocusBody()
    {
        bool focused = false;
        SettingsUi.RunOnUiThread(5000, null, "selftest qr focus", out _, () =>
        {
            focused = _window?.SelfTestFocusBody() ?? false;
            return null;
        });
        return focused;
    }

    /// <summary>
    /// 視窗**真的存在於螢幕上**嗎（而不只是「有一個物件」）。與裁切視窗那一條同一條理由：
    /// <see cref="IsOpen"/> 只是「上一次開啟有沒有成功」的快照，一個 hwnd 都沒有的視窗照樣能讓它綠燈。
    /// </summary>
    public static bool SelfTestWindowPresent(int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "(no text window)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest qr window present", out string failure, () =>
        {
            QrTextWindow? window = _window;
            if (window == null) return "(no text window object exists)";
            System.Windows.Interop.HwndSource? source =
                PresentationSource.FromVisual(window) as System.Windows.Interop.HwndSource;
            if (!window.IsLoaded || source == null || source.Handle == IntPtr.Zero)
            {
                return $"(a text window object exists but it is not on screen; IsLoaded={window.IsLoaded} " +
                       $"IsVisible={window.IsVisible} size={window.ActualWidth}x{window.ActualHeight} " +
                       $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")})";
            }
            report = $"a text window is loaded and has an hwnd " +
                     $"(0x{source.Handle.ToInt64().ToString("X")}, {window.ActualWidth}x{window.ActualHeight})";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, "qr text ui: " + detail);
        return ran;
    }

    /// <summary>一句話的狀態（含 hwnd 與「唯讀但可選取」那一組旗標）。</summary>
    public static string SelfTestState()
    {
        string report = "(no text window is open)";
        bool ran = SettingsUi.RunOnUiThread(5000, null, "selftest qr state", out string failure, () =>
        {
            QrTextWindow? window = _window;
            report = window == null ? "(no text window is open)" : window.SelfTestState();
            return null;
        });
        return ran ? report : $"(unavailable: {failure})";
    }

    /// <summary>日誌。**不丟例外**（日誌本身壞掉不可以讓功能失敗）。</summary>
    private static void Log(Action<string>? log, string text)
    {
        if (log == null) return;
        try
        {
            log(text);
        }
        catch (Exception)
        {
            // 見上
        }
    }
}
