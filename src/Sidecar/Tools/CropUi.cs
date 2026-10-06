using System.Collections.Concurrent;
using System.Windows;

namespace Sidecar;

/// <summary>
/// 裁切視窗的**對外唯一入口**與生命週期擁有者（比照 <see cref="SettingsUi"/> 對設定頁的關係）。
///
/// ## 執行緒
///   它**不自己開執行緒**：視窗建在 <see cref="SettingsUi"/> 那條共用的 WPF UI 執行緒上
///   （理由：一個 AppDomain 只准有一個 <c>Application</c>，見 <c>SettingsUi</c> 裡的說明）。
///   開窗走 <see cref="SettingsUi.PostToSharedUiThread"/>（**不等待**——開 modal 視窗要是等下去，
///   等於把主訊息迴圈凍在那裡）；其餘的自測動作走 <see cref="SettingsUi.RunOnUiThread"/>（要等）。
///
/// ## 單一實例與「關掉之後再開」
///   兩件事其實是同一件事，而這個專案已經為它付過代價（設定頁那一輪）：
///   ① 已經開著就**拒絕**第二次要求（兩個視窗會各自產生一個矩形，後確認的把先確認的蓋掉）；
///   ② 關掉之後要能再開，靠的是 <c>Closed</c> 事件把 <c>_isOpen</c> 與 <c>_window</c> 放掉，
///      而不是靠「呼叫 <see cref="Close"/> 的那個人記得放掉」。
///   ③ 開窗失敗**一定要 catch 並大聲記 log**：建起來但顯示不出來的視窗會變成螢幕上關不掉的幽靈，
///      所以那個 catch 是唯一知道「這次開窗失敗」的地方，它要把視窗收乾淨。
///
/// ## 不碰使用者的滑鼠
///   本專案 2026-10-06 的新規則：**預設跑套件絕不碰使用者的滑鼠**。所以拖曳的驗證走
///   <see cref="SelfTestDrag"/>（把座標餵進真正的那三個方法），而不是合成輸入。
///   要真的動滑鼠的診斷在別的地方（而且必須是可選旗標）。
/// </summary>
internal static class CropUi
{
    /// <summary>目前開著的裁切視窗（只在共用的 UI 執行緒上存取）。</summary>
    private static CropWindow? _window;

    /// <summary><see cref="IsOpen"/> 的快照。UI 執行緒寫、任何執行緒讀，走 <c>volatile</c>。</summary>
    private static volatile bool _isOpen;

    /// <summary>每次開窗遞增：讓「上一輪的關閉要求」不會誤關剛開好的新視窗（與設定頁同一條規矩）。</summary>
    private static long _generation;

    private static readonly object Gate = new();

    public static bool IsOpen => _isOpen;

    /// <summary>這張視窗正在預覽的來源檔（自測的證據用）。</summary>
    private static volatile string _previewPath = "";

