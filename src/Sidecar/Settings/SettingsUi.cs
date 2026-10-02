using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// 這個專案同時開了 UseWPF 與 UseWindowsForms，所以同名的型別會「模稜兩可」。
// 設定頁要的一律是 WPF 那一組；逐項釘死，避免哪天漏打全名而悄悄換成另一個型別。
//
// 色票權杖（WheelDesign）是 System.Drawing.Color，WPF 要的是 System.Windows.Media.Color，
// 所以 GDI 那一組另外取別名（GdiColor）：既不必在檔案頂端引入 System.Drawing
// （那會讓 WPF 的 Color／FontFamily 全部變成模稜兩可），也不必在每個用到的地方打全名。
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Control = System.Windows.Controls.Control;
using Dispatcher = System.Windows.Threading.Dispatcher;
using FontFamily = System.Windows.Media.FontFamily;
using GdiColor = System.Drawing.Color;
using GdiColors = System.Drawing.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Sidecar;

/// <summary>
/// 設定頁（規格 §8，D10 指定 WPF）的**對外唯一入口**與**UI 執行緒唯一擁有者**。
///
/// 執行緒契約（主程式有自己的原始 Win32 <c>GetMessage</c> 迴圈，設定頁只是附加的）：
///   - 本類別自己開一條 <c>Thread</c>，<c>SetApartmentState(STA)</c>、<c>IsBackground = true</c>；
///   - 在那條執行緒上建立 <c>System.Windows.Application</c> 並跑該執行緒自己的
///     <c>Dispatcher</c> 訊息迴圈；
///   - <see cref="Open"/> **立刻**返回，絕不阻塞呼叫端（＝主訊息迴圈執行緒）；
///   - 那條執行緒是**背景**執行緒：行程結束時它不會擋住結束，也不需要主程式配合。
///
/// 為什麼不是「每次開啟都建一個 <c>Application</c>」：WPF 的 <c>Application</c> 有
/// <c>Application.Current</c> 這個 static，同一個 AppDomain 裡建第二個會拋
/// <c>InvalidOperationException</c>。所以 UI 執行緒與 <c>Application</c> 在行程內**只建一次**，
/// 設定視窗則每次要求時新建、關閉時銷毀。第一次 <see cref="Open"/> 才會拉起那條執行緒
/// （使用者沒開設定頁就不花這條執行緒）。
///
/// 視窗外觀的顏色**唯一權威在這裡**（見 <see cref="StyleKit"/>），它又完全建立在
/// <see cref="WheelDesign"/> 的既有權杖上；XAML 裡沒有任何十六進位色，也沒有任何文字。
/// </summary>
internal static class SettingsUi
{
    /// <summary>UI 執行緒。第一次 <see cref="Open"/> 時建立，之後整個行程共用。</summary>
    private static Thread? _thread;

    private static Dispatcher? _dispatcher;

    /// <summary>UI 執行緒的受控執行緒識別碼：勾子據此判斷「我自己是不是已經在 UI 執行緒上」。</summary>
    private static int _uiThreadId;

    /// <summary>
    /// 跨執行緒的工作佇列。**不用 <c>Dispatcher.BeginInvoke</c>**：那需要在其他執行緒上
    /// 先取得 <see cref="Dispatcher"/> 物件，而「還沒準備好」與「已經關掉」的分支會變得曖昧。
    /// 這裡改成「先排隊、執行緒起來後自己抽乾」，排入失敗只有一種情況：執行緒還沒醒。
    /// </summary>
    private static readonly ConcurrentQueue<Action> Pending = new();

    /// <summary>
    /// 目前開著的設定視窗（只在 UI 執行緒上存取）。同時是「執行緒起來了沒」的旗標來源。
    /// </summary>
    private static SettingsWindow? _window;

    /// <summary><see cref="IsOpen"/> 的快照。UI 執行緒寫、任何執行緒讀，所以走 <c>Volatile</c>。</summary>
    private static volatile bool _isOpen;

