using System.Reflection;

namespace Sidecar;

internal static class Program
{
    /// <summary>觸發鍵預設值 VK_SPACE（Q1：預設空白鍵、可設定）。此為唯一硬編碼處。</summary>
    public const uint DefaultTriggerKey = 0x20;

    /// <summary>
    /// 對外顯示的版本。用 <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>：
    /// <c>AssemblyVersion</c> 會被補成四段（<c>0.1.0</c> → <c>0.1.0.0</c>），不適合當顯示字串。
    /// </summary>
    public static string VersionText()
    {
        var asm = typeof(Program).Assembly;
        string? info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info)
            ? asm.GetName().Version?.ToString(3) ?? "0.0.0"
            : info;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Options opt = Options.Parse(args);

        if (opt.ShowUsage)
        {
            Native.AttachParentConsole();
            Console.WriteLine(Options.Usage);
            return 2;
        }

        if (opt.ShowVersion)
        {
            Native.AttachParentConsole();
            Console.WriteLine(VersionText());
            return 0;
        }

        // 單一實例守衛。A13 要求「被強制結束後能立刻重啟」，
        // 因此這裡不留下任何需要清理的檔案鎖。
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Sidecar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Native.AttachParentConsole();
            Console.Error.WriteLine("Sidecar is already running.");
            return 3;
        }

        return App.Run(opt);
    }
}

/// <summary>
/// 命令列介面。**先凍結**：驗收跑者（tools/verify-m1a.ps1）依賴這組旗標，
/// 後續任務只接線、不改名。
/// </summary>
internal sealed class Options
{
    public const string Usage =
        """
        Sidecar (M1a)
          --version                 print version and exit
          --dump-monitors           list monitors and exit (arrives in T2)
          --raise                   raise the hit layer without hooks (diagnostic)
          --raise-seconds <N>       auto-hide after N seconds in --raise mode (default 30)
          --log <path>              evidence log path
          --trigger-key <0xNN|NAME> trigger virtual-key (default 0x20 / VK_SPACE)
          --cycle-test <N>          show/hide the hit layer N times and exit
          --quiet                   do not echo events to the console
        """;

    public bool ShowUsage;
    public bool ShowVersion;
    public bool DumpMonitors;
    public bool ForceRaiseHitLayer;
    public string? LogPath;
    public uint TriggerKey = Program.DefaultTriggerKey;
    public int CycleTest;
    public bool Quiet;

    /// <summary>--raise 的自我收層秒數（診斷模式沒有釋放路徑，必須自己保證不會困住桌面）。</summary>
    public int RaiseSeconds = 30;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--help" or "-h" or "/?": o.ShowUsage = true; break;
                case "--version" or "-v": o.ShowVersion = true; break;
                case "--dump-monitors": o.DumpMonitors = true; break;
                case "--raise": o.ForceRaiseHitLayer = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--log":
                    if (++i < args.Length) o.LogPath = args[i];
                    break;
                case "--trigger-key":
                    if (++i < args.Length) o.TriggerKey = ParseVirtualKey(args[i]);
                    break;
                case "--cycle-test":
                    if (++i < args.Length && int.TryParse(args[i], out int n) && n > 0) o.CycleTest = n;
                    break;
                case "--raise-seconds":
                    if (++i < args.Length && int.TryParse(args[i], out int s) && s >= 0) o.RaiseSeconds = s;
                    break;
            }
        }
        return o;
    }

    /// <summary>接受 0xNN 十六進位或少量具名虛擬鍵；未知值一律退回預設並由呼叫端記錄。</summary>
    public static uint ParseVirtualKey(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
            return hex;

        return t.ToUpperInvariant() switch
        {
            "VK_SPACE" => 0x20,
            "VK_TAB" => 0x09,
            "VK_CAPITAL" or "VK_CAPSLOCK" => 0x14,
            "VK_ESCAPE" => 0x1B,
            "VK_RMENU" => 0xA5,
            "VK_F8" => 0x77,
            _ => Program.DefaultTriggerKey
        };
    }
}

