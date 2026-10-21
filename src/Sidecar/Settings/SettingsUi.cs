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
using TextBox = System.Windows.Controls.TextBox;
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

    /// <summary>
    /// 這條 UI 執行緒是怎麼結束的（空字串＝還沒結束）。<see cref="EnsureUiThread"/> 靠它在日誌裡
    /// 說出「上一條執行緒為什麼走了」，自測也靠它確認那個致命例外真的發生過。
    /// </summary>
    private static volatile string _uiThreadFailure = "";

    /// <summary>最後一次穿出 dispatcher 迴圈的例外（給自測斷言用；沒有就保持 null）。</summary>
    private static Exception? _uiThreadException;

    /// <summary>每次 <see cref="Open"/> 遞增。用來讓「上一輪的關閉要求」不會誤關剛開好的新視窗。</summary>
    /// <remarks>
    /// 這也正是「UI 執行緒若真的結束、重開一條之後，舊工作不會被誤執行」的機制：
    /// 執行緒結束時佇列裡可能還留著上一輪的工作，但它們帶著**舊的** generation，
    /// 一被抽到就會自己返回；帶新 generation 的則是使用者剛剛真的按下去的要求，本來就該被服務。
    /// 所以重啟時不需要（也不可以）清空佇列或重編號。
    /// </remarks>
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
                            // 建起來但**顯示不出來**的視窗不可以留下來。
                            // 2026-10-06 實測：ShowDialog 被例外拆掉之後，那張視窗還在螢幕上（Win32 的
                            // EnumWindows 看得到它），但已經沒有人擁有它——不會再有人關它，也不會再有人
                            // 回應它，變成一個永遠關不掉的幽靈；下一次開啟還會多疊一張。
                            // 擁有權在這裡：這個 catch 是唯一知道「這次開窗失敗」的地方。
                            try
                            {
                                _window.Close();
                            }
                            catch (Exception closeFailure)
                            {
                                Log(log, $"settings ui: could not close the window that failed to show " +
                                         $"({closeFailure.GetType().Name}: {closeFailure.Message})");
                            }
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

    // =====================================================================================
    // 共用的 WPF UI 執行緒（裁切視窗借用它）
    //
    // 為什麼是**借用同一條**，而不是讓裁切視窗再開一條自己的：一個 AppDomain 只准有一個
    // Application（見 UiThreadMain 的說明），第二條執行緒拿不到自己的 Application，
    // 而本檔已經量到「沒有 Application 的那條執行緒開出來的視窗不會上螢幕」那個形狀。
    // 兩條執行緒誰先起來就決定了誰能拿到 Application——那是一種**看啟動順序才會出現**的 bug。
    // 借用同一條執行緒，這個問題就不存在：永遠只有一個 Application、一條 STA 執行緒、
    // 一個工作佇列、一個 generation。裁切視窗與設定頁因此可以同時開著而不互相破壞。
    // =====================================================================================

    /// <summary>
    /// 確定共用的 WPF UI 執行緒已經起來（可以從任何執行緒呼叫；內部自己上鎖）。
    /// 這是「借用」的**唯一入口**：呼叫端不必、也不可以自己管那條執行緒。
    /// </summary>
    internal static void EnsureSharedUiThread(Action<string>? log)
    {
        lock (Gate) EnsureUiThread(log);
    }

    /// <summary>
    /// 把一件工作排到共用的 WPF UI 執行緒上並**立刻返回**（不等待、不丟例外）。
    ///
    /// 為什麼不能拿 <see cref="RunOnUiThread"/> 代替它：那一個會等到工作做完才返回，而
    /// 「開一個 modal 視窗」這件工作要等到使用者關窗才做完——用它等於把呼叫端（主訊息迴圈）
    /// 凍在那裡，托盤、鉤子、放手偵測全部停擺。
    ///
    /// 回傳 false 只有一種情況：佇列還沒有人抽（呼叫端要先 <see cref="EnsureSharedUiThread"/>）。
    /// </summary>
    internal static bool PostToSharedUiThread(Action work, Action<string>? log, string what)
    {
        if (work == null)
        {
            Log(log, $"settings ui: post rejected ({what}: work is null)");
            return false;
        }
        Pending.Enqueue(work);
        return true;
    }

    /// <summary>
    /// 這個行程裡是不是已經有可用的 WPF dispatcher（＝共用的 UI 執行緒活著且在抽佇列）。
    /// 給「排工作之前要先確認」的呼叫端用。
    /// </summary>
    internal static bool SharedUiThreadReady => _dispatcher != null && _uiThreadId != 0;

    /// <summary>
    /// 目前是不是在**共用的 WPF UI 執行緒**上。呼叫端據此拒絕「自己開窗再自己等」的自我等待，
    /// 以及「從 UI 執行緒直接寫 Evidence」這種沒有上鎖的日誌。
    /// </summary>
    internal static bool OnSharedUiThread =>
        _uiThreadId != 0 && Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>把 UI 執行緒拉起來（只拉一次）。呼叫端必須持有 <see cref="Gate"/>。</summary>
    private static void EnsureUiThread(Action<string>? log)
    {
        // 「已經有一條」不等於「那一條還能用」。
        //
        // 2026-10-06 量到的（使用者的日誌就是這個形狀）：UI 執行緒有可能結束——dispatcher 的例外、
        // WPF 內部的失敗、或是它自己的迴圈返回。結束之後它**不會再抽 Pending**，而這裡如果只看
        // `_thread != null` 就直接返回，之後每一次 Open() 都只是把工作排進一個沒有抽乾者的佇列：
        // 日誌上只會看到 `open requested`，沒有視窗、沒有錯誤、也沒有 `already open`，
        // 托盤按幾次都一樣——設定頁就**永久**失效了。
        // 所以判準必須是「那條執行緒還活著嗎」，而不是「有沒有建過」。
        if (_thread is { IsAlive: true }) return;

        if (_thread != null)
        {
            // 走到這裡代表它已經結束（IsAlive=false ⇒ finally 已經跑完），照實說出來再重開一條。
            // _dispatcher／_uiThreadId／_isOpen／_window 已由 UiThreadMain 的 finally 歸零。
            Log(log, $"settings ui: the previous UI thread has ended ({_uiThreadFailure}); starting a new one");
            _thread = null;
        }

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
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            catch (InvalidOperationException ex)
            {
                // 一個 AppDomain 只准有一個 Application，而它已經被**第一條** UI 執行緒用掉了
                // （WPF 把它記在自己的靜態欄位上，那個實例不會因為執行緒結束而消失）。
                // 重啟時不可能再建第二個；而視窗並不需要 Application 才能顯示——Application 負責的是
                // 「最後一個視窗關掉就結束行程」那類行程層級政策，本產品是 OnExplicitShutdown
                // （關窗與否由托盤決定），所以這裡就沒有 Application 地繼續跑。
                // 不這樣做的話，重啟的執行緒會當場死在這裡，等於「UI 執行緒永遠只能死一次」。
                app = null;
                Log(null, $"settings ui: this process has already used its one Application " +
                          $"({ex.Message}); the restarted UI thread runs without one");
            }

            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            _uiThreadId = Environment.CurrentManagedThreadId;

            // 「執行緒起來之前」排進來的工作先跑掉，之後每 25 ms 抽一次。
            // 用 DispatcherTimer（而不是另開執行緒）是因為它就在這條 Dispatcher 迴圈上跑，
            // 不需要任何跨執行緒同步；設定頁的動作是「使用者等級」的頻率，25 ms 足夠。
            //
            // ⚠ 回呼**只排一次 Drain，自己立刻返回**，絕對不可以直接在這裡抽乾佇列。
            // 原因（2026-10-06 量到的，不是推論）：佇列裡的工作可能是「開設定頁」＝
            // SettingsWindow.ShowDialog()，那是一個**阻塞**的巢狀 dispatcher 迴圈。
            // 若那一項是在這個回呼的堆疊上被抽到，回呼就永遠不會返回，而 WPF 的 DispatcherTimer
            // 在回呼返回之前**不會再排下一次 tick**——於是 Drain 再也沒有人叫，Pending 永久堆積：
            // 「關掉之後再按托盤也叫不出來」就是這個形狀（日誌裡只看得到 open requested，
            // 之後什麼都不會發生，連 Close() 的要求也永遠不會被執行）。
            //
            // 量到的證據（探針逐行記 tid／drainDepth／dialogDepth）：
            //   第一次開：Drain 是在 Dispatcher.Run() **之前**被直接呼叫的，所以 modal 迴圈裡的
            //             tick 照常觸發（drainDepth=2、dialogDepth=1）→ 一切正常。
            //   第二次開：工作項是在 tick 的回呼裡被抽到的（drainDepth=1、dialogDepth=1），
            //             那一行之後 tick 全部消失，直到 modal 迴圈退出才恢復。
            // 改成「排隊」之後，回呼永遠立刻返回，計時器因此永遠活得下去；而被排出去的 Drain
            // 會落在 modal 迴圈裡執行——已量測：巢狀 frame 會正常執行 Normal 優先權的
            // dispatcher 操作（那一次探針就是在 dialogDepth=1 的情況下被執行完的）。
            var pump = new DispatcherTimer(
                TimeSpan.FromMilliseconds(25), DispatcherPriority.Normal,
                (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Drain)), dispatcher);
            pump.Start();

            Drain();

            // ⚠ 「跑一次 Dispatcher.Run() 就結束」是這個 bug 的第二個實體（使用者回報的「之後再從托盤按
            // 『開啟設定』，還是叫不出來」）。2026-10-06 量到：只要有任何一個例外穿出 Dispatcher.Run()
            // ——設定頁裡那一條（輸入框第一次取得焦點時 WPF 建 caret 失敗）就是——這條執行緒就結束了；
            // `_thread` 卻還是非 null，於是 `EnsureUiThread` 永遠提早返回，之後每一次開啟都只把工作
            // 排進一個沒有抽乾者的佇列：日誌上看得到 open requested，卻永遠不會有視窗。
            // 而**重建一條執行緒救不回來**：一個 AppDomain 只准有一個 Application，第二條執行緒拿不到
            // 自己的 Application，沒有 Application 的執行緒開出來的視窗根本不會上螢幕（已量測：
            // IsLoaded=False、IsVisible=False、hwnd=(none)）。所以正確的擁有者是**這條執行緒本身的壽命**：
            // 一次失敗的 dispatcher 回呼不可以讓它結束——例外跑掉之後，同一個 dispatcher 還能再跑。
            while (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    Dispatcher.Run();       // 跑這條執行緒自己的 Dispatcher 迴圈（靜態方法，見 CS0176）
                }
                catch (Exception ex)
                {
                    // 例外穿出去了，但執行緒、Application、dispatcher 都還在：記下來，繼續跑同一個迴圈。
                    _uiThreadFailure = $"{ex.GetType().Name}: {ex.Message}";
                    _uiThreadException = ex;
                    Log(null, $"settings ui: a fatal exception escaped the dispatcher loop " +
                              $"({ex.GetType().Name}: {ex.Message}); the UI thread keeps running");
                    continue;
                }
                break;      // Run 自己返回＝dispatcher 被關掉了（有人呼叫 Shutdown，或行程要收了）
            }
            pump.Stop();
        }
        catch (Exception ex)
        {
            _uiThreadFailure = $"{ex.GetType().Name}: {ex.Message}";
            _uiThreadException = ex;
            Log(null, $"settings ui: UI thread failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            _dispatcher = null;
            _isOpen = false;
            _window = null;
            if (_uiThreadFailure.Length == 0) _uiThreadFailure = "(Dispatcher.Run returned normally)";
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
    /// 讀出畫面上那兩行「還不能存」的行內提示（診斷用）。
    ///
    /// 存在的理由：<see cref="SelfTestSave"/> 的 <c>detail</c> 是**狀態列**的文字，而使用者
    /// 真正會先看到的是欄位底下那一行；只驗狀態列的話，「行內提示整行是空的」這個 bug 會全綠。
    /// 這裡刻意只讀不寫——它是一個觀察窗，不是第二個權威。
    /// </summary>
    public static bool SelfTestNoticeTexts(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(the settings page never answered)";
        bool ran = RunOnUiThread(timeoutMs, log, "selftest notice texts", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded)
                return "(no settings window is open)";

            report = $"compressNotice='{w.CompressNoticeText.Text}' " +
                     $"outputNotice='{w.OutputNoticeText.Text}' " +
                     $"limitBoxEnabled={w.CompressTargetBox.IsEnabled} " +
                     $"limitBoxText='{w.CompressTargetBox.Text}'";
            return null;
        });

        detail = ran ? report : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// 以「和真人按下取消鈕完全相同的那條路徑」關掉設定頁：對 <c>CancelButton</c> 發出真正的
    /// <see cref="Button.ClickEvent"/>，由 <see cref="SettingsWindow"/> 自己的處理常式
    /// （<c>OnCancelClick =&gt; Close()</c>，右上角的 X 也是同一條）關閉視窗。
    ///
    /// 為什麼非要有這一條：產品自己的 <see cref="Close"/> 是**另一條**關窗路徑——它排一個 closure
    /// 到 UI 執行緒去呼叫 <c>_window?.Close()</c>，而且要先比對世代編號。原本兩個自測都只走產品那條，
    /// 於是「**視窗自己**關掉之後再也開不回來」這一格完全沒有覆蓋：那正是使用者回報的症狀
    /// （點了一下設定頁就消失，之後托盤怎麼按都叫不出來，而日誌顯示要求確實送進去了）。
    /// 兩條路徑的差別（誰先送 <c>Closed</c>、世代編號怎麼被用掉、關窗的請求與新視窗的建立誰先到）
    /// 正好是 bug 最可能的藏身處。
    ///
    /// 這裡**沒有任何自己的關窗邏輯**：勾子只負責「按鈕、回報」，與 <see cref="SelfTestSave"/> 同一個風格。
    /// 「按了卻關不掉」不會被這裡掩蓋：回傳 true 只代表按鈕真的被按到，關閉是否完成一律由呼叫端
    /// 用 <see cref="IsOpen"/> 去等、去斷言。
    /// </summary>
    public static bool SelfTestCancel(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(the cancel handler never reported a result)";

        bool ran = RunOnUiThread(timeoutMs, log, "selftest cancel", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded)
                return "(no settings window is open)";

            report = $"cancel clicked on the window editing {w.SettingsPath} (generation={w.Generation})";
            w.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return null;
        });

        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// UI 執行緒的狀態快照。**可以在任何執行緒上讀**（不經 <see cref="RunOnUiThread"/>），
    /// 因為它必須在「UI 執行緒已經死了、排進去的東西再也沒有人抽」的時候還能回答問題——
    /// 那正是自測要驗的那個狀態。
    /// </summary>
    public static string SelfTestUiThreadState()
    {
        Thread? t = _thread;
        string alive = t == null ? "none" : (t.IsAlive ? "alive" : "dead");
        return $"uiThread={alive} id={_uiThreadId} isOpen={_isOpen} " +
               $"dispatcher={(_dispatcher != null ? "set" : "null")} " +
               $"failure={(_uiThreadFailure.Length == 0 ? "(none)" : _uiThreadFailure)}";
    }

    /// <summary>殺掉 UI 執行緒的例外全文（含堆疊；沒有就回一句話）。自測用它確認例外真的發生過。</summary>
    public static string SelfTestUiThreadExceptionText() =>
        _uiThreadException?.ToString() ?? "(no exception escaped Dispatcher.Run)";

    /// <summary>UI 執行緒還活著嗎（可以在任何執行緒上問）。</summary>
    public static bool SelfTestUiThreadAlive() => _thread is { IsAlive: true };

    /// <summary>
    /// 自測：**故意把 UI 執行緒弄死**——排一個會丟例外的 dispatcher 操作上去。
    ///
    /// 為什麼是「排一個會丟例外的工作」而不是別的：<see cref="Drain"/> 對佇列裡的工作逐項 try/catch，
    /// 所以「工作失敗」殺不死執行緒（那是我上一輪修掉的另一件事）；會殺死它的是**例外穿出
    /// <c>Dispatcher.Run()</c>**，也就是直接排給 dispatcher 的操作自己丟出來。
    /// 這正是「UI 執行緒已經結束、<c>_thread</c> 卻還是非 null」那個狀態的來源。
    /// </summary>
    public static bool SelfTestKillUiThread(int timeoutMs, out string detail, Action<string> log)
    {
        _ = timeoutMs;
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            detail = "selftest: the settings UI thread is not running, so it cannot be killed";
            return false;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            throw new InvalidOperationException(
                "settings ui selftest: a deliberate fatal exception on the settings UI thread")));
        detail = "selftest: a fatal exception has been posted to the settings UI thread";
        Log(log, detail);
        return true;
    }

    /// <summary>
    /// 把「使用者那串互動」**排進 UI 執行緒的 dispatcher**（不等它做完）：選「自訂資料夾」那個項目、
    /// 把鍵盤焦點放進輸入框（＝真人點下去）、再逐字打 <paramref name="text"/>。
    ///
    /// 為什麼非得這樣排、而不是由勾子直接呼叫：使用者那條路上，例外是在 WPF 自己的 dispatcher frame
    /// （<c>ShowDialog</c> 的 modal 迴圈）裡被丟出來的。由呼叫端直接呼叫時，例外會被勾子自己的
    /// try/catch 接住，**modal frame 根本不會倒**，也就不是同一個故障了。
    /// 排進 dispatcher 之後，它會在 modal frame 裡被執行；例外於是穿過 <c>PushFrame</c> 往上跑，
    /// 與真人點下去的那一次是同一個形狀（2026-10-06 用真的滑鼠點過，兩者一致）。
    ///
    /// <paramref name="trace"/> 逐字記下走到哪一步——序列被中斷在第幾步，就是壞在哪一步的證據。
    /// </summary>
    public static void SelfTestPlayUserStepsInsideTheModalFrame(string text, List<string> trace)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            trace.Add("(the settings UI thread is not running)");
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded) { trace.Add("(no settings window is open)"); return; }

            ComboBoxItem? custom = w.OutputPresetBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, "custom", StringComparison.Ordinal));
            if (custom == null) { trace.Add("(the custom output preset is missing)"); return; }

            w.OutputPresetBox.SelectedItem = custom;
            trace.Add("preset selected (custom)");

            w.OutputPathBox.Focus();                     // ← 真人「點進輸入框」的那一步
            trace.Add($"input focused={w.OutputPathBox.IsKeyboardFocused}");

            w.OutputPathBox.Text = "";
            foreach (char c in text ?? "")
            {
                w.OutputPathBox.Text += c;
                trace.Add($"'{w.OutputPathBox.Text}' notice='{w.OutputNoticeText.Text}'");
            }
            trace.Add("(the whole user sequence returned normally)");
        }));
    }

    /// <summary>
    /// 自測用：現在**真的**有一張設定視窗開著嗎（物件存在，而且已經載入）。
    ///
    /// 為什麼不能只問 <see cref="IsOpen"/>：那只是「UI 執行緒上一次開啟有沒有成功」的快照。
    /// 一個「視窗已經關掉、但 <c>Closed</c> 沒有送達、於是 <c>_isOpen</c> 卡在 true」的 bug，
    /// 會讓只讀 <see cref="IsOpen"/> 的斷言照樣綠燈——而它的後果正是**再開一次被拒絕**，
    /// 也就是使用者回報的那個症狀。所以要問的是視窗物件本身。
    ///
    /// 必須在 UI 執行緒上問：<c>_window</c> 只由那條執行緒存取，跨執行緒讀它是資料競爭
    /// （所以經由 <see cref="RunOnUiThread"/>，與其他勾子同一條路）。
    /// </summary>
    public static bool SelfTestWindowPresent(int timeoutMs, out string detail, Action<string> log)
    {
        string report = "(no settings window)";

        bool ran = RunOnUiThread(timeoutMs, log, "selftest window present", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null)
                return "(no settings window object exists)";
            if (!w.IsLoaded)
            {
                var source = PresentationSource.FromVisual(w) as System.Windows.Interop.HwndSource;
                return $"(a settings window object exists but it is not loaded; " +
                       $"IsLoaded={w.IsLoaded} IsVisible={w.IsVisible} " +
                       $"size={w.ActualWidth}x{w.ActualHeight} " +
                       $"hwnd={(source != null ? "0x" + source.Handle.ToInt64().ToString("X") : "(none)")} " +
                       $"generation={w.Generation})";
            }

            report = $"a settings window is loaded (generation={w.Generation}, path={w.SettingsPath})";
            return null;
        });

        detail = ran ? $"selftest: {report}" : $"selftest: {failure}";
        Log(log, detail);
        return ran;
    }

    /// <summary>
    /// 把目前這張設定頁畫成 PNG。
    ///
    /// 可靠性：<see cref="RenderTargetBitmap"/> 在「還沒排版／還沒被繪製」的視窗上會產生空白圖，
    /// 所以這裡先 <c>UpdateLayout()</c>，並在寫檔**前**把像素解回來數不同顏色——
    /// 整張只有 1 種顏色＝空白圖，這種圖不寫檔、回 false 並說出原因。
    /// 也就是說：回 true 保證檔案裡有真的畫面，不會偶爾交出空白圖。
    /// </summary>
    /// <summary>
    /// 拍照前先換配色？用 <see cref="DumpFrame(string?, string, int, out string, Action{string})"/>。
    /// </summary>
    public static bool DumpFrame(string path, int timeoutMs, out string detail, Action<string> log)
        => DumpFrame(path, null, timeoutMs, out detail, log);

    /// <summary>
    /// 把設定頁拍成一張 PNG（<paramref name="switchPalette"/> 見下）。
    ///
    /// <paramref name="switchPalette"/> 非空時，會在**同一個行程內**先換成那套配色、重套樣式，
    /// 然後才拍。為什麼需要這個入口：兩個行程各拍一張證明不了任何事——那時權杖是在建構
    /// 視窗時就用對的配色算好的；「在 GUI 裡換配色，設定頁會不會跟著變」只有同一個行程
    /// 換兩次才看得到（2026-10-21：改之前不會變，要重開程式才套用）。
    /// </summary>
    public static bool DumpFrame(string path, string? switchPalette, int timeoutMs,
        out string detail, Action<string> log)
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

            if (!string.IsNullOrWhiteSpace(switchPalette))
            {
                if (!Palettes.TrySet(switchPalette, out Palette applied))
                    return $"(unknown palette '{switchPalette}')";
                StyleKit.Apply(w);
                log($"settings ui dump: palette switched in-process to '{applied.Id}' before capture");
            }

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
    /// `--settings-ui-dump-popup <png>`：把某個下拉選單**展開之後**的清單拍成一張 PNG。
    ///
    /// 為什麼要另外一支：WPF 的下拉清單是一個獨立的 <see cref="Popup"/> 視窗，不在主視窗的
    /// 視覺樹裡，所以 <see cref="DumpFrame"/> 永遠拍不到它。2026-10-21 使用者回報「下拉是
    /// 白底白字」時，這個盲點讓那個 bug 躲過了整組驗收——沒有哪一條斷言看過展開之後的樣子。
    ///
    /// 做法：開下拉讓版面跑完，把 <c>PART_Popup</c> 的 Child（那圈 Border 加清單）直接
    /// RenderTargetBitmap 拍下來，拍完關掉。不必把彈出視窗真的留在螢幕上等它關。
    /// 每一列**實際解析到**的底色／字色與對比度會寫進證據行，所以「看不看得清楚」是一個
    /// 數字，不是一句形容詞。
    /// </summary>
    internal static bool DumpPopupFrame(string boxName, string path, int timeoutMs,
        out string detail, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "popup dump: rejected (empty path)";
            Log(log, detail);
            return false;
        }

        byte[]? png = null;
        string report = "(no popup was captured)";

        bool ran = RunOnUiThread(timeoutMs, log, "dump popup", out string failure, () =>
        {
            SettingsWindow? w = _window;
            if (w == null || !w.IsLoaded)
                return "(no settings window is open)";

            if (w.FindName(boxName) is not ComboBox box)
                return $"(no ComboBox named '{boxName}' on this page)";

            w.UpdateLayout();
            if (box.Template?.FindName("PART_Popup", box) is not Popup popup ||
                popup.Child is not FrameworkElement panel)
                return "(the ComboBox template has no PART_Popup child)";

            bool wasOpen = box.IsDropDownOpen;
            box.IsDropDownOpen = true;
            try
            {
                w.UpdateLayout();
                if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
                {
                    panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                    panel.Arrange(new System.Windows.Rect(default, panel.DesiredSize));
                    panel.UpdateLayout();
                }

                int cw = (int)Math.Ceiling(panel.ActualWidth);
                int ch = (int)Math.Ceiling(panel.ActualHeight);
                if (cw <= 0 || ch <= 0)
                    return $"(the popup has no size: {cw}x{ch})";

                // 證據：每一列**畫得出來**的顏色（不是我們宣告了什麼，是解析完剩什麼）。
                // Popup.Child 宣告成 FrameworkElement，而 Aero2 的實際型別是 Border，
                // 所以底色／外框依型別取——不要假設它是哪一種。
                System.Windows.Media.Brush? panelBg = panel switch
                {
                    Border bd => bd.Background,
                    Control ct => ct.Background,
                    System.Windows.Controls.Panel pn => pn.Background,
                    _ => null
                };
                System.Windows.Media.Brush? panelBorder = panel switch
                {
                    Border bd => bd.BorderBrush,
                    Control ct => ct.BorderBrush,
                    _ => null
                };
                SolidColorBrush? panelBrush = panelBg as SolidColorBrush;
                Log(log, $"popup backdrop: box={boxName} size={cw}x{ch} " +
                         $"type={panel.GetType().Name} bg={BrushText(panelBg)} border={BrushText(panelBorder)}");
                for (int i = 0; i < box.Items.Count; i++)
                {
                    if (box.ItemContainerGenerator.ContainerFromIndex(i) is not ComboBoxItem item)
                    {
                        Log(log, $"popup item {i}: (container not realised)");
                        continue;
                    }
                    SolidColorBrush? back = (item.Background as SolidColorBrush) ?? panelBrush;
                    SolidColorBrush? fore = item.Foreground as SolidColorBrush;
                    string ratio = (fore != null && back != null)
                        ? $"{Contrast(fore.Color, back.Color):0.0}:1"
                        : "?";
                    Log(log, $"popup item {i}: bg={BrushText(item.Background)} " +
                             $"backdrop={BrushText(back)} fg={BrushText(fore)} contrast={ratio}");
                }

                var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(panel);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                var buffer = new MemoryStream();
                encoder.Save(buffer);
                byte[] bytes = buffer.ToArray();
                if (bytes.Length == 0)
                    return "(the PNG encoder produced 0 bytes)";

                png = bytes;
                report = $"{boxName} {cw}x{ch}, {bytes.Length} bytes, " +
                         $"backdrop={BrushText(panelBg)}";
                return null;
            }
            finally
            {
                box.IsDropDownOpen = wasOpen;
            }
        });

        if (!ran)
        {
            detail = $"popup dump: {failure}";
            Log(log, detail);
            return false;
        }

        if (png == null)
        {
            detail = $"popup dump: {report}";
            Log(log, detail);
            return false;
        }

        try
        {
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex)
        {
            detail = $"popup dump: could not write {path} ({ex.GetType().Name}: {ex.Message})";
            Log(log, detail);
            return false;
        }

        detail = $"popup dump: {path} ({report})";
        Log(log, detail);
        return true;
    }

    /// <summary>筆刷寫成人看得懂的樣子：`#RRGGBB`、`none`（沒有設定）或 `non-solid`。</summary>
    private static string BrushText(System.Windows.Media.Brush? brush) =>
        brush switch
        {
            null => "none",
            SolidColorBrush s => $"#{s.Color.R:X2}:{s.Color.G:X2}:{s.Color.B:X2}",
            _ => "non-solid"
        };

    /// <summary>
    /// WCAG 2.1 的對比度（前景／背景），1:1 到 21:1。正文的門檻是 4.5:1、大字是 3:1。
    /// 用它把「看不看得清楚」變成一個可斷言的數字。
    /// </summary>
    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(System.Windows.Media.Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>
    /// 把 <paramref name="work"/> 排到 UI 執行緒上執行並**等它做完**。
    /// <paramref name="work"/> 回傳 null 表示成功，否則回傳失敗原因。
    /// 已在這條 UI 執行緒上時直接執行（排隊會自我等待）。不丟例外。
    ///
    /// ⚠ 這裡的「等」是重點：**不可以**拿它來開 modal 視窗（那要等到使用者關窗才返回），
    /// 開窗請用 <see cref="PostToSharedUiThread"/>。
    /// </summary>
    internal static bool RunOnUiThread(int timeoutMs, Action<string>? log, string what,
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
    /// 設定頁與**裁切視窗**的 dump 都用它（同一個「不是空白圖」的判準，不各自實作一份）；
    /// 解不開就當成失敗（回 0）。
    /// </summary>
    internal static int CountDistinctColors(byte[] png)
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
        // ---- 顏色權杖 ----------------------------------------------------------
        //
        // ⚠ 為什麼是屬性而不是 static readonly：直到 2026-10-21 這些是 static readonly，
        // 在類別初始化的那一刻算一次。症狀是「在 GUI 裡換配色，輪盤變了、設定頁沒變」——
        // 要重開程式才套用。現在 key 是 Palettes.CurrentId，換色票後第一次讀就重算
        // （與下面 Font 同一個做法）。
        //
        // ⚠ 第二件事：**「淡一階」在淺色配色要往黑調，不是往白調**。原本這幾個權杖一律疊白：
        // 深色底疊白＝變亮＝看得見；淺色底疊白＝幾乎同色＝看不見（冷瓷實測對比 1.05:1）。
        // 現在方向由遮罩亮度決定（VeilIsLight），透明度百分比兩邊共用——所以五套深色配色的
        // 輸出連一個位元都不變（驗收：五套的設定頁 dump 與改動前逐像素相同）。
        private sealed record Tokens(
            SolidColorBrush WindowBg, SolidColorBrush SurfaceBg, SolidColorBrush CardBorder,
            SolidColorBrush TextMain, SolidColorBrush TextDim, SolidColorBrush TextFaint,
            SolidColorBrush PrimaryBg, SolidColorBrush PrimaryFg, SolidColorBrush SecondaryBorder,
            SolidColorBrush WarningFg, SolidColorBrush StubFg, SolidColorBrush FailFg,
            SolidColorBrush SelectedBg, SolidColorBrush HoverBg,
            SolidColorBrush CropMask, SolidColorBrush SelectionStroke, SolidColorBrush HandleFill);

        private static Tokens? _tokens;
        private static string _tokensFor = "";

        /// <summary>現在這套配色的權杖。換配色（<see cref="Palettes.CurrentId"/> 變了）就重算一次。</summary>
        private static Tokens T
        {
            get
            {
                string id = Palettes.CurrentId;
                if (_tokens == null || !string.Equals(_tokensFor, id, StringComparison.Ordinal))
                {
                    _tokens = BuildTokens();
                    _tokensFor = id;
                }
                return _tokens;
            }
        }

        /// <summary>遮罩夠亮嗎？決定「淡一階」要疊黑還是疊白。</summary>
        private static bool VeilIsLight()
        {
            GdiColor veil = WheelDesign.Veil;
            return RelativeLuminance(System.Windows.Media.Color.FromRgb(veil.R, veil.G, veil.B)) > 0.5;
        }

        private static Tokens BuildTokens()
        {
            GdiColor veil = WheelDesign.Veil;
            // 深色配色疊白、淺色配色疊黑；百分比共用 ⇒ 深色配色的輸出與改動前完全相同。
            GdiColor dimStep = VeilIsLight() ? GdiColors.Black : GdiColors.White;
            return new Tokens(
                WindowBg: Opaque(veil),
                SurfaceBg: Opaque(veil, GdiColors.White, 13),
                CardBorder: Opaque(veil, WheelDesign.Rim, 46),
                TextMain: Opaque(veil, WheelDesign.Label, 255),
                TextDim: Opaque(veil, dimStep, 158),
                TextFaint: Opaque(veil, dimStep, 107),
                PrimaryBg: Opaque(WheelDesign.Rim),
                PrimaryFg: Opaque(WheelDesign.CapFg),
                SecondaryBorder: Opaque(WheelDesign.SelStroke),
                WarningFg: Opaque(WheelDesign.SelStroke),
                StubFg: Opaque(WheelDesign.SelStroke),
                FailFg: Opaque(WheelDesign.NoticeFill),
                SelectedBg: Opaque(WheelDesign.SelFill),
                HoverBg: Opaque(veil, dimStep, 26),
                CropMask: new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                    153, WheelDesign.Veil.R, WheelDesign.Veil.G, WheelDesign.Veil.B)),
                SelectionStroke: new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                    255, WheelDesign.SelStroke.R, WheelDesign.SelStroke.G, WheelDesign.SelStroke.B)),
                HandleFill: new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                    255, WheelDesign.Label.R, WheelDesign.Label.G, WheelDesign.Label.B)));
        }

        /// <summary>不透明的視窗底色：<see cref="WheelDesign.Veil"/> 的顏色（暗罩本身是 .80，這裡取實心）。</summary>
        public static SolidColorBrush WindowBg => T.WindowBg;

        /// <summary>卡片／輸入框底：白 13 % 疊在視窗底色上（輪盤沒有這個權杖，因為它沒有卡片）。</summary>
        public static SolidColorBrush SurfaceBg => T.SurfaceBg;

        /// <summary>卡片外框：外緣薄荷 <see cref="WheelDesign.Rim"/> 的顏色，透明度壓到 18 %（原 55 % 太重）。</summary>
        public static SolidColorBrush CardBorder => T.CardBorder;

        /// <summary>主要內文＝<see cref="WheelDesign.Label"/>（輪盤標籤白）。</summary>
        public static SolidColorBrush TextMain => T.TextMain;

        /// <summary>
        /// 說明文字：深色配色＝白 62 % 疊底；**淺色配色＝黑 62 % 疊底**（往白調會在淺底上消失）。
        /// </summary>
        public static SolidColorBrush TextDim => T.TextDim;

        /// <summary>更弱的一層（設定檔路徑、區塊標題）。同樣依配色的明暗決定疊黑或疊白。</summary>
        public static SolidColorBrush TextFaint => T.TextFaint;

        /// <summary>主要按鈕底＝外緣薄荷 <see cref="WheelDesign.Rim"/>（實心；配深字對比約 17:1）。</summary>
        public static SolidColorBrush PrimaryBg => T.PrimaryBg;

        /// <summary>主要按鈕字＝<see cref="WheelDesign.CapFg"/>（近黑深青）。</summary>
        public static SolidColorBrush PrimaryFg => T.PrimaryFg;

        /// <summary>次要按鈕框＝琥珀 <see cref="WheelDesign.SelStroke"/>（與「選中外框」、「警告」、「尚未生效」同一色）。</summary>
        public static SolidColorBrush SecondaryBorder => T.SecondaryBorder;

        /// <summary>警告文字（含 Shift 的規格警告）＝琥珀。</summary>
        public static SolidColorBrush WarningFg => T.WarningFg;

        /// <summary>「尚未生效」標記＝琥珀。</summary>
        public static SolidColorBrush StubFg => T.StubFg;

        /// <summary>失敗訊息＝<see cref="WheelDesign.NoticeFill"/>（產品內「這是錯誤」的唯一紅）。</summary>
        public static SolidColorBrush FailFg => T.FailFg;

        /// <summary>選中／作用中＝選中扇區填色 <see cref="WheelDesign.SelFill"/>（深色配色是 #3F6B7A）。</summary>
        public static SolidColorBrush SelectedBg => T.SelectedBg;

        /// <summary>
        /// 下拉清單「滑鼠移上去／目前選中」那一列的底色：比 <see cref="SurfaceBg"/> 再差一階
        /// （深色配色疊白 26 %、淺色配色疊黑 26 %）。
        ///
        /// 為什麼非得有這個權杖：WPF 預設的 <c>ComboBoxItem</c> 樣式在 IsHighlighted／IsSelected
        /// 時會把那一列**直接改成主題的淺色底**（模板裡的 setter，不是屬性），而字色是我們設的
        /// 近白 ⇒ 白底白字。2026-10-21 使用者回報的正是這個：下拉一展開，選取中的那一列看不見。
        /// 實測（--settings-ui-dump-popup）：修好前那一列平均亮度 243，其餘五列 51–53。
        /// </summary>
        public static SolidColorBrush HoverBg => T.HoverBg;

        // ---- 裁切視窗（M3 第四顆扇形）----
        // 這三個是**唯一**允許帶透明度的筆刷：它們疊在「使用者自己的照片」上，而不是疊在
        // 不透明的視窗底色上。照片的亮度無法預測，所以遮罩必須是半透明的（不透明的遮罩會把
        // 使用者正在判斷的那張圖整個蓋掉）；琥珀色取自輪盤「選中」的同一支權杖，所以
        // 「被選取的範圍」在全產品是同一個顏色。

        /// <summary>裁切框**以外**的暗化遮罩：暗罩 <see cref="WheelDesign.Veil"/> 壓到 60 %。</summary>
        public static SolidColorBrush CropMask => T.CropMask;

        /// <summary>裁切框線與把手外框＝琥珀 <see cref="WheelDesign.SelStroke"/>（與「選中扇區」同一色）。</summary>
        public static SolidColorBrush SelectionStroke => T.SelectionStroke;

        /// <summary>把手填色＝輪盤標籤白 <see cref="WheelDesign.Label"/>（實心，抓得到的那一種白）。</summary>
        public static SolidColorBrush HandleFill => T.HandleFill;

        /// <summary>字體。無襯線；中文用 <c>Microsoft JhengHei UI</c>，缺字時 WPF 自己往後退。</summary>
        /// <summary>
        /// 目前語言的字型堆疊（2026-10-21：語言從 2 種變 5 種）。
        /// **每次取用都問 <see cref="Typeface"/>**，因為語言是執行期可切換的，
        /// 而 <c>static readonly</c> 只算一次——那樣切到日文之後視窗還是舊字型。
        /// 這裡只快取「堆疊字串沒變就不重建 FontFamily」，切換語言時自然會換掉。
        /// </summary>
        public static FontFamily Font
        {
            get
            {
                string stack = Typeface.Stack;
                if (!string.Equals(stack, _fontStack, StringComparison.Ordinal) || _font is null)
                {
                    _font = new FontFamily(stack);
                    _fontStack = stack;
                }
                return _font;
            }
        }

        private static string _fontStack = "";
        private static FontFamily? _font;

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
                window.OutputLabel,
                window.JpgLabel, window.WebpLabel, window.NamingLabel, window.TimeoutLabel,
                window.HotkeyLabel, window.DiagnosticsLabel, window.TriggerValueText, window.JpgValueText,
                window.WebpValueText, window.TimeoutValueText, window.NamingValueText,
                window.CompressLabel
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
            // 輸出資料夾那一行的提示（規格 §9.5）：與觸發鍵的警告同一個顏色、同一種語意
            window.OutputNoticeText.Foreground = WarningFg;
            // 壓縮方式那一行的提示（M3 第三顆扇形）：同一種語意——「這樣還不能存」
            window.CompressNoticeText.Foreground = WarningFg;
            window.HotkeyStubText.Foreground = StubFg;
            window.StartupStubBadge.Foreground = StubFg;

            PrimaryButton(window.SaveButton);
            SecondaryButton(window.CancelButton);
            SecondaryButton(window.CaptureButton);

            foreach (ComboBox box in new[]
            {
                window.TriggerPresetBox, window.LanguageBox, window.PaletteBox, window.OutputPresetBox,
                window.CompressModeBox, window.CompressUnitBox
            })
            {
                StyleKit.StyleCombo(box);
            }

            // 輸出資料夾的自訂路徑：與緊急熱鍵的文字框同一種外觀（這一頁只有這一種輸入框樣式）
            // 壓縮上限那一格也是同一種輸入框（M3 第三顆扇形）
            foreach (TextBox input in new[] { window.HotkeyBox, window.OutputPathBox, window.CompressTargetBox })
            {
                input.Background = SurfaceBg;
                input.Foreground = TextMain;
                input.BorderBrush = CardBorder;
                input.CaretBrush = TextMain;
                input.FontFamily = Font;
            }

            foreach (ToggleButton toggle in new[]
            { window.StartupToggle, window.HotkeyToggle, window.DiagnosticsToggle })
            {
                toggle.Foreground = TextMain;
                toggle.FontFamily = Font;
                toggle.Template = ToggleTemplate();
                toggle.Cursor = System.Windows.Input.Cursors.Hand;
                // **狀態由這裡還原，不是由呼叫端**：先前的寫法是無條件設 Background = SurfaceBg，
                // 於是「換配色」重跑這一段時，本來是「開」的開關會被悄悄打回「關」（2026-10-21
                // 用 --settings-ui-dump-after-palette 量到：切換後那兩顆的像素既不是新配色也不是
                // 舊配色，因為它們停在關的底色上）。Apply 是「整頁重新上色」的唯一入口，狀態也歸它。
                SetToggleState(toggle, toggle.IsChecked == true);
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

        /// <summary>
        /// 一個下拉選單的全部外觀——**包含清單展開之後的每一列**。
        ///
        /// 這裡之所以是一個方法而不是散在各處的幾行賦值：外框的顏色跟清單裡每一列的顏色
        /// 必須一起換，只換一半的症狀就是「展開之後選取中的那一列白底白字」（2026-10-21 回報）。
        /// </summary>
        public static void StyleCombo(ComboBox box)
        {
            box.Background = SurfaceBg;
            box.Foreground = TextMain;
            box.BorderBrush = CardBorder;
            box.FontFamily = Font;
            // 項目樣式給整個 ComboBox：Popup 裡那些列是它生出來的容器，會套用這一份。
            box.ItemContainerStyle = ComboItemStyle;
        }

        /// <summary>共用一份：同一個 <see cref="Style"/> 可以套在任意數量的控制項上。</summary>
        /// <summary>
        /// 下拉項目的樣式。**依配色快取**：這個樣式包著 <see cref="HoverBg"/> 與
        /// <see cref="SecondaryBorder"/>，換配色時若不重建，滑過與選中的顏色會留在上一套，
        /// 於是「頁面其他部分都換了，只有清單那一列沒換」。
        /// </summary>
        private static Style ComboItemStyle
        {
            get
            {
                string id = Palettes.CurrentId;
                if (_comboItemStyle == null ||
                    !string.Equals(_comboItemStyleFor, id, StringComparison.Ordinal))
                {
                    _comboItemStyle = BuildComboItemStyle();
                    _comboItemStyleFor = id;
                }
                return _comboItemStyle;
            }
        }

        private static Style? _comboItemStyle;
        private static string _comboItemStyleFor = "";

        /// <summary>
        /// 一列下拉選項的樣式。**自己給 ControlTemplate**，不讓主題的 ComboBoxItem 樣式有機會
        /// 插手——它會在 IsHighlighted／IsSelected 時把底色換成淺色，而我們的字是近白。
        /// 顏色權杖只有一份（本類別的靜態欄位），所以靜止／滑過／選中三種狀態永遠同一套語言：
        /// 靜止＝<see cref="SurfaceBg"/>、滑過＝<see cref="HoverBg"/>、選中＝<see cref="SelectedBg"/>
        /// （與設定頁開關「開」的狀態同一個顏色）。
        /// </summary>
        private static Style BuildComboItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBg));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextMain));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, Font));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, System.Windows.Media.Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.BackgroundProperty,
                            new TemplateBindingExtension(Control.BackgroundProperty));
            // 外框也交給樣式：選中那一列靠左側一道琥珀豎條標示（見下面的 selected 觸發條件）
            border.SetValue(Border.BorderBrushProperty,
                            new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,
                            new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));   // 與修改前同一組內距：清單高度不變
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };

            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, HoverBg));
            template.Triggers.Add(hover);

            // 選中那一列**不用** SelectedBg。這裡跟設定頁的開關不同：開關的字在開關外面，
            // 文字從來沒有疊在 SelFill 上；這裡文字疊在自己身上，而 SelFill 在 graphite 是
            // #787B7E 中灰（白字實測 4.3:1，低於 WCAG AA 的 4.5），citrus／celadon 更亮更糟。
            // 所以改成：底色與 hover 同一個 HoverBg（白對它約 19:1）、左側一道琥珀豎條、
            // 字加半粗——「目前的值」照樣一眼看得出來，而且不必靠一條會隨配色變得看不清的底。
            var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, HoverBg));
            selected.Setters.Add(new Setter(Control.BorderBrushProperty, SecondaryBorder));
            selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2, 0, 0, 0)));
            selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            template.Triggers.Add(selected);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
    }
}