    /// <summary>
    /// 開啟裁切視窗。**立刻返回**（不阻塞呼叫端），結果由 <paramref name="onConfirmed"/> 帶回。
    ///
    /// <paramref name="paths"/>：這一整批要裁的檔案；**第一張**是視窗裡顯示（＝框矩形的那一張）。
    /// 其餘的只會用到「每一張依自己的尺寸換算」那一條——框出來的是正規化矩形。
    ///
    /// <paramref name="onConfirmed"/>：使用者按下確認時被呼叫一次，帶著**正規化**矩形。
    ///   ⚠ <b>它是在 WPF UI 執行緒上被叫的</b>，而且**不是**主訊息迴圈執行緒：
    ///   起 worker／碰視覺層請自己排回主執行緒（正式路徑就是這樣做的：排進佇列，由既有的
    ///   週期回呼抽乾）。取消時**不會**被呼叫。
    ///
    /// 開不起來時只記 log、什麼都不做（不回饋、不丟例外）。
    /// </summary>
    public static void Open(string[] paths, Action<CropSpec> onConfirmed, Action<string> log)
    {
        try
        {
            if (paths.Length == 0)
            {
                log("crop ui: open rejected (there is no source path to preview)");
                return;
            }
            if (SettingsUi.OnSharedUiThread)
            {
                // 從 UI 執行緒自己開窗會當場自我等待（RunOnUiThread 那條路），直接拒絕並說出來。
                log("crop ui: open rejected (called from the WPF UI thread; it would deadlock)");
                return;
            }

            lock (Gate)
            {
                if (_isOpen)
                {
                    log("crop ui: open ignored (already open)");
                    return;
                }

                long generation = ++_generation;
                string first = paths[0];
                string[] batch = paths;
                SettingsUi.EnsureSharedUiThread(log);

                bool posted = SettingsUi.PostToSharedUiThread(() =>
                {
                    // 這一輪已經被後來的 Open／Close 取代：不要開
                    if (generation != Volatile.Read(ref _generation)) return;
                    try
                    {
                        // 解碼的是**引擎等一下會拿去裁的那一張畫格**（同一個解碼器、同一個方向處理），
                        // 否則使用者在躺著的圖上框出來的矩形會落在錯的地方。
                        (System.Windows.Media.Imaging.BitmapSource frame, int frameCount) =
                            ConversionEngine.DecodeFirstFrame(first);
                        int orientation = ConversionEngine.ReadOrientation(frame);
                        if (orientation >= 2)
                        {
                            frame = ConversionEngine.ApplyOrientation(frame, orientation);
                            Log(log, $"crop ui: preview decoded with EXIF={orientation} applied to the pixels " +
                                     $"({frame.PixelWidth}x{frame.PixelHeight}) -- the rectangle is drawn on the " +
                                     "upright picture, which is what the engine will crop");
                        }
                        if (frameCount > 1)
                            Log(log, $"crop ui: {Path.GetFileName(first)} has {frameCount} frames; " +
                                     "the preview (and the crop) uses the first one (spec 6.1)");

                        // ⚠ 一定要留住**區域**變數：`_window` 是單一實例的參照，而關窗時
                        // OnCropWindowClosed 會把它設成 null——那發生在 ShowDialog 返回**之前**
                        // （Closed 事件是關窗動作的一部分）。若這裡讀 `_window?.Result`，
                        // 使用者按下確認之後會拿到 null，於是那個裁切**永遠不會被交出去**：
                        // 視窗關了、日誌上什麼都沒有、圖也沒被裁。這正是自測要抓的那種錯。
                        CropWindow window = new CropWindow(Path.GetFileName(first), batch.Length, frame,
                                                            CropRatios.DefaultId, log);
                        _window = window;
                        _previewPath = first;
                        _isOpen = true;
                        window.Closed += OnCropWindowClosed;
                        window.ShowDialog();

                        // ShowDialog 返回＝視窗關了。結果在**這個視窗物件**上（確認才有值）。
                        CropSpec? spec = window.Result;
                        if (spec is { } confirmed)
                        {
                            Log(log, $"crop ui: the user confirmed {confirmed.Describe()} " +
                                     $"on {Path.GetFileName(first)} -> handing {batch.Length} file(s) to the caller");
                            try
                            {
                                onConfirmed(confirmed);
                            }
                            catch (Exception callbackFailure)
                            {
                                // 呼叫端的回呼壞掉不干這個視窗的事，但**不可以靜默**。
                                Log(log, $"crop ui: the onConfirmed callback threw " +
                                         $"{callbackFailure.GetType().Name}: {callbackFailure.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 建起來但顯示不出來的視窗不可以留下來（與設定頁同一條規矩：它會變成
                        // 螢幕上一個沒有擁有者的幽靈，下一次開啟還會多疊一張）。
                        if (_window != null)
                        {
                            _window.Closed -= OnCropWindowClosed;
                            try
                            {
                                _window.Close();
                            }
                            catch (Exception closeFailure)
                            {
                                Log(log, $"crop ui: could not close the window that failed to show " +
                                         $"({closeFailure.GetType().Name}: {closeFailure.Message})");
                            }
                            _window = null;
                        }
                        _isOpen = false;
                        Log(log, $"crop ui: FAILED to show the window ({ex.GetType().Name}: {ex.Message})");
                    }
                }, log, "open the crop window");

                Log(log, posted
                    ? $"crop ui: open requested (preview={Path.GetFileName(first)}, batch={batch.Length})"
                    : "crop ui: open could not be posted (the WPF UI thread is not available)");
            }
        }
        catch (Exception ex)
        {
            // 「不靜默失敗」不等於「把例外丟回主訊息迴圈」：主程式不該因為裁切視窗而倒下。
            Log(log, $"crop ui: open failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>關閉裁切視窗（如果開著）。可在任何執行緒呼叫；實際關閉動作排到 UI 執行緒。取消語意。</summary>
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
                CropWindow? w = _window;
                if (w == null) return;
                w.SelfTestCancel();     // 走真正的取消鈕：與使用者按 Esc 同一條路（Result 不會被填）
            }, null, "close the crop window");
        }
        catch (Exception ex)
        {
            Log(null, $"crop ui: close failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 視窗關閉時的**唯一**清理點。少了它，「關掉之後再開」會永遠被 <c>_isOpen</c> 擋住
    /// ——而日誌上只看得到 <c>open ignored (already open)</c>，看起來像使用者的操作錯誤。
    /// </summary>
    private static void OnCropWindowClosed(object? sender, EventArgs e)
    {
        if (sender is CropWindow w)
        {
            try
            {
                w.Closed -= OnCropWindowClosed;
            }
            catch (Exception)
            {
                // 解不掉事件的後果只是多留一個委派，不值得讓關窗失敗。
            }
        }
        _window = null;
        _isOpen = false;
        _previewPath = "";
    }

    // =====================================================================================
    // 自測接縫（`--crop-ui-selftest`；正式路徑不經過這裡）
    //
    // 每一條都是「排到共用的 UI 執行緒、等它做完、把結果帶回來」，而且動的**都是真物件**：
    // 真的視窗、真的按鈕、滑鼠事件呼叫的那三個拖曳方法。這裡沒有一條是「測試專用的替代實作」
    // ——替代實作能證明的事情，正好就是它自己。
    // =====================================================================================

    /// <summary>程式化設定矩形（**正規化**座標）。等同使用者在畫面上框出那個範圍。</summary>
    public static bool SelfTestSetRect(double x, double y, double w, double h, int timeoutMs,
                                       out string detail, Action<string> log)
    {
        string report = "(nothing was set)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest set rect", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window is open)";
            bool ok = window.SelfTestSetRect(x, y, w, h);
            report = ok
                ? $"the rectangle is now {window.RectInPixels.X},{window.RectInPixels.Y}," +
                  $"{window.RectInPixels.Width},{window.RectInPixels.Height} px " +
                  $"(from normalized {x},{y},{w},{h})"
                : $"the normalized rectangle {x},{y},{w},{h} is degenerate on this image";
            return ok ? null : report;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// 程式化拖曳：把**畫面座標**的起點與終點餵進真正的 <c>BeginDrag</c>／<c>ContinueDrag</c>／
    /// <c>EndDrag</c>（滑鼠事件呼叫的就是那三個）。回傳 false＝沒有視窗或拖曳沒被接受。
    ///
    /// <paramref name="run"/> 是這一次拖曳的**型別化**結果：命中模式與四個時點的矩形。
    /// 呼叫端要比對的必須是它（數字），不是 <paramref name="detail"/> 那個給人看的字串
    /// ——「矩形真的變了」用字串比對會被無關的欄位（提示文字、比例…）騙過去。
    /// </summary>
    public static bool SelfTestDrag(double fromX, double fromY, double toX, double toY, int timeoutMs,
                                    out CropWindow.DragRun run, out string detail, Action<string> log)
    {
        CropWindow.DragRun captured = default;
        string report = "(no drag happened)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest drag", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window is open)";
            var trace = new List<string>();
            captured = window.SelfTestDrag(new System.Windows.Point(fromX, fromY),
                                           new System.Windows.Point(toX, toY), trace);
            report = string.Join(" | ", trace);
            return null;
        });
        run = captured;
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>按下某一個比例鈕（走真正的 Click 處理常式）。</summary>
    public static bool SelfTestSelectRatio(string id, int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(no ratio was selected)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest select ratio", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window is open)";
            if (!window.SelfTestSelectRatio(id)) return $"(there is no '{id}' button on this window)";
            report = $"ratio={window.RatioId} rect={window.RectInPixels.X},{window.RectInPixels.Y}," +
                     $"{window.RectInPixels.Width},{window.RectInPixels.Height} " +
                     $"notice='{window.SelfTestNotice}'";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>按下**真正的確認鈕**（與 Enter 走同一條路徑的是同一個方法）。</summary>
    public static bool SelfTestConfirm(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(nothing was confirmed)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest confirm", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window is open)";
            window.SelfTestConfirm();
            report = window.Result is { } spec
                ? $"confirmed {spec.Describe()}"
                : $"the window refused the confirmation (notice='{window.SelfTestNotice}')";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>按下**真正的取消鈕**。</summary>
    public static bool SelfTestCancel(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(nothing was cancelled)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest cancel", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window is open)";
            window.SelfTestCancel();
            report = $"cancel clicked (result={(window.Result == null ? "none" : "SET")})";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// 這張視窗正在顯示的**影像像素尺寸**（自測的斷言對象）；沒有視窗時 (0,0)。
    ///
    /// 為什麼需要這一條：自測要斷言「全圖的像素矩形就是 <c>0,0,W,H</c>」，而 W／H 必須是
    /// **這張圖自己的尺寸**。期望值若寫死一組數字（原本是 512x384＝當時唯一的夾具），
    /// 整條自測就只在那一個夾具上成立：換一張 3000x2250 的圖，矩形的像素值正確地變成
    /// <c>0,0,3000,2250</c>、確認鈕也正確地拒絕了全圖，那一格卻仍然紅——紅在一件做對了的產品上。
    /// 尺寸只有一個來源：<see cref="CropWindow.ImageWidth"/>／<see cref="CropWindow.ImageHeight"/>。
    /// </summary>
    public static (int Width, int Height) SelfTestImageSizePx()
    {
        int width = 0, height = 0;
        SettingsUi.RunOnUiThread(5000, null, "selftest image size", out _, () =>
        {
            CropWindow? window = _window;
            if (window == null) return null;
            width = window.ImageWidth;
            height = window.ImageHeight;
            return null;
        });
        return (width, height);
    }

    /// <summary>
    /// 目前的矩形，**影像像素**。與 <see cref="SelfTestImageSizePx"/> 同一條理由：斷言的基準值
    /// 必須是**視窗自己報的**數字。從正規化值自己再算一次（或從狀態字串裡刮出來）都是第二份規則，
    /// 而它與產品不一致時，那一格會紅在測試的算式上，不是紅在產品上。
    /// </summary>
    public static (int X, int Y, int Width, int Height) SelfTestRectPx()
    {
        int x = 0, y = 0, width = 0, height = 0;
        SettingsUi.RunOnUiThread(5000, null, "selftest rect", out _, () =>
        {
            CropWindow? window = _window;
            if (window == null) return null;
            System.Windows.Int32Rect rect = window.RectInPixels;
            x = rect.X;
            y = rect.Y;
            width = rect.Width;
            height = rect.Height;
            return null;
        });
        return (x, y, width, height);
    }

    /// <summary>
    /// ①②兩個座標系之間的橋（見 <see cref="CropWindow.SelfTestStageMapping"/>）：自測要從
    /// 「影像像素」算出「畫面座標」時，用的必須是**視窗自己的**縮放與偏移。全部是 0＝沒有視窗。
    /// </summary>
    public static (double Scale, double ImageLeft, double ImageTop) SelfTestStageMapping()
    {
        double scale = 0, left = 0, top = 0;
        SettingsUi.RunOnUiThread(5000, null, "selftest stage mapping", out _, () =>
        {
            CropWindow? window = _window;
            if (window == null) return null;
            (double s, double l, double t, _, _) = window.SelfTestStageMapping();
            scale = s;
            left = l;
            top = t;
            return null;
        });
        return (scale, left, top);
    }

    /// <summary>一句話的狀態（含 hwnd 與矩形的像素值／正規化值）。</summary>
    public static string SelfTestState()
    {
        string report = "(no crop window is open)";
        bool ran = SettingsUi.RunOnUiThread(5000, null, "selftest state", out string failure, () =>
        {
            CropWindow? window = _window;
            report = window == null ? "(no crop window is open)" : window.SelfTestState();
            return null;
        });
        return ran ? report : $"(unavailable: {failure})";
    }

    /// <summary>
    /// 視窗**真的存在於螢幕上**嗎（而不只是「有一個物件」）。與設定頁那條同一條理由：
    /// <see cref="IsOpen"/> 只是「上一次開啟有沒有成功」的快照，一個 hwnd 都沒有的視窗照樣能讓它綠燈。
    /// </summary>
    public static bool SelfTestWindowPresent(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(no crop window)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "selftest crop window present", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null) return "(no crop window object exists)";
            System.Windows.Interop.HwndSource? source =
                PresentationSource.FromVisual(window) as System.Windows.Interop.HwndSource;
            if (!window.IsLoaded || source == null || source.Handle == IntPtr.Zero)
            {
                return $"(a crop window object exists but it is not on screen; IsLoaded={window.IsLoaded} " +
                       $"IsVisible={window.IsVisible} size={window.ActualWidth}x{window.ActualHeight} " +
                       $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")})";
            }
            report = $"a crop window is loaded and has an hwnd " +
                     $"(0x{source.Handle.ToInt64().ToString("X")}, {window.ActualWidth}x{window.ActualHeight}, " +
                     $"preview={Path.GetFileName(_previewPath)})";
            return null;
        });
        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// 把裁切視窗畫成一張 PNG（**離屏合成**，不是螢幕擷取）：回 true 保證檔案裡有真的畫面
    /// （視窗自己會先檢查「不是整張同色」才寫檔）。
    /// </summary>
    public static bool DumpFrame(string path, int timeoutMs, out string detail, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "dump: rejected (empty path)";
            Log(log, "crop ui: " + detail);
            return false;
        }

        string report = "(no frame was captured)";
        bool ran = SettingsUi.RunOnUiThread(timeoutMs, log, "crop dump frame", out string failure, () =>
        {
            CropWindow? window = _window;
            if (window == null || !window.IsLoaded) return "(no crop window is open)";
            if (!window.TryDump(path, out string dumpDetail)) return $"({dumpDetail})";
            report = dumpDetail;
            return null;
        });

        detail = ran ? $"dump: {path} ({report})" : $"dump: {failure}";
        Log(log, "crop ui: " + detail);
        return ran;
    }

    /// <summary>日誌。**不丟例外**（日誌本身壞掉不可以讓功能失敗），回傳同一行方便串接。</summary>
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
