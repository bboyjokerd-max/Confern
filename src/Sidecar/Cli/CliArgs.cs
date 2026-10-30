namespace Sidecar;

/// <summary>
/// 命令列的表面層：把使用者打的句子翻譯成既有的長旗標。
///
/// <code>
/// confern convert a.png b.jpg --to webp --quality 80 -o C:\出圖
///   → --convert "a.png;b.jpg" --target webp --quality 80 --out-dir C:\出圖
/// confern formats
///   → --list-formats
/// confern watch C:\in --to JPG
///   → --watch C:\in --target JPG
/// confern watch --add C:\in --to PNG -o D:\out
///   → --watch-add C:\in --target PNG --out-dir D:\out
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
        if (!args[0].StartsWith("-", StringComparison.Ordinal) && !Is(args[0], "convert") && !Is(args[0], "watch")
            && !Is(args[0], "recent") && !Is(args[0], "preset"))
            return Error($"unknown command '{args[0]}' (try: confern formats, confern convert, confern watch, " +
                         "confern recent, confern preset, or confern --help)");

        bool isConvert = Is(args[0], "convert");
        // `watch` 是第二個位置參數型子命令（2026-10-22）：`confern watch <folder>` 直接監看，
        // 而 `watch --add`／`--remove`／`--list` 是管理設定檔裡的那份清單。
        bool isWatch = Is(args[0], "watch");
        // `recent` 是第三個子命令（2026-10-23）：印出／清空「最近轉檔」。
        // 它**沒有位置參數**——清單是程式自己寫的，使用者沒有東西要指定。
        bool isRecent = Is(args[0], "recent");
        // `preset` 是第四個子命令（2026-10-23）：「每種來源格式預設轉成什麼」。
        // 形狀是 `--set PNG=WEBP` 這種「旗標＋一個值」，所以只有 `--set/--remove` 要吃值。
        bool isPreset = Is(args[0], "preset");
        string? watchFolder = null;
        // recent 沒有位置參數，但「有沒有多打東西」還是要看得出來，所以裸字自己收一份。
        // ⚠ 不能用「事後掃 rest 裡的裸字」代替：`recent --limit 5` 的那個 `5` 就長得像裸字，
        // 掃下去會變成「你多打了參數」而 exit 4（實測抓到）。
        var recentExtra = new List<string>();
        // 同理：`preset --set PNG=WEBP` 的那個值不是裸字，它只是「不像旗標」。收在同一條規則裡。
        var presetExtra = new List<string>();

        var rest = new List<string>();
        var files = new List<string>();
        bool stopOptions = false;

        // 子命令自己（`convert`／`watch`）要跳過，它是動詞不是參數。第一版的 watch 忘了這件事，
        // 於是 `confern watch --list` 被讀成「監看一個叫 watch 的資料夾」（實測 exit 4）。
        for (int i = (isConvert || isWatch || isRecent || isPreset) ? 1 : 0; i < args.Length; i++)
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

            // `watch --add` / `--remove` / `--list` 是 watch 的短寫法，翻成長旗標。
            // ⚠ 第一版漏了這段：`watch --add C:\dir` 的 `--add` 被當成普通旗標丟進 rest、
            // 而 `C:\dir` 變成位置參數 ⇒ **它其實開了一個前台監看**，不是「加進設定檔」。
            // 使用者打的是「記下來」，跑出來的卻是「現在就盯著」——這種錯最難發現，因為兩者都活著。
            // `recent --list`／`--clear`／`--limit N`：翻成長旗標。`--list` 是預設行為，
            // 但明打一次也要接受（「我打了卻沒反應」是最容易讓人以為壞掉的體驗）。
            // `preset --set PNG=WEBP`／`--remove PNG`／`--clear`／`--list`：翻成長旗標。
            // `--set` 的值含 `=` 與 `:`（`HEIC=JPG:82`），但它仍然是一個 token——shell 不會拆它。
            if (!stopOptions && isPreset)
            {
                if (a == "--list") { rest.Add("--preset-list"); continue; }
                if (a == "--clear") { rest.Add("--preset-clear"); continue; }
                if (a == "--set" || a == "--remove")
                {
                    if (i + 1 >= args.Length)
                        return Error(a == "--set" ? "--set needs SRC=TGT, e.g. --set PNG=WEBP"
                                                  : "--remove needs a source format, e.g. --remove PNG");
                    rest.Add(a == "--set" ? "--preset-set" : "--preset-remove");
                    rest.Add(args[++i]);
                    continue;
                }
            }

            if (!stopOptions && isRecent)
            {
                if (a == "--list") { rest.Add("--recent-list"); continue; }
                if (a == "--clear") { rest.Add("--recent-clear"); continue; }
                if (a == "--limit")
                {
                    if (i + 1 >= args.Length) return Error("--limit needs a number, e.g. --limit 5");
                    rest.Add("--recent-limit");
                    rest.Add(args[++i]);
                    continue;
                }
            }

            if (!stopOptions && isWatch && (a == "--add" || a == "--remove" || a == "--list"))
            {
                if (a == "--list") { rest.Add("--watch-list"); continue; }
                if (i + 1 >= args.Length) return Error($"{a} needs a folder path");
                rest.Add(a == "--add" ? "--watch-add" : "--watch-remove");
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

            if (isRecent && (stopOptions || !a.StartsWith("-", StringComparison.Ordinal)))
            {
                recentExtra.Add(a);
                continue;
            }

            if (isPreset && (stopOptions || !a.StartsWith("-", StringComparison.Ordinal)))
            {
                presetExtra.Add(a);
                continue;
            }

            if (isWatch && (stopOptions || !a.StartsWith("-", StringComparison.Ordinal)))
            {
                // 只收一個資料夾：`confern watch a b` 看起來像「監看兩個」但我們的旗標只有一個位置，
                // 与其猜，不如說清楚。
                if (watchFolder != null)
                    return Error($"watch takes ONE folder (got '{watchFolder}' and '{a}'); " +
                                 "run it once per folder");
                watchFolder = a;
                continue;
            }

            rest.Add(a);
        }

        if (isWatch)
        {
            bool add = rest.Contains("--watch-add");
            bool remove = rest.Contains("--watch-remove");
            bool list = rest.Contains("--watch-list");
            int modes = (add ? 1 : 0) + (remove ? 1 : 0) + (list ? 1 : 0);
            if (modes > 1) return Error("watch --add / --remove / --list are alternatives; pick one");
            if (list && watchFolder != null)
                return Error("watch --list takes no folder (it lists every watched folder)");
            if ((add || remove || list) && watchFolder != null)
                return Error($"watch --{(add ? "add" : remove ? "remove" : "list")} takes its folder from the flag, " +
                             $"not as a bare word (got '{watchFolder}')");
            if (modes == 0 && watchFolder == null)
                return Error("watch needs a folder: confern watch <folder> --to <FORMAT>, " +
                             "or confern watch --list");

            var w = new List<string>();
            if (modes == 0) { w.Add("--watch"); w.Add(watchFolder!); }
            w.AddRange(rest);
            w.Add("--cli-subcommand");
            return w.ToArray();
        }

        if (isRecent)
        {
            // 位置參數一律拒絕：`confern recent foo` 看起來像「查某個檔」，但這個清單沒有查詢介面。
            // 与其猜，不如說清楚（與 watch 的「只收一個資料夾」同一條規矩）。
            if (recentExtra.Count > 0)
                return Error($"recent takes no arguments (got '{string.Join(" ", recentExtra)}'); " +
                             "try: confern recent, confern recent --limit 5, or confern recent --clear");
            // ⚠ 不是 `rest.Count == 0`：`--log`／`--quality` 這些**共用**旗標也在 rest 裡，
            //   用它判斷會讓 `confern recent --log x.log` 落到常駐那條路（＝開出托盤）。
            //   判斷的是「模式旗標有沒有出現」。
            if (!rest.Contains("--recent-list") && !rest.Contains("--recent-clear") &&
                !rest.Contains("--recent-limit"))
                rest.Insert(0, "--recent-list");
            rest.Add("--cli-subcommand");
            return rest.ToArray();
        }

        if (isPreset)
        {
            // 位置參數一律拒絕：`confern preset PNG=WEBP` 看起來像「直接設定」，但那要 `--set`。
            // 與 `recent`／`watch` 同一條規矩：与其猜，不如說清楚。
            if (presetExtra.Count > 0)
                return Error($"preset takes no bare words (got '{string.Join(" ", presetExtra)}'); " +
                             "try: confern preset --set PNG=WEBP");
            // 同上：`confern preset --log x.log` 也必須印清單，而不是開一個托盤。
            if (!rest.Contains("--preset-list") && !rest.Contains("--preset-clear") &&
                !rest.Contains("--preset-set") && !rest.Contains("--preset-remove"))
                rest.Insert(0, "--preset-list");
            rest.Add("--cli-subcommand");
            return rest.ToArray();
        }

        if (!isConvert) return rest.ToArray();

        if (files.Count == 0)
            return Error("convert needs at least one file: confern convert <file> [<file> ...] --to <FORMAT>");

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
        "--preset-remove",
        "--preset-set",
        "--recent-limit",
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
        "--watch",
        "--watch-add",
        "--watch-duration-ms",
        "--watch-remove",
        "--watch-settle-ms",
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
