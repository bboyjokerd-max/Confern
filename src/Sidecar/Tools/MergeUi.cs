using System.Windows;

namespace Sidecar;

/// <summary>
/// 合併排序視窗的**對外唯一入口**與生命週期擁有者（比照 <see cref="CropUi"/> 對裁切視窗、
/// <see cref="QrTextUi"/> 對文字視窗、<see cref="SettingsUi"/> 對設定頁的關係）。
///
/// ## 執行緒
///   它**不自己開執行緒**：視窗建在 <see cref="SettingsUi"/> 那條共用的 WPF UI 執行緒上
///   （一個 AppDomain 只准有一個 <c>Application</c>，見 SettingsUi 的說明）。
///   開窗走 <see cref="SettingsUi.PostToSharedUiThread"/>（**不等待**——開 modal 視窗要是等下去，
///   等於把主訊息迴圈凍在那裡）；自測動作走 <see cref="SettingsUi.RunOnUiThread"/>（要等）。
///
/// ## 單一實例與「關掉之後再開」（這一區的三條規矩是**照抄 CropUi 的**，專案已經為它付過代價）
///   ① 已經開著就**拒絕**第二次要求——兩個排序視窗會各自產生一份順序，後確認的把先確認的蓋掉，
///      而使用者看到的是同一批檔的兩種順序，沒有任何線索知道哪一份生效；
///   ② 關掉之後要能再開，靠的是 <c>Closed</c> 事件把 <c>_isOpen</c> 與 <c>_window</c> 放掉，
///      而**不是**靠「呼叫 <see cref="Close"/> 的那個人記得放掉」；
///   ③ 開窗失敗**一定要 catch 並大聲記 log**：建起來但顯示不出來的視窗會變成螢幕上關不掉的幽靈，
///      所以那個 catch 是唯一知道「這次開窗失敗」的地方，它要把視窗收乾淨。
///
/// ## 不碰使用者的滑鼠
///   本專案 2026-10-06 的規則：**預設跑套件絕不碰使用者的滑鼠**。所以「拖曳排序」的驗證走
///   <see cref="SelfTestDrag"/>（把座標餵進真正的那三個方法），而不是合成輸入。
///   ⚠ 這也是這個視窗**刻意不用** WPF 的 <c>DragDrop.DoDragDrop</c> 的原因（它會進入自己的模態
///   訊息迴圈，於是拖曳只能靠真滑鼠驗＝驗不到），理由寫在 <see cref="MergeWindow"/> 的類別說明。
/// </summary>
internal static class MergeUi
{
    /// <summary>目前開著的排序視窗（只在共用的 UI 執行緒上存取）。</summary>
    private static MergeWindow? _window;

    /// <summary><see cref="IsOpen"/> 的快照。UI 執行緒寫、任何執行緒讀，走 <c>volatile</c>。</summary>
    private static volatile bool _isOpen;

    /// <summary>每次開窗遞增：讓「上一輪的關閉要求」不會誤關剛開好的新視窗（與裁切視窗同一條規矩）。</summary>
    private static long _generation;

    private static readonly object Gate = new();

    public static bool IsOpen => _isOpen;

    /// <summary>這張視窗正在排序的那一批來源檔（自測的證據用；**原始順序**，不是目前的順序）。</summary>
    private static volatile string[] _batch = Array.Empty<string>();

