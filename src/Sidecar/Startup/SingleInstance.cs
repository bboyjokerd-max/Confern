namespace Sidecar;

/// <summary>
/// 常駐 GUI 的**單一實例守衛**（待辦：兩個 Confern 同時跑＝兩套全域滑鼠鉤子、兩個托盤圖示、
/// 同一個手勢被處理兩次）。
///
/// <para>
/// ⚠ **這一格只作用在「常駐 GUI 模式」上，這是本案最重要的一件事。**
/// 這個 exe 同時是好幾種東西：
/// <list type="bullet">
/// <item><b>worker 行程</b>（<c>--worker &lt;pipe&gt;</c>，規格 §9.1）：它是**每個工作被 spawn 一個**的，
/// 天生就必須能同時存在多個（批次轉檔同時跑好幾個檔）。擋掉它＝整個轉檔功能壞掉。</item>
/// <item><b>一堆跑完就結束的診斷模式</b>（<c>--convert</c>、<c>--crop-ui-selftest</c>、
/// <c>--tray-selftest</c>、<c>--settings-ui-selftest</c>…）：驗收套件大量使用它們，而且它們
/// <b>最可能被跑的時間點就是使用者自己開著 Confern 的時候</b>。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>怎麼判定「這是不是常駐 GUI 模式」：用「位置」，不是用旗標。</b>
/// <see cref="Acquire"/> 的呼叫點在 <see cref="Program.Main"/> 裡只有一個：所有診斷分支
/// （含 worker）**都已經 return 之後**、呼叫 <see cref="App.Run"/> **之前**那一行。
/// 換句話說：**能走到那一行 ⇔ 沒有任何診斷旗標被設定過 ⇔ 這就是常駐 GUI 模式**。
/// 既有程式碼已經照這個規矩排好（<c>--convert</c>／<c>--crop</c>／<c>--tools-list</c> 等分支
/// 都帶註解說明「刻意放在守衛之前」），所以新增診斷時只要照同樣的風格放在守衛前面就自動免疫。
/// </para>
///
/// <para>
/// <b>為什麼不用「檢查某個旗標」來判定</b>：那需要維護一份「哪些旗標算診斷」的清單，
/// 而漏掉一個的後果是——那條診斷在使用者開著 Confern 時**靜默地回一個 3**，
/// 什麼都量不到卻看起來像「跑完了」。那比原本的 bug 嚴重得多，所以刻意不給它一個旗標開關。
/// </para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// 具名 mutex 的名稱。**這是這個名稱的唯一擁有者**：<see cref="ProductInfo.Name"/> 是
    /// <c>const</c>，所以整串是編譯期常數，改名時兩邊自動一致（不會出現「改了九成」）。
    ///
    /// <para>
    /// <c>Local\</c> 而不是 <c>Global\</c>：這是要擋「同一個使用者、同一個登入工作階段
    /// 把它開兩次」這個實際症狀。用 <c>Global\</c> 會讓「另一個使用者登入時各自開一份」
    /// 也變成互斥——那是不對的（兩個使用者本來就該各有一份自己的常駐實例與托盤圖示）。
    /// </para>
    /// </summary>
    public const string MutexName = @"Local\" + ProductInfo.Name + ".SingleInstance";

    private readonly Mutex _mutex;
    private bool _ownsSlot;

    /// <summary>本行程是否搶到了常駐槽位（＝有權成為那個唯一常駐實例）。</summary>
    public bool IsOwner => _ownsSlot;

    private SingleInstance(Mutex mutex, bool ownsSlot)
    {
        _mutex = mutex;
        _ownsSlot = ownsSlot;
    }

    /// <summary>
    /// 嘗試搶佔常駐槽位。**不等待、不阻塞**：第二個實例要的是立刻知道結果，
    /// 而不是排在第一個後面等它關掉（那會讓行程憑空卡住，比直接退出更糟）。
    ///
    /// <para>
    /// <b>不建立任何需要清理的檔案鎖</b>（A13：被強制結束後必須能立刻重啟）。
    /// Windows 核心物件在最後一個 handle 消失時就自動消失——這正是用具名 mutex
    /// 而不是 lock 檔的理由：lock 檔會在當機後留下一份「假的執行中」，而那需要一個清理者
    /// 去分辨「還在跑」與「上次當掉留下的」。
    /// </para>
    ///
    /// <para>
    /// 為什麼是 <c>WaitOne(0)</c> 而不是 <c>new Mutex(initiallyOwned: true, …)</c> 配 <c>createdNew</c>：
    /// 後者只回答「我是不是**建立者**」，而「建立者」與「擁有者」在一個情況會分家——
    /// 前一個實例被強制結束、mutex 處於 **abandoned** 狀態時，物件還在（有別的 handle），
    /// 但沒有人擁有它。那時 <c>createdNew</c> 是 false，於是守衛會**永遠**拒絕啟動，
    /// 使用者的下場是「按了都沒反應，要重開機」——正是這個守衛不准造成的結果。
    /// <c>WaitOne(0)</c> 對三種情況各自給出正確答案：
    /// 剛建立 ⇒ 成功；被活的實例持有 ⇒ false（拒絕，正確）；
    /// abandoned ⇒ 丟 <see cref="AbandonedMutexException"/>，而那是「由我接手」的意思。
    /// </para>
    /// </summary>
    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 前一個實例沒有好好放手就消失了（工作管理員、當機）。所有權在例外發生的同時
            // 已經轉移給本執行緒，所以這裡是「接手」，不是「失敗」。
            owned = true;
        }

        return new SingleInstance(mutex, owned);
    }

    /// <summary>
    /// 第二個實例的**明確且有定義**的行為：留一筆看得見的證據，然後**安靜地**以 <c>3</c> 結束
    /// （與其他「跑完了但沒有結果」的診斷同一個碼，見 <see cref="App.Run"/> 的 <c>--dump-monitors</c>）。
    ///
    /// <para>
    /// <b>為什麼選「安靜退出」而不是「喚起第一個實例」：</b>第一個實例**本來就還在跑**——
    /// 托盤圖示還在、鉤子還裝著、手勢照常處理。使用者要的「程式有在跑」已經被滿足了。
    /// 反過來說，多做一個「把第一個帶到前景」的動作，會在第二個實例**不是使用者親自按的**時候
    /// 突然搶走焦點（例如開機自動啟動與桌面圖示幾乎同時觸發）。
    /// </para>
    ///
    /// <para>
    /// <b>為什麼不彈任何視窗：</b>使用者按第二次的情境是「我以為沒開」。那時一個警告框
    /// 只會讓人更慌，而且常駐程式沒有前景視窗，對話框會憑空冒出來。守衛不該嚇人。
    /// </para>
    ///
    /// <para>
    /// <b>為什麼一定要寫檔案（不能只寫 stderr）：</b>Confern 是 WinExe，從桌面圖示啟動時
    /// **沒有主控台**，<c>Console.Error</c> 一個人都看不到。少了檔案那一份，第二個實例
    /// 對使用者就是「按了完全沒反應」——而那是這個守衛不准造成的結果。
    /// </para>
    /// </summary>
    public static int Refuse(string requestedLogPath)
    {
        Native.AttachParentConsole();
        string where = Notice(requestedLogPath,
            $"single instance: another {ProductInfo.Name} is already running (mutex {MutexName}); " +
            "this launch exits now: no hooks, no tray icon, no window, no settings change");
        Console.Error.WriteLine(
            $"{ProductInfo.Name} is already running; refusing to start a second resident instance " +
            $"(the first one keeps running). Evidence: {where}");
        return 3;
    }

    /// <summary>
    /// <c>--single-instance-probe</c>：**只問不佔**——回報本行程拿不拿得到常駐槽位，然後立刻放掉。
    ///
    /// <para>
    /// 這條診斷是為了讓「守衛到底有沒有在作用」變成一個**可以當場量到的事實**，而不是
    /// 靠讀程式碼相信。它自己一定得排在守衛**之前**才會有意義（否則它問的永遠是
    /// 「守衛有沒有把我擋掉」，而不是「槽位被誰佔著」）。
    /// </para>
    ///
    /// <para>它不建視窗、不裝鉤子、不碰托盤、不寫任何設定，跑完就結束。
    /// 離開碼沿用守衛自己的碼：<c>0</c>＝拿得到（目前沒有常駐實例）、<c>3</c>＝被佔著。</para>
    /// </summary>
    public static int Probe(string requestedLogPath)
    {
        using SingleInstance instance = Acquire();
        bool got = instance.IsOwner;
        string line = got
            ? "single-instance probe: acquired (no resident " + ProductInfo.Name +
              " holds the slot; this probe releases it again before exiting)"
            : "single-instance probe: refused (a resident " + ProductInfo.Name + " is already running)";

        Native.AttachParentConsole();
        string where = Notice(requestedLogPath, line);
        Console.WriteLine(line);
        Console.Error.WriteLine($"single-instance probe: acquired={got} evidence={where}");
        return got ? 0 : 3;
    }

    /// <summary>
    /// 把一行通知寫進日誌，並回傳**真的寫進去的路徑**（用來印給使用者看，不假裝寫成功）。
    ///
    /// <para>
    /// 這裡刻意**不用 <see cref="Evidence"/>**：<see cref="Evidence"/> 以「截斷」模式開啟
    /// （<c>new StreamWriter(path, append: false)</c>、預設 <c>FileShare.Read</c>），
    /// 而第二個實例最典型的處境就是「第一個實例正握著同一份日誌」——那時要嘛開不起來，
    /// 要嘛（更糟）把還活著的那個實例的日誌截斷掉。所以這裡改用**附加模式 ＋ FileShare.ReadWrite**，
    /// 而且失敗時誠實退到一個專屬檔名，不是靜默地什麼都不寫。
    /// </para>
    /// </summary>
    private static string Notice(string requestedLogPath, string line)
    {
        string preferred = string.IsNullOrEmpty(requestedLogPath) ? Evidence.DefaultPath() : requestedLogPath;
        string? written = TryAppend(preferred, line);
        if (written is not null) return written;

        // 退路：與偏好的日誌同一個資料夾，但換一個專屬檔名。名字裡有 slug，
        // 所以它跟正式日誌放在一起、一看就知道是誰寫的。
        string dir = System.IO.Path.GetDirectoryName(preferred) is { Length: > 0 } d
            ? d
            : ProductInfo.DataDirectory;
        string fallback = System.IO.Path.Combine(dir, ProductInfo.Slug + ".second-instance.log");
        return TryAppend(fallback, line) ?? fallback + " (could not be written)";
    }

    private static string? TryAppend(string path, string line)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // FileShare.ReadWrite: 讓「還活著的那個實例」也能同時寫同一份檔案；
            // FileMode.Append: 絕不截斷別人已經寫下去的東西。
            using var writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsSlot)
        {
            // 好好放手（而不是靠行程結束讓它變成 abandoned）：下一個實例就會走
            // 「剛建立」那條乾淨的路，不會踩到 AbandonedMutexException 的補救路徑。
            _ownsSlot = false;
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* 不是擁有者：不該發生，但也不該炸掉 */ }
        }

        _mutex.Dispose();
    }
}