    /// <summary>每次 <see cref="Open"/> 遞增。用來讓「上一輪的關閉要求」不會誤關剛開好的新視窗。</summary>
    private static long _generation;

    private static readonly object Gate = new();

    /// <summary>設定頁是否已經開著。呼叫端可以據此避免重複開啟（重複開啟會被拒絕並記錄）。</summary>
    public static bool IsOpen => _isOpen;

    /// <summary>
    /// 在自己的 STA 執行緒上開啟設定視窗後**立刻**返回（絕不阻塞呼叫端＝主訊息迴圈執行緒）。
    ///
    /// <paramref name="current"/> 與 <paramref name="path"/>：要編輯的設定與檔案路徑
    /// （<paramref name="path"/> 為 null 或空字串時用 <see cref="SettingsStore.DefaultPath"/>）。
    ///
    /// <paramref name="onSaved"/>：使用者按下「儲存」且 <see cref="SettingsStore.Save"/> 成功之後，
    /// 以**新的 Settings** 被呼叫一次。
    ///   ⚠ <b>這個回呼是在 WPF 執行緒上被叫的，不是主執行緒</b>——呼叫端自己負責跨執行緒
    ///   （例如把結果貼回自己的狀態、或 marshal 回自己的訊息迴圈）。
    ///   存檔失敗時**不會**呼叫它；失敗只會顯示在視窗內與 <paramref name="log"/> 裡。
    ///   ⚠ 這個回呼裡**不要**呼叫 <see cref="SettingsUi"/> 的 <see cref="Open"/>／<see cref="Close"/>：
    ///   那會在同一條 UI 執行緒上自我等待。要關窗請按「取消」，或從別的執行緒呼叫 <see cref="Close"/>。
    ///
    /// <paramref name="log"/>：證據日誌，可為 null（null 時關鍵訊息改寫進
    /// <see cref="Evidence.DefaultPath"/>）。⚠ 既有 <see cref="Evidence.Line"/> **沒有上鎖**，
    /// 從兩條執行緒同時寫會互相插斷；呼叫端若要傳證據物件，請自行在回呼裡 marshal 回主執行緒。
    ///
    /// 這個方法**不丟例外**給呼叫端：開不起來時記錄並收乾淨，主程式不受影響。
    /// </summary>
    public static void Open(Settings current, string path, Action<Settings> onSaved, Action<string> log)
    {
        try
        {
            if (current is null)
            {
                Log(log, "settings ui: open rejected (current settings is null)");
                return;
            }

            lock (Gate)
            {
                // 從 UI 執行緒自己開啟會當場自我等待，直接拒絕（這是呼叫端的 bug，要說出來）
                if (_dispatcher != null && _dispatcher.CheckAccess())
                {
                    Log(log, "settings ui: open rejected (called from the settings UI thread; it would deadlock)");
                    return;
                }

                if (_isOpen)
                {
                    Log(log, "settings ui: open ignored (already open)");
                    return;
                }

                long generation = ++_generation;

                Settings normalized = current.Normalized(out List<string> notes);
                foreach (string note in notes)
                    Log(log, $"settings ui: clamped {note} before showing the window");

                string file = string.IsNullOrWhiteSpace(path) ? SettingsStore.DefaultPath() : path;
                EnsureUiThread(log);

                Pending.Enqueue(() =>
                {
                    // 這一輪已經被後來的 Open／Close 取代，或執行緒正在收：不要開
                    if (generation != Volatile.Read(ref _generation))
                        return;
                    try
                    {
                        _window = new SettingsWindow(normalized, file, onSaved, log, generation);
                        _isOpen = true;
                        _window.Closed += OnSettingsWindowClosed;
                        _window.ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        if (_window != null)
                        {
                            _window.Closed -= OnSettingsWindowClosed;
                            _window = null;
                        }
                        _isOpen = false;
                        Log(log, $"settings ui: FAILED to show the window ({ex.GetType().Name}: {ex.Message})");
                    }
                });

                Log(log, $"settings ui: open requested (path={file}, language={normalized.Language})");
            }
        }
        catch (Exception ex)
        {
            // 「不靜默失敗」不等於「把例外丟回主訊息迴圈」：主程式不該因為設定頁而倒下。
            Log(log, $"settings ui: open failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 關閉設定視窗（如果開著）。可在任何執行緒呼叫；實際關閉動作排到 UI 執行緒。
    /// **不會**呼叫 <c>onSaved</c>：關窗不等於儲存。
    /// </summary>
    public static void Close()
    {
        try
        {
            long generation;
            lock (Gate) generation = _generation;
            if (generation == 0)
                return;     // 從來沒開過

            Pending.Enqueue(() =>
            {
                if (generation != Volatile.Read(ref _generation))
                    return;     // 這是上一輪的關閉要求，不要誤關新視窗
                try
                {
                    _window?.Close();
                }
                catch (Exception ex)
                {
                    Log(null, $"settings ui: close failed ({ex.GetType().Name}: {ex.Message})");
                }
            });
        }
        catch (Exception)
        {
            // Close 是「請求」，不該因為任何理由讓呼叫端例外。
        }
    }

    /// <summary>視窗關閉（含使用者按 X）：收掉參照並讓 <see cref="IsOpen"/> 誠實。</summary>
    private static void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow w)
            w.Closed -= OnSettingsWindowClosed;
        _window = null;
        _isOpen = false;
    }

    /// <summary>把 UI 執行緒拉起來（只拉一次）。呼叫端必須持有 <see cref="Gate"/>。</summary>
    private static void EnsureUiThread(Action<string>? log)
    {
        if (_thread != null) return;

        var thread = new Thread(UiThreadMain)
        {
            IsBackground = true,                                  // 背景執行緒：不阻擋行程結束
            Name = ProductInfo.Slug + "-settings-ui"
        };
        thread.SetApartmentState(ApartmentState.STA);              // WPF 必須是 STA
        thread.Start();
        _thread = thread;
        Log(log, $"settings ui: STA thread started (id={thread.ManagedThreadId})");
    }

    /// <summary>
    /// UI 執行緒主體：建立 <c>Application</c>，跑該執行緒的 Dispatcher 迴圈，抽乾工作佇列。
    /// 迴圈結束＝這條執行緒結束；它是背景執行緒，所以行程結束不需要等它。
    /// </summary>
    private static void UiThreadMain()
    {
        Application? app = null;
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            _uiThreadId = Environment.CurrentManagedThreadId;

            // 「執行緒起來之前」排進來的工作先跑掉，之後每 25 ms 抽一次。
            // 用 DispatcherTimer（而不是另開執行緒）是因為它就在這條 Dispatcher 迴圈上跑，
            // 不需要任何跨執行緒同步；設定頁的動作是「使用者等級」的頻率，25 ms 足夠。
            var pump = new DispatcherTimer(
                TimeSpan.FromMilliseconds(25), DispatcherPriority.Normal, (_, _) => Drain(), dispatcher);
            pump.Start();

            Drain();
            Dispatcher.Run();       // 跑這條執行緒自己的 Dispatcher 迴圈（靜態方法，見 CS0176）
            pump.Stop();
        }
        catch (Exception ex)
        {
            Log(null, $"settings ui: UI thread failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            _dispatcher = null;
            _isOpen = false;
            _window = null;
            try
            {
                app?.Shutdown();
            }
            catch (Exception)
            {
                // 執行緒都要結束了，關不掉 Application 沒有補救手段，也不該再拋。
            }
        }
    }

    /// <summary>抽乾工作佇列。**逐項 try/catch**：一項失敗不可以讓整條 UI 執行緒死掉。</summary>
    private static void Drain()
    {
        while (Pending.TryDequeue(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log(null, $"settings ui: queued work failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// 記錄一則訊息。<paramref name="log"/> 可為 null；null 時寫進證據檔（<see cref="Evidence.DefaultPath"/>）。
    /// **絕不因為記錄失敗而丟例外**：呼叫端傳進來的委派是外部程式碼。
    /// </summary>
    private static void Log(Action<string>? log, string message)
    {
        try
        {
            if (log != null)
            {
                log(message);
                return;
            }

            string path = Evidence.DefaultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 連日誌都寫不出來時，沒有更底層的管道了；刻意吞掉以免掩蓋原問題。
        }
    }

    // =====================================================================================
    // 自動化勾子（驗收證據用；正式路徑不經過這裡）
    //
    // 為什麼需要它們：驗收腳本按不了滑鼠，所以「按下儲存 → 真的寫檔 → onSaved 被叫」
    // 這條鏈路在無人操作時完全沒有可重現的證據。這兩個勾子把 UI 執行緒借給呼叫端，
    // 讓它能在**真正的視窗**上驅動**真正的按鈕**。
    //
    // ⚠ 兩者都**阻塞等待**（呼叫端拿得到布林結果），所以：
    //   - 不可從 UI 執行緒呼叫（會自我等待；這裡直接拒絕，不讓它變成 25 秒的卡死）；
    //   - 逾時一律回 false，並且**絕不丟例外**給呼叫端。
    // ⚠ <see cref="SelfTestSave"/> 會真的寫檔，寫的是**這個視窗被打開時指定的路徑**。
    //   驗收腳本要用暫存路徑開窗，否則會覆蓋使用者真正的設定檔。
    // =====================================================================================

    /// <summary>
    /// 以「和真人按儲存完全相同的那條路徑」套用 <paramref name="edited"/> 並回報結果。
    ///
    /// 流程：把值推回控制項（<see cref="SettingsWindow.ApplyEdited"/>）→
    /// 對 <c>SaveButton</c> 發出真正的 <see cref="Button.ClickEvent"/> →
    /// 由 <see cref="SettingsWindow"/> 的按鈕處理常式與
    /// <see cref="SettingsStore.Save"/> 完成其餘一切。
    /// 這裡**沒有任何自己的存檔邏輯**：勾子只負責「排隊、按鈕、等待、回報」。
    ///
    /// <paramref name="detail"/> 一定含：<c>onSaved</c> 有沒有被叫、寫入的檔案路徑，
    /// 以及 <see cref="SettingsStore.Describe"/> 的結果或失敗原因。
    /// </summary>
    public static bool SelfTestSave(Settings edited, int timeoutMs, out string detail, Action<string> log)
    {
        if (edited is null)
        {
            detail = "selftest: rejected (edited settings is null)";
            Log(log, detail);
            return false;
        }

        bool savedByCallback = false;
        string report = "(the save button handler never reported a result)";
        string file = "(unknown)";

        bool ran = RunOnUiThread(timeoutMs, log, "selftest save", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded)
                return "(no settings window is open)";

            file = w.SettingsPath;
            w.ApplyEdited(edited);

            // 存檔成功時 OnSaveClick 會換成**新的 Settings 物件**，所以用參考比較判斷結果，
            // 不必去改動 _hasSaved 的語意。（若存檔失敗，這裡看到的仍是舊物件。）
            Settings before = w.SavedSettings;

            w.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Settings after = w.SavedSettings;
            bool changed = !ReferenceEquals(before, after);
            if (!changed)
                return $"save failed: {w.StatusText.Text}";

            savedByCallback = true;
            report = SettingsStore.Describe(after);
            return null;
        });

        if (!ran)
        {
            detail = $"selftest: {failure}";
            Log(log, detail);
            return false;
        }

        if (!savedByCallback)
        {
            detail = $"selftest: {report}";
            Log(log, detail);
            return false;
        }

        detail = $"selftest: onSaved fired=True; saved to {file}; {report}";
        Log(log, detail);
        return true;
    }

    /// <summary>
    /// 把目前這張設定頁畫成 PNG。
    ///
    /// 可靠性：<see cref="RenderTargetBitmap"/> 在「還沒排版／還沒被繪製」的視窗上會產生空白圖，
    /// 所以這裡先 <c>UpdateLayout()</c>，並在寫檔**前**把像素解回來數不同顏色——
    /// 整張只有 1 種顏色＝空白圖，這種圖不寫檔、回 false 並說出原因。
    /// 也就是說：回 true 保證檔案裡有真的畫面，不會偶爾交出空白圖。
    /// </summary>
    public static bool DumpFrame(string path, int timeoutMs, out string detail, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "dump: rejected (empty path)";
            Log(log, detail);
            return false;
        }

        byte[]? png = null;
        string report = "(no frame was captured)";

        bool ran = RunOnUiThread(timeoutMs, log, "dump frame", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded)
                return "(no settings window is open)";

            w.UpdateLayout();
            int pw = (int)Math.Ceiling(w.ActualWidth);
            int ph = (int)Math.Ceiling(w.ActualHeight);
            if (pw <= 0 || ph <= 0)
                return $"(window has no size: {w.ActualWidth}x{w.ActualHeight})";

            DpiScale dpi = VisualTreeHelper.GetDpi(w);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(pw * dpi.DpiScaleX),
                                             (int)Math.Ceiling(ph * dpi.DpiScaleY),
                                             96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(w);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            var buffer = new MemoryStream();
            encoder.Save(buffer);
            byte[] bytes = buffer.ToArray();
            if (bytes.Length == 0)
                return "(the PNG encoder produced 0 bytes)";

            // 「不是空白圖」的證據：把 PNG 解回來數顏色。整張同色＝什麼都沒畫到。
            int distinct = CountDistinctColors(bytes);
            if (distinct < 2)
                return $"(captured frame is blank: {distinct} distinct colour(s) in {pw}x{ph})";

            png = bytes;
            report = $"{pw}x{ph} @{dpi.DpiScaleX:0.##}x, {bytes.Length} bytes, {distinct} distinct colours";
            return null;
        });

        if (!ran)
        {
            detail = $"dump: {failure}";
            Log(log, detail);
            return false;
        }

        if (png == null)
        {
            detail = $"dump: {report}";
            Log(log, detail);
            return false;
        }

        try
        {
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex)
        {
            detail = $"dump: could not write {path} ({ex.GetType().Name}: {ex.Message})";
            Log(log, detail);
            return false;
        }

        detail = $"dump: {path} ({report})";
        Log(log, detail);
        return true;
    }