    /// <summary>開啟排序視窗。**立刻返回**（不阻塞呼叫端），結果由 <paramref name="onConfirmed"/> 帶回。</summary>
    /// <param name="paths">這一整批要合併的檔案，**依目前（來源）順序**；視窗的初始順序就是它。</param>
    /// <param name="onConfirmed">使用者按下確認時被呼叫一次，帶著**他排出來的順序**。
    ///   ⚠ <b>它是在 WPF UI 執行緒上被叫的</b>，而且**不是**主訊息迴圈執行緒：起 worker／碰視覺層
    ///   請自己排回主執行緒（正式路徑就是這樣做的：排進佇列，由既有的週期回呼抽乾）。取消時**不會**被呼叫。</param>
    /// <param name="log">日誌（可以不給）。開不起來時只記 log、什麼都不做（不回饋、不丟例外）。</param>
    public static void Open(string[] paths, Action<string[]> onConfirmed, Action<string>? log)
    {
        try
        {
            if (paths == null || paths.Length == 0)
            {
                Log(log, "merge ui: open rejected (there is nothing to order)");
                return;
            }
            if (SettingsUi.OnSharedUiThread)
            {
                // 從 UI 執行緒自己開窗會當場自我等待（RunOnUiThread 那條路），直接拒絕並說出來。
                Log(log, "merge ui: open rejected (called from the WPF UI thread; it would deadlock)");
                return;
            }

            lock (Gate)
            {
                if (_isOpen)
                {
                    Log(log, "merge ui: open ignored (already open)");
                    return;
                }

                long generation = ++_generation;
                string[] batch = paths.ToArray();
                // ⚠ 回呼存在**欄位**裡而不是閉包在下面那個 lambda 裡：交回順序的時機是視窗的
                //    <c>Closed</c> 事件（見 OnMergeWindowClosed），而那是靜態的處理常式，
                //    看不到開窗這個 lambda 的區域變數。生命週期與 _window 相同：開窗時寫、關窗時清。
                _confirmed = onConfirmed;
                SettingsUi.EnsureSharedUiThread(log);

                bool posted = SettingsUi.PostToSharedUiThread(() =>
                {
                    // 這一輪已經被後來的 Open／Close 取代：不要開
                    if (generation != Volatile.Read(ref _generation)) return;
                    try
                    {
                        var window = new MergeWindow(batch, log);
                        _window = window;
                        _batch = batch;
                        _isOpen = true;
                        window.Closed += OnMergeWindowClosed;
                        window.ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        // 建起來但顯示不出來的視窗不可以留下來（與裁切視窗／設定頁同一條規矩）。
                        if (_window != null)
                        {
                            _window.Closed -= OnMergeWindowClosed;
                            try
                            {
                                _window.Close();
                            }
                            catch (Exception closeFailure)
                            {
                                Log(log, $"merge ui: could not close the window that failed to show " +
                                         $"({closeFailure.GetType().Name}: {closeFailure.Message})");
                            }
                            _window = null;
                        }
                        _isOpen = false;
                        Log(log, $"merge ui: the window failed to open ({ex.GetType().Name}: {ex.Message})");
                    }
                }, log, "merge window");

                if (!posted)
                {
                    _confirmed = null;
                    Log(log, "merge ui: the open request could not be posted (nobody is pumping the UI queue)");
                    return;
                }

                Log(log, $"merge ui: open requested for {batch.Length} file(s)");
            }
        }
        catch (Exception ex)
        {
            Log(log, $"merge ui: open threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 關閉目前開著的排序視窗（取消語意：不交回任何順序）。沒有開著時是 no-op。
    /// 走這條路而不是直接 <c>_window.Close()</c>，是為了讓「誰把 <c>_isOpen</c> 放掉」仍然只有
    /// <c>Closed</c> 那一個地方。
    /// </summary>
    public static void Close()
    {
        try
        {
            lock (Gate)
            {
                if (!_isOpen) return;
                _generation++;      // 讓還在佇列裡的開窗要求失效
                SettingsUi.PostToSharedUiThread(() =>
                {
                    try
                    {
                        _window?.Close();
                    }
                    catch (Exception)
                    {
                        // 關不掉不是錯誤（視窗可能已經在關）；狀態由 Closed 事件收拾。
                    }
                }, null, "close merge window");
            }
        }
        catch (Exception)
        {
            // 見上
        }
    }

    private static void OnMergeWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            // 「按下確認」與「視窗關掉」是兩件事：使用者按 X 或 Esc 關掉時，window.Order 仍然是 null
            // ⇒ 只有真的有訂單才交回去（取消不會產生任何檔案，見 MergeWindow.CancelWithLog）。
            MergeWindow? window = _window;
            if (window != null)
            {
                window.Closed -= OnMergeWindowClosed;
                if (window.Order is { } order && order.Length > 0)
                {
                    // ⚠ 這裡仍然是 **UI 執行緒**（Closed 是在它上面發生的）。回呼的實作必須自己排回
                    //   主執行緒，理由見 Open 的說明。
                    _confirmed?.Invoke(order);
                }
            }
        }
        catch (Exception)
        {
            // 回呼丟例外不可以讓 UI 執行緒倒下（本專案為此付過代價）。
        }
        finally
        {
            _window = null;
            _isOpen = false;
            _confirmed = null;
        }
    }

    /// <summary>
    /// 這一輪的確認回呼。存在**這裡**而不是閉包在開窗那個 lambda 裡，是因為交回順序的時機是
    /// <c>Closed</c>（見 <see cref="OnMergeWindowClosed"/>），而那個事件處理常式是靜態的。
    /// 生命週期與 <see cref="_window"/> 完全相同：開窗時寫、關窗時清。
    /// </summary>
    private static volatile Action<string[]>? _confirmed;

    // =====================================================================================
    // 自測接縫（驗收證據用；正式路徑不經過這裡）
    // =====================================================================================

    /// <summary>自測：視窗在不在（在 UI 執行緒上問，要等）。</summary>
    public static bool SelfTestWindowPresent(int timeoutMs, out string detail, Action<string>? log)
    {
        // ⚠ `out` 參數不可以在 lambda 裡被指派（CS1628）：先在區域變數裡寫，回來再搬出去。
        string report = "";
        bool found = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge window present", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null || !window.IsLoaded)
            {
                report = "(no merge window is open)";
                return null;
            }
            found = true;
            report = window.SelfTestState();
            return null;
        });
        detail = ran ? report : failure;
        return ran && found;
    }

    /// <summary>自測：一句話的狀態（視窗不在時也回一句話，不丟例外）。</summary>
    public static string SelfTestState()
    {
        string report = "(no merge window is open)";
        bool ran = SettingsUi.RunOnUiThread(5000, null, "selftest merge state", out string failure, () =>
        {
            MergeWindow? window = _window;
            report = window == null ? "(no merge window is open)" : window.SelfTestState();
            return null;
        });
        return ran ? report : $"(unavailable: {failure})";
    }

    /// <summary>自測：目前的順序（視窗不在時回空陣列）。</summary>
    public static string[] SelfTestOrder()
    {
        string[] report = Array.Empty<string>();
        SettingsUi.RunOnUiThread(5000, null, "selftest merge order", out _, () =>
        {
            MergeWindow? window = _window;
            report = window == null ? Array.Empty<string>() : window.SelfTestOrder();
            return null;
        });
        return report;
    }

    /// <summary>自測：一句話的順序快照（格式見 <see cref="MergeWindow.SelfTestOrderLine"/>）。</summary>
    public static string SelfTestOrderLine()
    {
        string report = "(no merge window is open)";
        bool ran = SettingsUi.RunOnUiThread(5000, null, "selftest merge order line", out string failure, () =>
        {
            MergeWindow? window = _window;
            report = window == null ? "(no merge window is open)" : window.SelfTestOrderLine();
            return null;
        });
        return ran ? report : $"(unavailable: {failure})";
    }

    /// <summary>自測：畫面上畫出來的列（證明「畫面」與「順序」是同一份東西）。</summary>
    public static string SelfTestRowsLine()
    {
        string report = "(no merge window is open)";
        bool ran = SettingsUi.RunOnUiThread(5000, null, "selftest merge rows", out string failure, () =>
        {
            MergeWindow? window = _window;
            report = window == null ? "(no merge window is open)" : window.SelfTestRowsLine();
            return null;
        });
        return ran ? report : $"(unavailable: {failure})";
    }

    /// <summary>自測：一次程式化拖曳（**餵進真正的那三個方法**，不合成滑鼠輸入）。</summary>
    public static bool SelfTestDrag(int fromIndex, double deltaRows, int timeoutMs,
                                   out string detail, Action<string>? log)
    {
        // ⚠ `out` 參數**不可以**在 lambda 裡被指派（CS1628），所以先在區域變數裡寫、
        //    等 RunOnUiThread 回來之後再搬出來。這是這個專案既有的自測接縫寫法（見 CropUi）。
        string report = "";
        bool moved = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge drag", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null)
            {
                report = "(no merge window is open)";
                return null;
            }
            moved = window.SelfTestDrag(fromIndex, deltaRows);
            report = $"drag from={fromIndex + 1} deltaRows={deltaRows:0.##} moved={moved} " +
                     window.SelfTestOrderLine();
            return null;
        });
        detail = ran ? report : failure;
        return ran && moved;
    }

    /// <summary>自測：拖曳之後拖回原處，順序必須完全還原。</summary>
    public static bool SelfTestDragAndReturn(int fromIndex, double deltaRows, int timeoutMs,
                                            out string detail, Action<string>? log)
    {
        string report = "";
        bool restored = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge drag and return", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null)
            {
                report = "(no merge window is open)";
                return null;
            }
            restored = window.SelfTestDragAndReturn(fromIndex, deltaRows);
            report = $"dragAndReturn from={fromIndex + 1} deltaRows={deltaRows:0.##} restored={restored} " +
                     window.SelfTestOrderLine();
            return null;
        });
        detail = ran ? report : failure;
        return ran && restored;
    }

    /// <summary>自測：按下第 <paramref name="index"/> 列上**真正的移除鈕**。</summary>
    public static bool SelfTestRemoveRow(int index, int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "";
        bool pressed = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge remove", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null)
            {
                report = "(no merge window is open)";
                return null;
            }
            pressed = window.SelfTestRemoveRow(index);
            report = $"remove row={index + 1} pressed={pressed} notice='{window.SelfTestNotice}' " +
                     window.SelfTestOrderLine();
            return null;
        });
        detail = ran ? report : failure;
        return ran && pressed;
    }

    /// <summary>自測：按下**真正的分頁鈕**（direction &lt; 0 ＝ 上）。</summary>
    public static bool SelfTestPage(int direction, int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "";
        bool pressed = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge page", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null)
            {
                report = "(no merge window is open)";
                return null;
            }
            pressed = window.SelfTestPage(direction);
            report = $"page direction={direction} pressed={pressed} {window.SelfTestRowsLine()}";
            return null;
        });
        detail = ran ? report : failure;
        return ran && pressed;
    }

    /// <summary>自測：按下**真正的確認鈕**。</summary>
    public static bool SelfTestConfirm(int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "(nothing was confirmed)";
        bool found = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge confirm", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null || !window.IsLoaded) return "(no merge window is open)";
            found = true;
            window.SelfTestConfirm();
            report = window.Order is { Length: > 0 } order
                ? $"confirmed {order.Length} file(s) [{string.Join(",", order.Select(Path.GetFileName))}]"
                : $"the window refused the confirmation (notice='{window.SelfTestNotice}')";
            return null;
        });
        // ⚠ 回傳值必須是「按到了**而且**有視窗」：只回 ran 會讓「沒有視窗」也算成成功
        //    （CropUi 那一版確實會這樣回），而那一格正是「視窗在不在」的斷言最需要的地方。
        detail = ran && found ? report : failure;
        Log(log, "merge ui: " + detail);
        return ran && found;
    }

    /// <summary>自測：按下**真正的取消鈕**。</summary>
    public static bool SelfTestCancel(int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "(nothing was cancelled)";
        bool found = false;
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest merge cancel", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null || !window.IsLoaded) return "(no merge window is open)";
            found = true;
            window.SelfTestCancel();
            report = $"cancel clicked (order={(window.Order == null ? "none" : "SET")})";
            return null;
        });
        detail = ran && found ? report : failure;
        Log(log, "merge ui: " + detail);
        return ran && found;
    }

    /// <summary>自測：把這張視窗畫成一張 PNG（離屏合成）。</summary>
    public static bool DumpFrame(string path, int timeoutMs, out string detail, Action<string>? log)
    {
        string report = "";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "merge dump frame", out string failure, () =>
        {
            MergeWindow? window = _window;
            if (window == null || !window.IsLoaded) return "(no merge window is open)";
            if (!window.TryDump(path, out string dumpDetail)) return $"({dumpDetail})";
            report = dumpDetail;
            return null;
        });

        detail = ran ? $"dump: {path} ({report})" : $"dump: {failure}";
        Log(log, "merge ui: " + detail);
        return ran;
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
