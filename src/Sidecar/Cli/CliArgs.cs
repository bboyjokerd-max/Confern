namespace Sidecar;

/// <summary>
/// 命令列的表面層：把使用者打的句子翻譯成既有的長旗標。
///
/// <code>
/// conforn convert a.png b.jpg --to webp --quality 80 -o C:\出圖
///   → --convert "a.png;b.jpg" --target webp --quality 80 --out-dir C:\出圖
/// conforn formats
///   → --list-formats
/// </code>
///
/// **為什麼要這一層，而不是在 <see cref="Options.Parse"/> 裡多加幾個 case**：
/// ① 本產品已經有八十幾個長旗標，全部是「旗標 + 值」的形狀；`convert &lt;files&gt;` 是
///    **位置參數**，而位置參數與「值也是裸字」的既有解析衝突，散在 parse 迴圈裡看不出邊界；
/// ② 這一層是純字串處理：不建視窗、不碰檔案、不轉檔，所以可以獨立驗證
///    （`tools/verify-cli.ps1` 的 S1 群完全不需要真檔）；
/// ③ 翻譯完只剩一條路徑進產品，不會出現「新寫法與舊寫法行為不同」這種要維護兩份的狀態。
///
/// **不做的事**（刻意的）：不展開萬用字元（PowerShell／cmd 自己會展開）、不做 shell 引用
/// 解析（那是 shell 的工作）、不支援 `--flag=value` 形式（既有旗標全部是空白分隔，
/// 混兩種形式等於多一條沒人驗的分支）。
/// </summary>
internal static class CliArgs
{
    /// <summary>
    /// 翻譯 `args`。回傳的陣列一律可以交給 <see cref="Options.Parse"/>；
    /// 用法錯誤時回傳單一個 `--cli-error &lt;訊息&gt;`，由 <see cref="Program.Main"/> 印出並以 4 結束
    /// （4 與 `--settings-set` 的「你要求的東西不合法」一致，不是 2 的「你要看說明」）。
    /// </summary>
    public static string[] Normalize(string[] args)
    {
        if (args.Length == 0) return args;

        // ① 子命令：formats／list-formats（唯讀，不帶參數）
        if (Is(args[0], "formats") || Is(args[0], "list-formats"))
        {
            if (args.Length > 1)
                return Error($"'{args[0]}' takes no arguments (got {args.Length - 1} more)");
            return new[] { "--list-formats", "--cli-subcommand" };
        }

        // ② 第一個字是裸字卻不是子命令 → 不要默默變成「啟動常駐程式」。
        //    這條是刻意加的安全網：以前 `confern typo` 會安靜地開一個托盤圖示。
        if (!args[0].StartsWith("-", StringComparison.Ordinal) && !Is(args[0], "convert"))
            return Error($"unknown command '{args[0]}' (try: conforn formats, or conforn --help)");

        bool isConvert = Is(args[0], "convert");

        var rest = new List<string>();
        var files = new List<string>();
        bool stopOptions = false;

        for (int i = isConvert ? 1 : 0; i < args.Length; i++)
        {
            string a = args[i];

            // `--` 之後一律當檔案：路徑真的以 - 開頭時的唯一寫法。
            if (!stopOptions && a == "--") { stopOptions = true; continue; }

            if (!stopOptions && (a == "-o" || a == "--out-dir"))
            {
                if (i + 1 >= args.Length) return Error($"{a} needs a folder path");
                rest.Add("--out-dir");
                rest.Add(args[++i]);
                continue;
            }

            if (!stopOptions && a == "--to")
            {
                if (i + 1 >= args.Length) return Error("--to needs a format, e.g. --to webp");
                rest.Add("--target");
                rest.Add(args[++i]);
                continue;
            }

            // 「吃值的旗標」要把下一個字一起收進來，否則那個值會被誤認成檔案。
            // 這正是 2026-10-22 實測抓到的缺陷：`--quality 20` 的 `20` 變成了輸入檔，
            // 於是每一次「不同品質」的轉檔其實都是預設 85（位元組一字不差、日誌裡有
            // `Fail source=20`）。這張表是**唯一**一份，驗收套件會拿它跟 Options.Parse
            // 的實際行為對照（見 tools/verify-cli.ps1 的 S2 群），漂移會被抓到。
            if (!stopOptions && a.StartsWith("--", StringComparison.Ordinal) && ValueFlags.Contains(a))
            {
                if (i + 1 >= args.Length) return Error($"{a} needs a value");
                rest.Add(a);
                rest.Add(args[++i]);
                continue;
            }

            if (isConvert && (stopOptions || !a.StartsWith("-", StringComparison.Ordinal)))
            {
                files.Add(a);
                continue;
            }

            rest.Add(a);
        }

        if (!isConvert) return rest.ToArray();

        if (files.Count == 0)
            return Error("convert needs at least one file: conforn convert <file> [<file> ...] --to <FORMAT>");

        var result = new List<string> { "--convert", string.Join(";", files) };
        result.AddRange(rest);
        // 標記「這是子命令寫法」。用途只有一個：把日誌行從 stderr 收到日誌檔裡，
        // 讓 `confern convert ... | tee` 拿到乾淨的結果。舊的 `--convert` 旗標寫法
        // **行為完全不變**（已有多支驗收套件依賴那條路徑的既有輸出）。
        result.Add("--cli-subcommand");
        return result.ToArray();
    }

    private static bool Is(string value, string word) =>
        string.Equals(value, word, StringComparison.OrdinalIgnoreCase);

    private static string[] Error(string message) => new[] { "--cli-error", message };

    /// <summary>
    /// 「後面一定跟著一個值」的旗標（＝<see cref="Options.Parse"/> 裡會往下讀一個參數的那些
    /// case；解析器有兩種寫法，<c>args[i + 1]</c> 與 <c>if (++i &lt; args.Length)</c>，兩者都算）。
    /// `-o`／`--to` 是別名，在上面的迴圈直接處理，不在這張表裡。
    ///
    /// <para>這張表**必須**跟解析器一致。驗收套件（tools/verify-cli.ps1 的 S8）會掃描
    /// Program.cs 的解析 switch、把「有往下讀一個參數」的旗標抽出來跟這張表比對，
    /// 所以新增一個吃值的旗標卻忘了登記，會是一條紅燈而不是一個安靜的錯誤——
    /// 這個缺陷正是這樣被抓到的（`--quality 20` 的 `20` 被當成輸入檔，
    /// 於是每次「不同品質」其實都是預設 85）。</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ValueFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--autostart-ack",
        "--autostart-ack-timeout-ms",
        "--autostart-probe",
        "--cli-error",
        "--codec-probe",
        "--convert",
        "--crop",
        "--cycle-test",
        "--log",
        "--out-dir",
        "--pill-interactive",
        "--pill-selftest-dump",
        "--pill-selftest-dump-json",
        "--pill-selftest-dump-raw",
        "--pill-selftest-shot",
        "--progress-dump",
        "--progress-dump-detail",
        "--progress-dump-detail-json",
        "--progress-dump-detail-raw",
        "--quality",
        "--settings-file",
        "--settings-probe",
        "--settings-set",
        "--settings-ui-dump",
        "--settings-ui-dump-after-palette",
        "--settings-ui-dump-popup",
        "--settings-ui-popup-box",
        "--target",
        "--tray-exit-after",
        "--trigger-key",
        "--watchdog-seconds",
        "--wheel-backdrop",
        "--wheel-demo",
        "--wheel-payload",
        "--wheel-previews",
        "--wheel-selected",
        "--wheel-simulate-cycles",
        "--wheel-targets",
        "--worker",
    };
}
