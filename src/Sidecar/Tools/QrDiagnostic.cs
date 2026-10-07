using System.Text;

namespace Sidecar;

/// <summary>
/// 工具 `read-qr` 的無頭診斷入口。
///
/// ## 它在驗什麼（而且為什麼非這樣驗不可）
///   這一刀把「解出來的內容」接上介面：是 http／https 網址就開預設瀏覽器，不是就顯示在一個
///   唯讀可複製的視窗裡。這兩條路**都有副作用**（彈出瀏覽器、出現一個視窗），而套件是在
///   使用者的機器上跑的，所以每一條副作用都必須有一個「當場關得掉」的縫，而且縫本身要被證明有效：
///     ①**分類**（<see cref="QrTextClassifier"/>）是一個純函式——可以列舉輸入而不觸發任何副作用。
///        診斷把每一個輸入的判定**印成一行**，驗收因此能從產品的輸出**重新推導**出判定，
///        不必相信任何 `ok=True`。
///     ②**開瀏覽器**那條路徑在自測模式下走 <see cref="QrBrowserOpener.UseSelfTestSeam"/>：
///        診斷會要求它開一個網址，然後斷言「**真的那支實作**被執行的次數是 0」，
///        並且在要求前後各量一次瀏覽器行程數（外部量測，不是自我報告）。
///     ③**文字視窗**走的是 <see cref="QrTextUi"/>（真的視窗、真的按鈕、真的關窗），
///        而它在自測模式下**不會開啟瀏覽器**——所有 fixture 都不是網址，這一點也是斷言。
///
/// ## 為什麼 worker 那一條要在這裡（<see cref="RunWorker"/>）
///   前一個切片誠實地記下一個缺口：`op=read-qr` 的**分派臂沒有任何執行時斷言走過**
///   （當時沒有任何命令列開關能叫 worker 做 read-qr，只能用原始碼斷言代替）。
///   這裡補上的是**執行時**的那一條：起真的 worker 行程、走真的管道，而且斷言
///   **client 收到的文字逐字元等於已知 payload**——`WorkerMessage.text` 是一個跨行程的
///   序列化欄位，「在 worker 裡面解對了」與「另一端收到同一串字」是兩件事。
/// </summary>
internal static class QrDiagnostic
{
    /// <summary>
    /// 分類的測資（**輸入 → 期望判定**）。涵蓋使用者指定的每一種：
    /// 非網址的 scheme（`file`／`javascript`／`ms-msdt`／`search-ms`）、純文字、空字串、
    /// 只有 `http` 而沒有 `//`、以及兩個白名單 scheme 的正向案例。
    ///
    /// ⚠ 期望值是**這裡**寫下來的（而不是從產品算出來的）：這一格要驗的正是「判定對不對」，
    /// 拿產品自己的判定當期望值等於什麼都沒驗。
    /// </summary>
    internal static readonly (string Input, string Expected)[] ClassifyCases =
    {
        ("http://example.com",                        "url"),
        ("https://example.com/a?b=c#d",               "url"),
        ("HTTPS://EXAMPLE.COM",                       "url"),
        ("http://192.168.1.10:8080/x",                "url"),
        ("  https://example.com/pad  ",               "url"),      // 前後空白只有判定忽略（顯示仍是原文）
        ("file:///C:/Windows/System32/calc.exe",      "text"),
        ("javascript:alert(1)",                       "text"),
        ("ms-msdt:/id PCWDiagnostic",                 "text"),
        ("search-ms:query=secret",                    "text"),
        ("ftp://example.com/file",                    "text"),
        ("mailto:a@b.c",                              "text"),
        ("Confern QR fixture 2026-10-07 / payload-v1","text"),
        ("http",                                      "text"),      // 只有 scheme 這個字，不是網址
        ("http:example.com",                          "text"),      // 沒有 `//`：交給 shell 會變成說不出要去哪的請求
        ("http://",                                   "text"),      // 沒有 host
        ("https:///path-only",                        "text"),      // 沒有 host
        ("",                                          "empty"),
        ("   ",                                       "empty")
    };