/// <summary>組裝與訊息迴圈。業務邏輯一律不放在這裡（收層由 ReleaseGuard 擁有）。</summary>
internal static class App
{
    public static int Run(Options opt)
    {
        using var evidence = new Evidence(opt.LogPath ?? Evidence.DefaultPath(), opt.Quiet);
        evidence.Line($"Sidecar {Program.VersionText()} starting: trigger=VK 0x{opt.TriggerKey:X2} " +
                      $"dumpMonitors={opt.DumpMonitors} raise={opt.ForceRaiseHitLayer} " +
                      $"cycleTest={opt.CycleTest} log={evidence.Path}");

        try
        {
            return RunCore(opt, evidence);
        }
        catch (Exception ex)
        {
            // 大聲失敗：證據檔與 stderr 都要留痕，而且不得讓 WER 對話框卡住驗收跑者。
            evidence.Line("FATAL: " + ex.ToString().Replace("\r", string.Empty).Replace("\n", " | "));
            Native.AttachParentConsole();
            Console.Error.WriteLine("FATAL: " + ex.Message);
            return 9;
        }
    }

    private static int RunCore(Options opt, Evidence evidence)
    {
        MonitorEntry[] monitors = MonitorLayout.Enumerate();
        evidence.Line($"monitors: {monitors.Length}");

        if (opt.DumpMonitors)
        {
            Native.AttachParentConsole();
            foreach (MonitorEntry m in monitors)
            {
                string line =
                    $"monitor#{m.Index} {m.Device} bounds=({m.Bounds.Left},{m.Bounds.Top},{m.Width}x{m.Height}) " +
                    $"work=({m.Work.Left},{m.Work.Top},{m.Work.Right - m.Work.Left}x{m.Work.Bottom - m.Work.Top}) " +
                    $"primary={m.IsPrimary}";
                evidence.Line(line);
                Console.WriteLine(line);
            }
            return 0;
        }

        using var hit = new HitLayer(evidence, monitors, Native.OverlayAlpha);

        hit.FilesDropped += (index, paths) =>
        {
            evidence.Status($"DROP: {paths.Length} file(s) on hit#{index} — M1a records only, no file operation");
            // T4 會在此接上 guard.OnDrop()（立即收層）。本任務尚無收層權威，故只記錄。
        };

        hit.EnsureCreated();      // 預先建立、保持隱藏（規格 §9.3：升起 < 30 ms）

        if (opt.CycleTest > 0) return CycleTest(hit, evidence, opt.CycleTest);

        if (opt.ForceRaiseHitLayer)
        {
            evidence.Line($"--raise: raising hit layer without hooks; auto-hide in {opt.RaiseSeconds}s");
            hit.ShowAll();
            // 診斷模式沒有釋放路徑，因此這裡自己計時收層——診斷也絕不可以困住桌面。
            // T4 會以 ReleaseGuard 的 watchdog（保險 #3）取代這一段。
            var raiseGuard = new System.Timers.Timer(opt.RaiseSeconds * 1000.0) { AutoReset = false };
            raiseGuard.Elapsed += (_, _) =>
            {
                evidence.Line($"--raise auto-hide after {opt.RaiseSeconds}s");
                hit.RequestHideAll();
            };
            raiseGuard.Start();
        }

        evidence.Line("message loop starting (release guard arrives in T4)");
        return RunMessageLoop(evidence);
    }

    /// <summary>程式化收層循環：N 次升起／收起後以**作業系統狀態**斷言全部隱藏。</summary>
    private static int CycleTest(HitLayer hit, Evidence evidence, int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            hit.ShowAll();
            Thread.Sleep(30);
            hit.HideAll();
            Thread.Sleep(30);
        }
        Thread.Sleep(150);
        bool hidden = !hit.IsVisible;
        evidence.Line($"cycle test done: {cycles} cycles; allHidden={hidden} (must be True)");
        return hidden ? 0 : 1;
    }

    private static int RunMessageLoop(Evidence evidence)
    {
        while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        evidence.Line("message loop exited");
        return 0;
    }
}
