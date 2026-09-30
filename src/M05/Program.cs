namespace M05;

internal static class Program
{
    /// <summary>硬保險 #3：接收層可見時間上限。</summary>
    private const int OverlayTimeoutMs = 30_000;

    /// <summary>
    /// 收層寬限（實作時發現的必要修正）：
    /// 低階滑鼠 hook 會在 shell 的拖放迴圈「之前」看到 mouse-up。
    /// 若在 hook 內立即收層，放手當下的 drop 會落到桌面底下而不是我們身上。
    /// 因此 mouse-up／放開觸發鍵一律延遲 250 ms 收層；
    /// 真正的 drop（WM_DROPFILES）會立刻收，這條只是保底。
    /// </summary>
    private const int HideGraceMs = 250;

    [STAThread]
    private static int Main(string[] args)
    {
        string crashPath = Path.Combine(Path.GetTempPath(), "m05", "crash.log");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
                File.AppendAllText(crashPath, $"[{DateTime.Now:HH:mm:ss.fff}] UNHANDLED {e.ExceptionObject}\n");
            }
            catch { /* 診斷失敗不得再拋 */ }
        };

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
                File.AppendAllText(crashPath, $"[{DateTime.Now:HH:mm:ss.fff}] MAIN {ex}\n");
            }
            catch { /* 同上 */ }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        bool force = args.Contains("--force-overlay");
        byte alpha = Native.OverlayAlpha;
        int alphaIdx = Array.IndexOf(args, "--alpha");
        if (alphaIdx >= 0 && alphaIdx + 1 < args.Length && byte.TryParse(args[alphaIdx + 1], out byte a) && a >= 1)
            alpha = a;

        using var evidence = new Evidence(Path.Combine(Path.GetTempPath(), "m05", "m05.log"), quiet);
        evidence.Line("step: evidence ready");

        // 收層可靠性壓力測試（矩陣 #5）：連續 show/hide N 次，最後確認視窗確實不可見。
        int cycleIdx = Array.IndexOf(args, "--cycle-test");
        if (cycleIdx >= 0 && cycleIdx + 1 < args.Length && int.TryParse(args[cycleIdx + 1], out int cycles))
        {
            using var cyc = new Overlay(evidence, alpha);
            cyc.EnsureCreated();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= cycles; i++)
            {
                cyc.Show();
                Thread.Sleep(20);
                cyc.Hide();
            }
            sw.Stop();
            evidence.Line($"cycle test done: {cycles} cycles in {sw.ElapsedMilliseconds} ms; " +
                          $"lastVisible={cyc.IsVisible} (must be False)");
            Thread.Sleep(150);
            return cyc.IsVisible ? 1 : 0;
        }

        using var overlay = new Overlay(evidence, alpha);
        using var hooks = new Hooks();
        var detector = new DragDetector(evidence);

        // 先宣告，事件處理常式會捕捉它
        using var watchdog = new System.Timers.Timer(OverlayTimeoutMs) { AutoReset = false };
        using var hideGrace = new System.Timers.Timer(HideGraceMs) { AutoReset = false };

        watchdog.Elapsed += (_, _) =>
        {
            if (!overlay.IsVisible) return;
            evidence.Line($"WATCHDOG: overlay visible > {OverlayTimeoutMs} ms -> force hide");
            overlay.RequestHide();
        };

        hideGrace.Elapsed += (_, _) =>
        {
            if (!overlay.IsVisible) return;
            evidence.Line("grace elapsed -> hide");
            overlay.RequestHide();
        };

        overlay.FilesDropped += paths => evidence.Status($"DROP OK: {paths.Length}");

        detector.Captured += () =>
        {
            overlay.Show();
            hideGrace.Stop();
            watchdog.Stop();
            watchdog.Start();
        };

        detector.ReleaseRequested += reason =>
        {
            evidence.Line($"release requested: {reason}");
            watchdog.Stop();
            hideGrace.Stop();
            hideGrace.Start();          // 寬限後收層（讓 drop 有機會先落到我們身上）
        };

        hooks.MouseEvent += detector.OnMouse;
        hooks.KeyEvent += detector.OnKey;

        if (force)
        {
            evidence.Line($"mode: --force-overlay (hooks NOT installed) alpha={alpha}");
            overlay.Show();
            watchdog.Start();
        }
        else
        {
            evidence.Line($"mode: normal alpha={alpha}");
            hooks.Install(evidence);
        }

        int exit = RunMessageLoop(evidence);
        evidence.Line("exit=" + exit);
        return exit;
    }

    private static int RunMessageLoop(Evidence evidence)
    {
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        evidence.Line("message loop exited");
        return 0;
    }
}
