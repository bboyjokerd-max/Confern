namespace Sidecar;

/// <summary>
/// 證據記錄。狀態列採「原地覆寫單行」，逐行事件寫入證據檔——兩者分離，
/// 避免長時執行時洗版（使用者輸出慣例：預設安靜）。
/// 註：Confern 是 WinExe，一般常駐模式下 Console 輸出為 no-op；證據以檔案為準。
///
/// 兩個層級（規格 §8「診斷記錄」）：
/// <list type="bullet">
/// <item><see cref="Line"/>＝必要事件。永遠寫。事件驅動、次數有限，是事後追查的骨架。</item>
/// <item><see cref="Detail"/>＝高頻行。只在診斷開啟時寫。</item>
/// </list>
/// </summary>
internal sealed class Evidence : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _quiet;
    private bool _diagnostics;

    public string Path { get; }

    /// <summary>
    /// 每一行寫下去之後的**通知**（不是第二個寫入者）。
    ///
    /// <para>為什麼要有它：worker 是「一個工作一個行程」，而多個 worker 必須能同時存在且
    /// <b>各自</b>寫自己的證據。做法是把證據寫進每個 worker 專屬的檔案，再把增量鏡射到
    /// 讀取者看的那個穩定檔名（見 <see cref="WorkerLog.Mirror"/>）。鏡射要「跟著每一行走」，
    /// 所以由這裡發出通知，而不是由 worker 的每個 <c>_log</c> 呼叫點各自記得去鏡射——
    /// 漏掉任何一個呼叫點就是靜默漏掉一段證據。UI 行程不訂閱它，行為逐位元不變。</para>
    ///
    /// <para>⚠ 訂閱者的例外**不可以**影響寫日誌本身：日誌是事後追查的唯一證據，
    /// 不能因為「鏡射失敗」而少一行。</para>
    /// </summary>
    public event Action? Mirrored;

    public static string DefaultPath()
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductInfo.Name, "logs");
        return System.IO.Path.Combine(dir, ProductInfo.Slug + ".log");
    }

    public Evidence(string path, bool quiet)
    {
        _quiet = quiet;
        (StreamWriter writer, string actualPath, string? fallbackNote) = OpenLog(path);
        _writer = writer;
        Path = actualPath;
        Line($"=== {ProductInfo.Name} session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
        // 先宣告預設模式：讀日誌的人必須能分辨「detail 行不在這裡」是**設定**，不是程式壞掉。
        Line("diagnostics=off (essential events only)");
        // 退路要留痕：否則使用者照著錯誤訊息找不到日誌，或是以為「證據沒了」。
        if (fallbackNote != null) Line(fallbackNote);
    }

    /// <summary>
    /// 開日誌檔，**被獨占時退到每個行程自己的檔名**。
    ///
    /// <para>為什麼需要這條退路（2026-10-22 實測到的真崩潰）：預設日誌是
    /// <c>%LOCALAPPDATA%\Confern\logs\confern.log</c>，<see cref="System.IO.StreamWriter(string, bool)"/>
    /// 用的是 <c>FileShare.Read</c>——所以**只要常駐的 Confern 在跑**，第二個行程要開它就會得到
    /// <see cref="IOException"/>。而這個例外是在 <see cref="Evidence"/> 的建構子裡、在任何
    /// try 之外，於是整個行程以 <c>Unhandled exception</c> 死掉（exit code 0xE0434352）。
    /// 對 GUI 常駐程式無害（它自己就是持有者），但對**命令列**是致命的：
    /// `confern convert` 的重點正是「托盤程式開著的時候我也能用」。</para>
    ///
    /// <para>退路檔名帶 pid，所以多個 CLI 同時跑也互不干擾；沿用
    /// <see cref="WorkerLog.PruneStale"/> 的同一條年齡規則清舊檔（不是「留最新 N 個」——
    /// 那會刪到正在跑的那一個）。</para>
    /// </summary>
    private static (StreamWriter Writer, string Path, string? Note) OpenLog(string requested)
    {
        string dir = System.IO.Path.GetDirectoryName(requested)!;
        Directory.CreateDirectory(dir);
        try
        {
            return (new StreamWriter(requested, append: false) { AutoFlush = true }, requested, null);
        }
        catch (IOException)
        {
            string alt = System.IO.Path.Combine(
                dir, $"{ProductInfo.Slug}.{Environment.ProcessId}.log");
            try
            {
                PruneStaleCliLogs(dir);
                return (new StreamWriter(alt, append: false) { AutoFlush = true }, alt,
                        $"log: '{System.IO.Path.GetFileName(requested)}' is held by another Confern " +
                        $"process (the resident one is running); this session writes " +
                        $"'{System.IO.Path.GetFileName(alt)}' instead");
            }
            catch (IOException ex)
            {
                // 連退路都開不起來：**不再拋**。改成丟掉日誌、把原因印到 stderr，
                // 讓命令本身（例如格式清單、轉檔）仍然能做事——證據少一份，
                // 比整個指令死掉好。這一條只有在日誌資料夾本身有問題時才會走到。
                Console.Error.WriteLine($"confern: cannot open any log file in '{dir}' ({ex.Message}); " +
                                        "continuing without evidence");
                return (new StreamWriter(Stream.Null) { AutoFlush = true }, alt,
                        "log: unavailable (see stderr)");
            }
        }
    }

    /// <summary>清掉 7 天前的每個行程日誌（退路的殘留）。</summary>
    private static void PruneStaleCliLogs(string dir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(
                         dir, $"{ProductInfo.Slug}.*.log", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    try { File.Delete(file); } catch { /* 別人的，算了 */ }
                }
            }
        }
        catch { /* 清舊檔失敗不影響寫日誌 */ }
    }

    /// <summary>逐行事件（可稽核的證據）。</summary>
    public void Line(string text)
    {
        _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        if (!_quiet) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
        NotifyMirrored();
    }

    /// <summary>
    /// 通知鏡射者。**吞掉它的例外**：鏡射是共用名的副本，而這裡的檔案才是證據本體——
    /// 讓鏡射的失敗往上冒會變成「日誌寫不下去」，那是拿副本去擋本體（順序反了）。
    /// 鏡射者自己會把失敗記進本體（見 <see cref="WorkerLog.Mirror"/>）。
    /// </summary>
    private void NotifyMirrored()
    {
        try { Mirrored?.Invoke(); }
        catch { /* 鏡射失敗不得影響本體 */ }
    }

    /// <summary>
    /// 「診斷記錄」（規格 §8）的語意——2026-10-02 使用者裁定為 (b)：
    /// **關＝只寫必要事件（預設）；開＝再加上高頻的 detail 行**。不是「關＝完全不寫日誌」。
    ///
    /// 為什麼選 (b)：日誌是唯一的事後追查證據，而且全程留在本機、不上網；關掉它等於把事故現場
    /// 直接丟掉，換來的只有「檔案小一點」。代價是關閉時日誌看起來比較空——所以模式一律寫進
    /// 日誌開頭，而且每次變更都留一行。
    /// </summary>
    public void SetDiagnostics(bool on)
    {
        if (_diagnostics == on) return;
        _diagnostics = on;
        Line(on ? "diagnostics: off -> on (detail events included)"
                : "diagnostics: on -> off (essential events only)");
    }

    /// <summary>
    /// 高頻行（每個 tick、每次移動之類）：只在診斷開啟時寫。
    ///
    /// 判準：**這行在正常操作中會一秒出現很多次嗎？** 會 → <see cref="Detail"/>；
    /// 不會（事件驅動、次數有限）→ <see cref="Line"/>。
    ///
    /// ⚠ 驗收腳本會斷言的行**不可以**放這裡：腳本預設跑在診斷關閉之下，降級會讓斷言變紅燈。
    /// 已降級的只有 `progress tick`（轉檔期間每個進度更新一次）。
    /// 反例：`wheel hover selection` 拖曳時也會反覆出現，但 m1b S8.9 斷言它 ≥1 次 → 必須留在 Line。
    /// </summary>
    public void Detail(string text)
    {
        if (_diagnostics) Line(text);
    }

    /// <summary>單行狀態（原地覆寫，不洗版）。</summary>
    public void Status(string text) => Console.Error.Write("\r" + text.PadRight(78));

    public void Dispose()
    {
        Line("=== session end ===");
        _writer.Dispose();
        // 收尾再鏡射一次：最後那一行（`=== session end ===`）一定要進共用檔，
        // 否則讀取者看到的是一份少了結尾的證據。
        NotifyMirrored();
    }
}