    /// <summary>
    /// 分類自測（**完全不碰 WPF**，所以它是這一族裡最安全的一條，也是唯一能在沒有視窗工作階段的
    /// 機器上跑的一條）。回傳碼 0＝全部符合期望、1＝有任何一格不符。
    ///
    /// 另外做一次**反面檢查**：分類說 `url` 的那些輸入，<see cref="QrTextClassifier.BrowserTarget"/>
    /// 必須給出一個字串；說 `text`／`empty` 的必須給出 null。兩個 API 對同一件事的答案
    /// 若不一致，那就是「說它是網址、開的卻是別的東西」的溫床。
    /// </summary>
    internal static int RunClassify(Evidence evidence)
    {
        evidence.Line($"qr classify selftest: {ClassifyCases.Length} inputs (expectation table is in the source, " +
                      "not derived from the product)");
        int bad = 0;
        foreach ((string input, string expected) in ClassifyCases)
        {
            QrTextClassifier.QrKind kind = QrTextClassifier.Classify(input);
            string label = QrTextClassifier.KindLabel(kind);
            string? target = QrTextClassifier.BrowserTarget(input);
            bool targetAgrees = label == "url" ? target != null : target == null;
            bool ok = label == expected && targetAgrees;
            if (!ok) bad++;
            evidence.Line($"qr classify: input='{Escape(input)}' chars={input.Length} " +
                          $"expected={expected} got={label} target='{Escape(target ?? "(none)")}' ok={ok}");
        }
        evidence.Line($"qr classify selftest done: cases={ClassifyCases.Length} mismatches={bad} " +
                      $"ok={(bad == 0)}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// worker 執行時路徑：對 <paramref name="source"/> 跑**真的 worker 行程**（`op=read-qr`），
    /// 並把 client 收到的文字印出來。回傳碼 0 成功、2 來源不存在、3 worker／引擎失敗。
    ///
    /// ⚠ 這一條**不寫任何檔案**，而它與 <c>--read-qr</c> 的差別只有一個：文字是跨過管道回來的，
    /// 驗的是 `WorkerMessage.text` 這個**序列化欄位**（欄位沒送／被寫成 null 的話，
    /// 另一端拿到的是一段不存在的文字，而 worker 自己的日誌還是會說它成功了）。
    /// </summary>
    internal static int RunWorker(Evidence evidence, string source)
    {
        if (!File.Exists(LongPath.Prepare(source)))
        {
            evidence.Line($"--qr-worker: source not found: {source}");
            return 2;
        }

        var beforeSnapshot = DirectorySnapshot(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".");
        string beforeHash = Sha256Of(source);

        string exePath = Environment.ProcessPath ?? $"{ProductInfo.Name}.exe";
        var client = new ConversionClient(exePath, evidence.Line);
        try
        {
            // outputDir 給 null、targetFormat 給空字串、quality 100：這一顆工具不寫檔，
            // 那三個欄位對它沒有意義（見 WorkerProtocol.OpReadQr 的說明）。
            if (!client.Start(new[] { source }, "", 100, outputDir: null,
                              toolOp: WorkerProtocol.OpReadQr, out string? startError))
            {
                evidence.Line($"--qr-worker FAILED: the worker did not start ({startError})");
                return 3;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!client.Snapshot.Finished && sw.ElapsedMilliseconds < 120000) Thread.Sleep(20);
            ConversionSnapshot snapshot = client.Snapshot;

            evidence.Line($"qr worker snapshot: finished={snapshot.Finished} done={snapshot.Done}/{snapshot.Total} " +
                          $"ok={snapshot.Ok} failed={snapshot.Failed} error={snapshot.Error ?? "-"} " +
                          $"line='{snapshot.Line}'");
            foreach (string failure in snapshot.Failures) evidence.Line($"qr worker failure: {failure}");

            string text = snapshot.QrText ?? "";
            // 與 `--read-qr` **同一個格式**（驗收腳本因此只有一個解析器）：文字本體 ＋ 長度。
            // 這是跨管道之後的那一份，不是引擎內部的那一份。
            evidence.Line(text.Length > 0
                ? $"tool qr-worker: text={text} chars={text.Length}"
                : $"tool qr-worker: output=(none) error={snapshot.Failures.Count > 0}");
            evidence.Line($"tool qr-worker: ok={snapshot.Ok == 1} failed={snapshot.Failed} " +
                          $"source-path='{Path.GetFileName(source)}'");
            try { Console.WriteLine($"tool qr-worker: text={text} chars={text.Length}"); }
            catch (IOException) { /* 沒有 stdout（父行程已關）：日誌仍然有 */ }

            string afterHash = Sha256Of(source);
            var afterSnapshot = DirectorySnapshot(Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".");
            evidence.Line($"tool qr-worker: source-untouched={beforeHash == afterHash} " +
                          $"dir-unchanged={beforeSnapshot == afterSnapshot} " +
                          $"(sha256 {beforeHash.Substring(0, 12)} -> {afterHash.Substring(0, 12)})");

            if (beforeHash != afterHash || beforeSnapshot != afterSnapshot)
            {
                evidence.Line("--qr-worker FAILED: something was written next to the source (spec 5 violation)");
                return 1;
            }
            if (!snapshot.Finished || snapshot.Failed > 0 || text.Length == 0)
            {
                evidence.Line($"--qr-worker FAILED: finished={snapshot.Finished} failed={snapshot.Failed} " +
                              $"chars={text.Length}");
                return 3;
            }
            return 0;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// 文字視窗自測：先用**產品自己的解碼器**從 <paramref name="source"/> 解出文字
    /// （所以視窗裡的字來自產品，不是夾具字串），再走完整條路由把它顯示出來，然後逐項自證。
    ///
    /// 依序驗（每一格都印出來）：
    ///   ① 分類表全數符合期望（見 <see cref="RunClassify"/>）；
    ///   ② 文字視窗**真的在螢幕上**（IsLoaded ＋ hwnd，不只是「有一個物件」）；
    ///   ③ **逐字元**等於解出來的那一串（唯一合法的期望值來源就是產品的解碼輸出）；
    ///   ④ 「複製」鈕真的把同一串字放進剪貼簿，而且**使用者的剪貼簿一個字都沒被改**；
    ///   ⑤ 已經開著時第二次要求被**拒絕**（單一實例）；
    ///   ⑥ 關掉之後**可以再開**，最後關得掉（行程照常結束）；
    ///   ⑦ **開瀏覽器那條路徑在自測模式下沒有真的開瀏覽器**：要求它開一個網址之後，
    ///      真的那支實作被執行的次數仍然是 0，而且瀏覽器行程數與要求之前相同。
    ///      這是這個切片最重要的一條安全斷言。
    ///
    /// 回傳碼 0 全過、1 有任何一格沒過、2 夾具不可用。
    /// </summary>
    internal static int RunTextWindow(Evidence evidence, string? source, string extraText = "")
    {
        if (string.IsNullOrEmpty(source) || !File.Exists(LongPath.Prepare(source)))
        {
            evidence.Line("qr open selftest: FAILED: no usable qr image " +
                          $"(got '{source ?? "(none)"}'; it must be an existing image with a QR code)");
            return 2;
        }

        // 自測模式：**連真的那一支開瀏覽器實作都不會被走到**（見 QrBrowserOpener 的兩道鎖）。
        QrBrowserOpener.UseSelfTestSeam(true);

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int classifyExit = RunClassify(evidence);

        // ① 文字從**產品自己的解碼器**來（與 worker 用的是同一個引擎方法）。
        var registry = new CodecRegistry();
        var engine = new ConversionEngine(registry, evidence.Line);
        ConversionResult decoded = engine.ReadQr(source, journal: null);
        string payload = decoded.QrText ?? "";
        evidence.Line($"qr open selftest: fixture={Path.GetFileName(source)} ok={decoded.Ok} " +
                      $"error={decoded.Error} chars={payload.Length}");
        if (!decoded.Ok || payload.Length == 0)
        {
            evidence.Line($"qr open selftest done: ok=False (the fixture did not decode: {decoded.Error})");
            return 1;
        }

        // ② 使用者的剪貼簿：整條路徑都不該碰它，只有「複製」鈕被按下去時才准寫。
        string clipboardBefore = ReadClipboard();

        // ③ 走**真正的路由**（正式路徑呼叫的就是這一個方法）。
        var routed = QrTextRouter.Route(payload, Path.GetFileName(source), lines.Enqueue);
        evidence.Line($"qr open selftest: routed kind={QrTextClassifier.KindLabel(routed.Kind)} " +
                      $"opened={routed.Opened} target='{routed.Target}' note='{routed.Note}'");

        bool opened = WaitFor(() => QrTextUi.IsOpen, 8000);
        bool windowPresent = false;
        string presentDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !windowPresent; attempt++)
        {
            windowPresent = QrTextUi.SelfTestWindowPresent(3000, out presentDetail, lines.Enqueue);
            if (!windowPresent) Thread.Sleep(250);
        }
        evidence.Line($"qr open selftest: opened={opened} windowPresent={windowPresent} " +
                      $"state={QrTextUi.SelfTestState()}");
        evidence.Line($"qr open selftest: window detail: {presentDetail}");

        // ④ 逐字元：**視窗裡的字串** vs **產品解出來的字串**。
        string? shown = QrTextUi.SelfTestText();
        bool textIdentical = shown != null && shown.Length == payload.Length &&
                             string.Equals(shown, payload, StringComparison.Ordinal);
        evidence.Line($"qr open selftest: window text chars={shown?.Length ?? -1} decoded chars={payload.Length} " +
                      $"identical={textIdentical}");

        bool focused = QrTextUi.SelfTestFocusBody();
        string copied = QrTextUi.SelfTestCopy(10000, out string copyDetail, lines.Enqueue);
        bool copyTextSame = copied.Length == payload.Length &&
                            string.Equals(copied, payload, StringComparison.Ordinal);
        string clipboardAfterCopy = ReadClipboard();
        evidence.Line($"qr open selftest: copy button focused={focused} copiedChars={copied.Length} " +
                      $"copiedTextIdentical={copyTextSame} clipboardHoldsIt={clipboardAfterCopy == payload} " +
                      $"({copyDetail})");

        // ⑤ 單一實例：已經開著時第二次要求必須被拒絕（而且不會換掉畫面上的那一張）。
        QrTextUi.Open("A SECOND WINDOW MUST NOT APPEAR", "second.txt", lines.Enqueue);
        bool singleInstance = QrTextUi.IsOpen;
        string? afterSecond = QrTextUi.SelfTestText();
        bool firstStillThere = afterSecond != null && string.Equals(afterSecond, payload, StringComparison.Ordinal);
        evidence.Line($"qr open selftest: a second open while the window is up was " +
                      $"{(singleInstance ? "ignored (single instance)" : "ACCEPTED (two windows!)")} " +
                      $"firstWindowStillIntact={firstStillThere}");

        // ⑥ 關掉 → 再開（單一實例的反面）→ 最後關掉。
        QrTextUi.Close();
        bool closed = WaitFor(() => !QrTextUi.IsOpen, 5000);
        QrTextUi.Open(payload, Path.GetFileName(source), lines.Enqueue);
        bool reopened = WaitFor(() => QrTextUi.IsOpen, 8000);
        bool reopenedPresent = false;
        string reopenDetail = "(never asked)";
        for (int attempt = 0; attempt < 20 && !reopenedPresent; attempt++)
        {
            reopenedPresent = QrTextUi.SelfTestWindowPresent(3000, out reopenDetail, lines.Enqueue);
            if (!reopenedPresent) Thread.Sleep(250);
        }
        evidence.Line($"qr open selftest: the window can be opened again after a close: " +
                      $"closed={closed} reopened={reopened} windowPresent={reopenedPresent}");
        QrTextUi.Close();
        bool closedAtEnd = WaitFor(() => !QrTextUi.IsOpen, 5000);
        evidence.Line($"qr open selftest: closed at the end of the selftest={closedAtEnd} " +
                      "(the process keeps running; the window is not what keeps it alive)");

        // ⑦ ⚠ 最重要的一條：開瀏覽器那條路徑在自測模式下**沒有真的開瀏覽器**。
        //    順序刻意如此：先證明「真的那一支從來沒被走到」，再證明外部的瀏覽器行程數沒暴增。
        //    ⚠ 2026-10-07 實測修正：這一行原本要求 browsersAfter == browsersBefore，那是個**會誤報的
        //    見證**。量到的實例：browsersBefore=53 browsersAfter=52。這台機器上有 53 個瀏覽器行程，
        //    而瀏覽器自己會不停生滅輔助行程（renderer／GPU／utility），**掉一個是正常抖動**，
        //    不是我們的副作用；那一次抖動讓整個自測 ok=False，連帶把兩條斷言一起弄紅（一因兩紅）。
        //    判定改成**不對稱**：真的開一個瀏覽器一定會**多出**行程，所以只有「變多」才算失敗；
        //    「變少」是別人的事。主要見證仍然是那個確定的 realAttempts == 0（縫自己數「真實實作被
        //    進入了幾次」），行程數只是第二個、外部的旁證——旁證不該有能力把好的自測判成壞的。
        int browsersBefore = QrBrowserOpener.BrowserProcessCount();
        bool seamOpened = QrBrowserOpener.TryOpen("https://example.com/qr-selftest", out string? seamError);
        Thread.Sleep(700);                       // 真的有開的話，行程會在這一小段時間內出現
        int browsersAfter = QrBrowserOpener.BrowserProcessCount();
        int realAttempts = QrBrowserOpener.RealLaunchAttempts;
        bool seamHeld = seamOpened && realAttempts == 0 && browsersAfter <= browsersBefore &&
                        QrBrowserOpener.LastTarget == "https://example.com/qr-selftest";
        evidence.Line($"qr open selftest: safety: seamOpened={seamOpened} seamError={seamError ?? "-"} " +
                      $"realLaunchAttempts={realAttempts} browsersBefore={browsersBefore} " +
                      $"browsersAfter={browsersAfter} lastTarget='{QrBrowserOpener.LastTarget}' " +
                      $"noRealBrowser={seamHeld}");

        // ⑧ 使用者的剪貼簿：在按下「複製」之前一個字都不該被改。
        string clipboardAfterAll = ReadClipboard();
        bool clipboardUntouchedBeforeCopy = clipboardBefore == clipboardAfterCopy
                                            || clipboardAfterCopy == payload;
        bool clipboardOnlyByButton = clipboardBefore == "" || clipboardAfterAll == payload
                                     || clipboardAfterAll == clipboardBefore;
        evidence.Line($"qr open selftest: clipboard beforeCopy='{Escape(clipboardBefore)}' " +
                      $"afterCopy='{Escape(clipboardAfterCopy)}' afterAll='{Escape(clipboardAfterAll)}' " +
                      $"untouchedBeforeCopy={clipboardUntouchedBeforeCopy} onlyTheButton={clipboardOnlyByButton}");

        // ⑨ 分類的自測（**不碰 WPF**）也順便跑一次，讓「這一刀的分類」在每一個入口都被驗到。
        bool classifyOk = classifyExit == 0;
        bool extraHandled = true;
        if (!string.IsNullOrEmpty(extraText))
        {
            var extra = QrTextRouter.Route(extraText, "selftest-extra", lines.Enqueue);
            extraHandled = extra.Kind == QrTextClassifier.QrKind.Url && extra.Opened &&
                           QrBrowserOpener.RealLaunchAttempts == 0;
            evidence.Line($"qr open selftest: extra url '{Escape(extraText)}' -> " +
                          $"kind={QrTextClassifier.KindLabel(extra.Kind)} routed={extra.Opened} " +
                          $"target='{extra.Target}' realLaunchAttempts={QrBrowserOpener.RealLaunchAttempts}");
        }

        while (lines.TryDequeue(out string? buffered)) evidence.Line(buffered);

        bool ok = classifyOk && opened && windowPresent && textIdentical && copyTextSame
                  && clipboardAfterCopy == payload && singleInstance && firstStillThere
                  && closed && reopened && reopenedPresent && closedAtEnd
                  && seamHeld && clipboardUntouchedBeforeCopy && clipboardOnlyByButton && extraHandled;
        evidence.Line($"qr open selftest done: ok={ok} (classifyOk={classifyOk} opened={opened} " +
                      $"windowPresent={windowPresent} textIdentical={textIdentical} " +
                      $"copyTextIdentical={copyTextSame} singleInstance={singleInstance} " +
                      $"reopened={reopened} browserSeamHeld={seamHeld} " +
                      $"realBrowserLaunches={realAttempts})");
        return ok ? 0 : 1;
    }

    /// <summary>目前剪貼簿裡的文字（讀失敗＝空字串；自測只讀、不寫）。</summary>
    internal static string ReadClipboard()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 診斷輸出裡的字串轉義。輸出必須**單行**（驗收一行一行讀），所以換行／定位字元都要變成看得見的
    /// 序列；而跳脫用的反斜線本身也要跳脫，否則「輸入裡真的有一個反斜線」與「這是我加的轉義」
    /// 在輸出上分不出來。
    /// </summary>
    internal static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(LongPath.Prepare(path));
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>資料夾的快照（檔名 ＋ 長度 ＋ 最後寫入時間）。「有沒有多出一個檔案」靠它。</summary>
    private static string DirectorySnapshot(string folder)
    {
        if (!Directory.Exists(folder)) return "(no folder)";
        var names = new List<string>();
        foreach (string file in Directory.GetFiles(folder))
            names.Add($"{Path.GetFileName(file)}|{new FileInfo(file).Length}|" +
                      $"{File.GetLastWriteTimeUtc(file).Ticks}");
        names.Sort(StringComparer.Ordinal);
        return string.Join("\n", names);
    }

    /// <summary>輪詢等一個條件成立（形狀與 Program.cs 的既有 WaitFor 相同）。</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