    /// <summary>
    /// 把 <paramref name="work"/> 排到 UI 執行緒上執行並**等它做完**。
    /// <paramref name="work"/> 回傳 null 表示成功，否則回傳失敗原因。
    /// 已在這條 UI 執行緒上時直接執行（排隊會自我等待）。不丟例外。
    /// </summary>
    private static bool RunOnUiThread(int timeoutMs, Action<string>? log, string what,
        out string failure, Func<string?> work)
    {
        const string NotStarted = "(not run)";
        failure = NotStarted;
        string? result = "the work never started";

        if (timeoutMs <= 0)
        {
            failure = "timeout must be positive";
            return false;
        }

        if (_dispatcher == null || _uiThreadId == 0)
        {
            failure = "the settings UI thread is not running (nothing has opened the settings page in this process)";
            return false;
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            failure = "called from the settings UI thread itself (it would self-wait)";
            Log(log, $"settings ui: {what} refused: {failure}");
            return false;
        }

        using var done = new ManualResetEventSlim(false);
        Pending.Enqueue(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = $"threw {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                    // 呼叫端已經逾時並把號誌收掉了：沒有人還在等，直接結束。
                }
            }
        });

        if (!done.Wait(timeoutMs))
        {
            failure = $"timed out after {timeoutMs} ms (the settings UI thread did not pick it up)";
            return false;
        }

        if (result != null)
        {
            failure = result;
            return false;
        }

        failure = "";
        return true;
    }

    /// <summary>
    /// 解回 PNG 並數不同的 RGB 顏色數（透明像素不算）。用來判斷「這張圖是不是空的」。
    /// 只在勾子裡用到；解不開就當成失敗（回 0）。
    /// </summary>
    private static int CountDistinctColors(byte[] png)
    {
        try
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            BitmapSource? frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            if (frame == null) return 0;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            var seen = new HashSet<int>();
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;      // 全透明＝沒有內容
                seen.Add((pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i]);
                if (seen.Count >= 4096) break;         // 夠多就確定不是空白，不必數完
            }
            return seen.Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // =====================================================================================
    // 外觀（視覺方向 D「冷鋼薄荷・對比加強」）
    // =====================================================================================

    /// <summary>
    /// 設定頁的調色盤與控制項外觀。
    ///
    /// **來源規則**：能引用 <see cref="WheelDesign"/> 權杖就引用，不新增第二份顏色權威；
    /// 只有「輪盤上不存在」的用途（視窗底色、輸入框底、內文灰）才在這裡由既有權杖**合成**出來，
    /// 每個合成值都寫明是從哪個權杖混出來的。
    ///
    /// 為什麼要合成而不是直接用 <see cref="WheelDesign.Veil"/>：輪盤是貼在桌面上的
    /// per-pixel alpha 圖層（暗罩 .80 是刻意要讓桌布透出來），設定頁是不透明視窗，
    /// 直接把 .80 的暗罩當底色會變成半透明視窗。所以用「暗罩疊在不透明的同色底上」算出等效色，
    /// 公式與理由寫在 <see cref="Opaque"/>。
    /// </summary>
    internal static class StyleKit
    {
        /// <summary>不透明的視窗底色：<see cref="WheelDesign.Veil"/> 的顏色（暗罩本身是 .80，這裡取實心）。</summary>
        public static readonly SolidColorBrush WindowBg = Opaque(WheelDesign.Veil);

        /// <summary>卡片／輸入框底：白 5 % 疊在視窗底色上（輪盤沒有這個權杖，因為它沒有卡片）。</summary>
        public static readonly SolidColorBrush SurfaceBg = Opaque(WheelDesign.Veil, GdiColors.White, 13);

        /// <summary>卡片外框：外緣薄荷 <see cref="WheelDesign.Rim"/> 的顏色，透明度壓到 18 %（原 55 % 太重）。</summary>
        public static readonly SolidColorBrush CardBorder = Opaque(WheelDesign.Veil, WheelDesign.Rim, 46);

        /// <summary>主要內文＝<see cref="WheelDesign.Label"/>（輪盤標籤白）。</summary>
        public static readonly SolidColorBrush TextMain = Opaque(WheelDesign.Veil, WheelDesign.Label, 255);

        /// <summary>說明文字＝白 62 % 疊底（沿用輪盤「未選中標籤要壓暗」的 <see cref="WheelDesign.LabelDimOpacity"/> 家族）。</summary>
        public static readonly SolidColorBrush TextDim = Opaque(WheelDesign.Veil, GdiColors.White, 158);

        /// <summary>更弱的一層（設定檔路徑、區塊標題）。</summary>
        public static readonly SolidColorBrush TextFaint = Opaque(WheelDesign.Veil, GdiColors.White, 107);

        /// <summary>主要按鈕底＝外緣薄荷 <see cref="WheelDesign.Rim"/>（實心；配深字對比約 17:1）。</summary>
        public static readonly SolidColorBrush PrimaryBg = Opaque(WheelDesign.Rim);

        /// <summary>主要按鈕字＝<see cref="WheelDesign.CapFg"/>（近黑深青）。</summary>
        public static readonly SolidColorBrush PrimaryFg = Opaque(WheelDesign.CapFg);

        /// <summary>次要按鈕框＝琥珀 <see cref="WheelDesign.SelStroke"/>（與「選中外框」、「警告」、「尚未生效」同一色）。</summary>
        public static readonly SolidColorBrush SecondaryBorder = Opaque(WheelDesign.SelStroke);

        /// <summary>警告文字（含 Shift 的規格警告）＝琥珀。</summary>
        public static readonly SolidColorBrush WarningFg = Opaque(WheelDesign.SelStroke);

        /// <summary>「尚未生效」標記＝琥珀。</summary>
        public static readonly SolidColorBrush StubFg = Opaque(WheelDesign.SelStroke);

        /// <summary>失敗訊息＝<see cref="WheelDesign.NoticeFill"/>（產品內「這是錯誤」的唯一紅）。</summary>
        public static readonly SolidColorBrush FailFg = Opaque(WheelDesign.NoticeFill);

        /// <summary>選中／作用中＝選中扇區填色 <see cref="WheelDesign.SelFill"/>（實心 #3F6B7A）。</summary>
        public static readonly SolidColorBrush SelectedBg = Opaque(WheelDesign.SelFill);

        /// <summary>字體。無襯線；中文用 <c>Microsoft JhengHei UI</c>，缺字時 WPF 自己往後退。</summary>
        public static readonly FontFamily Font =
            new("Segoe UI, Microsoft JhengHei UI, Microsoft YaHei UI, Tahoma");

        /// <summary>
        /// 把「帶透明度的權杖」疊在不透明底色上，算成不透明色（WPF 的 <c>Background</c> 用透明色
        /// 會讓視窗變成半透明，那不是這裡要的效果）。合成公式就是標準的 source-over：
        /// <c>out = fg·a + bg·(1−a)</c>，四捨五入。<paramref name="over"/> 省略時＝直接取該權杖的 RGB。
        /// </summary>
        private static SolidColorBrush Opaque(GdiColor baseRgb, GdiColor? over = null, byte? overAlpha = null)
        {
            System.Windows.Media.Color bottom =
                System.Windows.Media.Color.FromRgb(baseRgb.R, baseRgb.G, baseRgb.B);
            if (over is not { } top)
                return new SolidColorBrush(bottom);

            double a = (overAlpha ?? top.A) / 255.0;
            byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B)));
        }

        /// <summary>把視窗底色、字體、以及每個控件的顏色套上去。XAML 裡因此不需要任何顏色。</summary>
        public static void Apply(SettingsWindow window)
        {
            window.Background = WindowBg;
            window.Foreground = TextMain;
            window.FontFamily = Font;

            foreach (Border card in new[] { window.BehaviourCard, window.QualityCard, window.SafetyCard })
            {
                card.Background = SurfaceBg;
                card.BorderBrush = CardBorder;
                card.BorderThickness = new Thickness(1);
            }

            window.FooterPanel.Background = SurfaceBg;
            window.FooterPanel.BorderBrush = CardBorder;

            foreach (TextBlock t in new[]
            {
                window.TitleText, window.TriggerLabel, window.StartupLabel, window.LanguageLabel,
                window.JpgLabel, window.WebpLabel, window.NamingLabel, window.TimeoutLabel,
                window.HotkeyLabel, window.DiagnosticsLabel, window.TriggerValueText, window.JpgValueText,
                window.WebpValueText, window.TimeoutValueText, window.NamingValueText
            })
                t.Foreground = TextMain;

            foreach (TextBlock t in new[]
            {
                window.SubtitleText, window.BehaviourCardDesc, window.QualityCardDesc, window.SafetyCardDesc
            })
                t.Foreground = TextDim;

            // 區塊標題用最弱的一層＝層次分明（標題 < 欄位名 < 值）
            window.BehaviourCardTitle.Foreground = TextFaint;
            window.QualityCardTitle.Foreground = TextFaint;
            window.SafetyCardTitle.Foreground = TextFaint;
            window.PathText.Foreground = TextFaint;
            window.StatusText.Foreground = TextDim;

            window.TriggerWarningText.Foreground = WarningFg;
            window.NamingReasonText.Foreground = WarningFg;
            window.HotkeyStubText.Foreground = StubFg;
            window.StartupStubBadge.Foreground = StubFg;

            PrimaryButton(window.SaveButton);
            SecondaryButton(window.CancelButton);
            SecondaryButton(window.CaptureButton);

            foreach (ComboBox box in new[] { window.TriggerPresetBox, window.LanguageBox })
            {
                box.Background = SurfaceBg;
                box.Foreground = TextMain;
                box.BorderBrush = CardBorder;
                box.FontFamily = Font;
            }

            window.HotkeyBox.Background = SurfaceBg;
            window.HotkeyBox.Foreground = TextMain;
            window.HotkeyBox.BorderBrush = CardBorder;
            window.HotkeyBox.CaretBrush = TextMain;
            window.HotkeyBox.FontFamily = Font;

            foreach (ToggleButton toggle in new[]
            { window.StartupToggle, window.HotkeyToggle, window.DiagnosticsToggle })
            {
                toggle.Background = SurfaceBg;
                toggle.Foreground = TextMain;
                toggle.BorderBrush = CardBorder;
                toggle.FontFamily = Font;
                toggle.Template = ToggleTemplate();
                toggle.Cursor = System.Windows.Input.Cursors.Hand;
            }
        }

        /// <summary>WPF 內建的 <c>Button</c> 範本會蓋掉 <c>Background</c>（還原成系統色），所以自己給一個最小範本。</summary>
        public static ControlTemplate ButtonTemplate() => Template(5);

        /// <summary>開關（<c>ToggleButton</c>）範本：<c>Background</c> 就是唯一的狀態顯示。</summary>
        public static ControlTemplate ToggleTemplate() => Template(4);

        private static ControlTemplate Template(int cornerRadius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush))
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

            var host = new FrameworkElementFactory(typeof(ContentPresenter));
            host.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            host.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(host);

            return new ControlTemplate(typeof(System.Windows.Controls.Primitives.ButtonBase)) { VisualTree = border };
        }

        /// <summary>主要動作（儲存）：實心薄荷＋深字。</summary>
        public static void PrimaryButton(Button button)
        {
            button.Background = PrimaryBg;
            button.Foreground = PrimaryFg;
            button.BorderBrush = PrimaryBg;
            button.FontFamily = Font;
            button.Template = ButtonTemplate();
            button.Cursor = System.Windows.Input.Cursors.Hand;
        }

        /// <summary>次要動作（取消、按下新鍵）：表面底＋琥珀外框＋白字。</summary>
        public static void SecondaryButton(Button button)
        {
            button.Background = SurfaceBg;
            button.Foreground = TextMain;
            button.BorderBrush = SecondaryBorder;
            button.FontFamily = Font;
            button.Template = ButtonTemplate();
            button.Cursor = System.Windows.Input.Cursors.Hand;
        }

        /// <summary>
        /// 開／關的視覺。<c>Background</c> 承載狀態：開＝實心選中藍＋白字（對比 6.4:1）、
        /// 關＝表面底＋白字（對比約 13:1）。**刻意不用「變灰」表示關**：這個視窗是深底，
        /// 灰字在白底／深底上都會掉對比，而兩個狀態本來就該一樣清楚。
        /// </summary>
        public static void SetToggleState(ToggleButton toggle, bool on)
        {
            toggle.Background = on ? SelectedBg : SurfaceBg;
            toggle.BorderBrush = on ? SecondaryBorder : CardBorder;
        }

        /// <summary>下拉選單的項目（Popup 裡自己一份，不繼承 ComboBox 的顏色）。</summary>
        public static void StyleComboItem(ComboBoxItem item)
        {
            item.Background = SurfaceBg;
            item.Foreground = TextMain;
            item.FontFamily = Font;
            item.Padding = new Thickness(8, 4, 8, 4);
        }
    }
}
