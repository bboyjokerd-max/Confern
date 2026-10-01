using System.Diagnostics;

namespace GlassSpike;

/// <summary>
/// 玻璃輪盤的**可行性 spike**：把「抓螢幕 → 自製模糊 → 合成玻璃輪盤」三步的真實成本量出來。
///
/// 為什麼要有這個 spike（設計背景見 docs/aegis/plans/2026-10-01-ui-redesign.md §4）：
/// DWM 的亞克力／Mica 只對「一般視窗」生效，而正式輪盤是 per-pixel alpha 的**分層視窗**
/// （WS_EX_LAYERED + UpdateLayeredWindow，畫面由我們自己合成），系統材質沒有東西可畫。
/// 所以毛玻璃只剩一條路：自己 BitBlt 抓底下的螢幕、自己模糊、自己疊。
/// 這個 spike 就是要在改正式程式碼**之前**回答「這條路的時間與記憶體代價是多少」。
///
/// 預設行為（**不彈任何視窗、不動使用者畫面**）：
///   1. 抓游標附近 330x330（clamp 進虛擬螢幕）→ 2. 可分離 box blur 3 pass → 3. 合成 220 px 玻璃輪盤
///   4. 存 docs/design/reference/spike-glass.png 與 spike-glass-noblur.png
///   5. 以上 1~3 步各跑 50 次，印出平均／最大
///
/// 旗標：
///   （無）        量測 + 出圖（預設）
///   --dump-only  只出圖不量測
///   --show       額外開一個看得到的視窗（**預設不執行**）
///   --diagnose   額外的診斷段：擷取面積 vs 耗時、各擷取路徑對照、更新週期同步假說驗證
/// </summary>
internal static class Program
{
    // ---- 量測參數 ----------------------------------------------------------
    /// <summary>取樣次數（題目要求 50）。</summary>
    private const int Iterations = 50;
    /// <summary>不列入統計的暖機次數（JIT、GDI+ 首次初始化都算在這裡）。</summary>
    private const int Warmup = 5;

    // ---- 幾何參數（會隨 --scale 一起放大：1× ＝ 真實尺寸，3× ＝ 三倍解析度輸出）--------
    /// <summary>
    /// 高解析度倍率（--scale=N）。
    ///
    /// 這不是「把圖放大」——是**用三倍大的幾何與三倍大的抓圖區重畫一次**，
    /// 所以 990px 的圖是真的有那麼多細節（等同 3× 截圖），不是補點。
    /// 1× 的輸出才是「螢幕上實際看到的樣子」。
    /// </summary>
    private static int Scale = 1;
    /// <summary>抓取的方形邊長（1× 時為題目指定的 330）。⚠ 必須在 Precompute() 之前定案。</summary>
    private static int CaptureSize = 330;
    /// <summary>輪盤直徑（與正式輪盤同尺寸：WheelGeometry.DiameterPx = 220）。</summary>
    private static int WheelDiameter = 220;
    /// <summary>中心鏤空半徑（1× 預設 48.4；D 設計模式改用互動原型拍板的 RI=47）。</summary>
    private static double CenterCutoutRadius = 48.4;
    /// <summary>box blur 半徑（題目建議 8–12）。--blur=NN 可覆寫；D 用 17（σ≈18，對應設計稿的 blur 18px）。</summary>
    private static int BlurRadius = 10;
    /// <summary>box blur 次數（3 次 ≈ 高斯，題目建議 2–3）。</summary>
    private const int BlurPasses = 3;

    /// <summary>
    /// 深色薄罩（黑幕）強度。題目規格是 55–65%；**預設值刻意偏離規格**，理由見下方 CREAM 的註解。
    /// 可用 --veil=NN 覆寫（NN 為百分比）。
    /// </summary>
    private static double DarkVeilAlpha = 0.22;
    /// <summary>
    /// 奶油扇區的不透明度。可用 --cream=NN 覆寫（NN 為百分比）。
    ///
    /// 這三個數字（veil=22、cream=52、glow 不內溢）**不是照題目規格填的**，是實測＋視覺模型
    /// 對照 docs/design/reference/wheel-format-stable.jpg 掃出來的：
    ///   - 規格的 veil=60% 配上 cream=84%，數學上必然產生「深褐色煙燻圓盤」而不是奶油毛玻璃
    ///     （奶油色會被 60% 黑幕壓成 #31363F 這種暗藍灰）。實測 4 組後視覺模型對 60% 的評語是
    ///     「dark opaque disc with no glass character at all」。
    ///   - 扇區不透明度掃 80/65/52/40%：80% 桌布完全不透（像貼紙）、40% 奶油色被洗掉，
    ///     **52% 是唯一同時「透出桌布」又「保住奶油色」的點**。
    ///   - 光暈原本會洗進整個盤面（45% 暖橘），把奶油扇區染成 #31363F；改成只留在圓盤外緣後才正常。
    /// 也就是說：**規格的 veil 55–65% 與「看起來像奶油毛玻璃」這兩個要求在數學上互相衝突**，
    /// 這一項是本次 spike 最重要的設計發現。
    /// </summary>
    private static double CreamAlpha = 0.52;
    /// <summary>柔光暈是否要只留在輪盤外緣（true＝不洗到盤面內部）。可用 --glow-disc=1 關閉內溢。</summary>
    private static bool GlowInsideDisc = true;
    /// <summary>
    /// 扇區要不要保留半透明（true＝桌布會淡淡透進扇區，比較像玻璃）。
    /// 視覺模型對照 macOS 原圖後的建議：扇區本身也該是半透明奶油，不能是不透明貼紙。
    /// 可用 --sector-translucent=0 關掉。
    /// </summary>
    private static bool SectorTranslucent = true;

    /// <summary>投影外擴的半徑（px）。</summary>
    private static double ShadowRadius = 26.0;
    /// <summary>投影在圓盤邊緣的最大強度（0–255）。</summary>
    private static double ShadowMax = 110.0;

    /// <summary>扇區數（預設 3 個示意；--sectors=N 可切 2–4，正式是 2–4 個）。</summary>
    private static int SectorCount = 3;

    /// <summary>扇區標籤（設計模式會被 PrepareResources 換成原型的格式表）。</summary>
    private static string[] SectorLabels = { "PNG", "JPG", "BMP", "TIF" };

    // ---- 調色盤（--palette=cream|d）----------------------------------------
    // ⚠ cream 的每一個值都是**第一輪 spike（REPORT.md）的基準，不得改動**，否則那份報告不可重現。
    //   d ＝ 2026-10-01 拍板的「冷鋼薄荷・對比加強」（計畫檔 §1），由 ApplyPalette 覆寫。
    private static string PaletteName = "cream";

    /// <summary>扇區填色（偶數格／奇數格交替）。</summary>
    private static (byte R, byte G, byte B) SectorFill = (240, 226, 200);     // cream
    private static (byte R, byte G, byte B) SectorFillAlt = (222, 205, 178);
    /// <summary>奇數格的不透明度（null ＝ 與 CreamAlpha 相同）。</summary>
    private static double? SectorAlphaAlt = null;
    /// <summary>扇區文字色。</summary>
    private static (byte R, byte G, byte B) Ink = (46, 38, 30);
    /// <summary>扇區之間的缺口（單邊度數；總缺口＝2×）。</summary>
    private static double SectorHalfGapDeg = 2.5;
    /// <summary>要不要畫扇區分隔線（D 用「缺口」當分隔，不畫線）。</summary>
    private static bool DrawSectorSeparators = true;
    /// <summary>外緣髮絲線。</summary>
    private static (byte R, byte G, byte B) RimColor = (255, 244, 226);
    private static double RimAlpha = 0.55;
    /// <summary>外緣內側第二圈細線。</summary>
    private static (byte R, byte G, byte B) InnerHairColor = (255, 255, 255);
    private static double InnerHairAlpha = 0.20;
    /// <summary>中心鏤空的邊緣線。</summary>
    private static (byte R, byte G, byte B) CutStrokeColor = (255, 246, 230);
    private static double CutStrokeAlpha = 0.50;
    /// <summary>扇區分隔線。</summary>
    private static (byte R, byte G, byte B) SepColor = (92, 72, 52);
    private static double SepAlpha = 0.38;
    /// <summary>柔光暈的顏色（烘表與合成兩處共用）。</summary>
    private static (byte R, byte G, byte B) GlowColor = (255, 190, 140);
    /// <summary>薄罩的顏色（D 是 #080B0F，不是純黑）。</summary>
    private static (byte R, byte G, byte B) VeilColor = (0, 0, 0);

    // ---- 設計模式（--design=full）：把互動原型拍板的完整外觀畫出來 -------------------
    /// <summary>畫出完整設計（中心來源縮圖＋白膠囊、選中扇區、鍵帽提示、未選文字變暗）。</summary>
    private static bool DesignMode = false;
    /// <summary>被選中的扇區索引（--select=N，-1 ＝ 沒有選中）。</summary>
    private static int SelectedSector = -1;
    /// <summary>選中扇區的填色／描邊／描邊寬／外擴（D：`#3F6B7A` / `#5AF2D6` / 2px / +4px）。</summary>
    private static (byte R, byte G, byte B) SelFillColor = (255, 255, 255);
    private static (byte R, byte G, byte B) SelStrokeColor = (255, 255, 255);
    private static double SelStrokeWidth = 2.0;
    private static double SelExpand = 4.0;
    /// <summary>未選中扇區文字的透明度（D：0.42，讓選中的那一格跳出來）。</summary>
    private static double LabelDimOpacity = 1.0;
    /// <summary>柔暈整體強度倍率（淺色桌布要更強的暈才看得見；由桌布亮度自動決定）。</summary>
    private static double GlowAlphaMul = 1.0;
    /// <summary>中心膠囊（白底深字，內容＝來源 → 目標）。</summary>
    private static (byte R, byte G, byte B) CapBg = (255, 255, 255);
    private static (byte R, byte G, byte B) CapFg = (10, 15, 20);
    private static string CapText = "PNG → JPG";
    /// <summary>膠囊底色的不透明度（使用者要求「加一些透明度」：0.85）。</summary>
    private static int CapAlpha = 216;

    public static int Main(string[] args)
    {
        bool show = Has(args, "--show");
        bool dumpOnly = Has(args, "--dump-only");
        bool diagnose = Has(args, "--diagnose");
        bool demoBg = Has(args, "--demo-bg");
        bool preview = Has(args, "--preview");

        // 調色盤／幾何／背景（預設值＝第一輪 spike 的行為，一個字都沒變）
        string palette = StrArg(args, "--palette", "cream");
        string backdrop = StrArg(args, "--backdrop", "screen");
        bool design = StrArg(args, "--design", "") == "full";
        SectorCount = (int)NumArg(args, "--sectors", SectorCount);
        BlurRadius = (int)NumArg(args, "--blur", BlurRadius);
        DesignMode = design;
        SelectedSector = design ? (int)NumArg(args, "--select", 0) : -1;

        // 調色盤先上、數值覆寫後上——順序不能顛倒，否則 --veil=NN 這種**明確的**覆寫
        // 會被 ApplyPalette 的調色盤預設值蓋掉（第一版就是這樣，害 --veil=0 與 --veil=80
        // 跑出位元完全相同的圖，白白多查了一輪）。
        ApplyPalette(palette);
        // 選中描邊換色（--selcolor=RRGGBB）。放在 ApplyPalette 之後，才蓋得掉調色盤預設值。
        if (DesignMode) SelStrokeColor = HexArg(args, "--selcolor", SelStrokeColor);

        // 參數掃描用（只影響幾何烘表；不改預設值）
        DarkVeilAlpha = NumArg(args, "--veil", DarkVeilAlpha * 100.0) / 100.0;
        CreamAlpha = NumArg(args, "--cream", CreamAlpha * 100.0) / 100.0;
        GlowInsideDisc = NumArg(args, "--glow-inside", 1) != 0;
        SectorTranslucent = NumArg(args, "--sector-translucent", 1) != 0;

        // 高解析度：**所有幾何一起放大**，所以放大後的圖是真的三倍細節（等同 3× 截圖）。
        // 一定要在 ApplyPalette 之後做（它會決定 D 的孔徑），也一定要在 Precompute 之前做。
        Scale = Math.Max(1, (int)NumArg(args, "--scale", 1));
        if (Scale > 1)
        {
            CaptureSize *= Scale;
            WheelDiameter *= Scale;
            CenterCutoutRadius *= Scale;
            BlurRadius *= Scale;
            ShadowRadius *= Scale;
        }
        PrepareResources();

        string repoRoot = FindRepoRoot();
        string outDir = Path.Combine(repoRoot, "docs", "design", "reference");
        Directory.CreateDirectory(outDir);
        string blurredPath = Path.Combine(outDir, "spike-glass.png");
        string noblurPath = Path.Combine(outDir, "spike-glass-noblur.png");

        Console.WriteLine("=== GlassSpike : Tangerine Windows 玻璃輪盤可行性量測 ===");
        Console.WriteLine($"capture   : {CaptureSize}x{CaptureSize} square from screen");
        Console.WriteLine($"blur      : separable box blur, radius={BlurRadius} px, passes={BlurPasses}");
        Console.WriteLine($"wheel     : diameter={WheelDiameter} px, dark veil={DarkVeilAlpha:P0}, sectors={SectorCount}");
        Console.WriteLine($"palette   : {PaletteName}" + (backdrop != "screen" ? $", synthetic backdrop={backdrop}" : "")
                          + (DesignMode ? $", design=full (selected sector {SelectedSector}, \"{CapText}\")" : "")
                          + (Scale > 1 ? $", scale={Scale}x -> {CaptureSize}x{CaptureSize} output" : ""));
        Console.WriteLine($"iterations: {Iterations} (plus {Warmup} warmup, not counted)");
        Console.WriteLine();

        if (dumpOnly || preview)
        {
            (int px, int py, int dw, int dh) = demoBg ? PickContentRichOrigin() : PickCaptureOrigin();
            Console.WriteLine($"capture origin = ({px},{py})  screen = {dw}x{dh}");
            using var capOnly = new ScreenCapture(px, py, CaptureSize);

            // --backdrop=dark|light：用合成桌布取代真實桌面，後面的模糊＋合成路徑完全不變。
            // 目的＝計畫檔 §4「深色桌布與淺色桌布各測一次」，而且不必去改使用者的真實桌布。
            bool synthetic = backdrop is "dark" or "light";
            if (synthetic || DesignMode || Scale > 1)
            {
                string selTag = StrArg(args, "--selcolor", "");
                string suffix = backdrop + (DesignMode ? "-design" : "") + (Scale > 1 ? $"-{Scale}x" : "")
                                + (selTag.Length == 6 ? $"-sel{selTag.ToLowerInvariant()}" : "");
                blurredPath = Path.Combine(outDir, $"spike-glass-{PaletteName}-{suffix}.png");
                noblurPath = Path.Combine(outDir, $"spike-glass-{PaletteName}-{suffix}-noblur.png");
            }

            capOnly.Grab();                              // 任何模式下都真的走一次 BitBlt
            byte[] rawBg = capOnly.SnapshotRaw();
            if (synthetic)
            {
                rawBg = BuildBackdrop(backdrop);
                capOnly.InjectBackdrop(rawBg);
                Console.WriteLine($"[backdrop] synthetic '{backdrop}' wallpaper injected (real screen NOT touched)");
            }
            // 光暈策略依**桌布亮度自動決定**（不是靠旗標）。
            // 原因：正式程式抓的是使用者當下的桌面，不會有人告訴它「這是淺色桌布」。
            // 淺色桌布上的薄荷亮暈等於隱形（實測：外圈與桌布幾乎同亮度），
            // 所以改用「深青暈＋更重的投影」——在淺色底上讀起來是輪盤浮起來的環境陰影。
            double meanLuma = MeanLuma(rawBg);
            if (meanLuma > 140.0)
            {
                GlowColor = (46, 120, 110);
                GlowAlphaMul = 1.8;     // 2.6 太重建議過：淺色底看起來像一塊灰髒污
                ShadowMax = 130.0;
                Console.WriteLine($"[glow] backdrop mean luma={meanLuma:0} -> 淺色桌布：改用深青暈（×2.6）＋較重投影");
            }
            else
            {
                Console.WriteLine($"[glow] backdrop mean luma={meanLuma:0} -> 深色桌布：維持薄荷亮暈");
            }

            byte[] bg = RunPipeline(capOnly, skipGrab: true);            SaveFrames(px, py, bg, rawBg, blurredPath, noblurPath);

            if (preview)
            {
                // 額外出兩張預覽。
                // 注意 spike-glass.png 本身就已經是「疊在真實桌布上」的版本，所以這裡不再重複產一張；
                // 真正有額外資訊的是棋盤格版——它讓「輪盤邊緣與中心是真的透明」在 PNG 裡看得出來
                // （直接存 ARGB 的話多數檢視器會把透明區畫成黑或白，完全看不出效果）。
                string onCheck = Path.Combine(outDir, "spike-glass-alpha-checker.png");
                var co2 = Precompute();
                using (var f = ComposeSingle(bg, co2))
                    SaveFrameRaw(f, null, onCheck);
                Describe(onCheck);
            }
            return 0;
        }

        (int ox, int oy, int screenW, int screenH) = PickCaptureOrigin();
        Console.WriteLine($"capture origin = ({ox},{oy})   screen = {screenW}x{screenH}");
        (int cxr, int cyr) = CursorPos;
        Console.WriteLine($"cursor         = ({cxr},{cyr})");

        using var capture = new ScreenCapture(ox, oy, CaptureSize);

        // 長命資源：正式輪盤也是「建立一次、顯示很多次」，所以這些都不該在每帧迴圈裡配置。
        byte[] blurred = new byte[CaptureSize * CaptureSize * 4];
        byte[] rawCopy = new byte[CaptureSize * CaptureSize * 4];
        var frame = new System.Drawing.Bitmap(CaptureSize, CaptureSize,
                                              System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var co = Precompute();
        var labelG = System.Drawing.Graphics.FromImage(frame);

        for (int i = 0; i < Warmup; i++)
            RenderOnce(capture, blurred, frame, labelG, co);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long wsBaseline = Environment.WorkingSet;
        long peakSample = wsBaseline;

        var tBlit = new double[Iterations];
        var tFixup = new double[Iterations];
        var tBlur = new double[Iterations];
        var tCompose = new double[Iterations];
        double tSave;

        for (int i = 0; i < Iterations; i++)
        {
            capture.Grab(out double msBlit, out double msFixup);
            tBlit[i] = msBlit;
            tFixup[i] = msFixup;

            var sw = Stopwatch.StartNew();
            capture.Blur(blurred, BlurRadius, BlurPasses);
            tBlur[i] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            Compose(blurred, frame, co);
            DrawLabels(labelG, co);
            tCompose[i] = sw.Elapsed.TotalMilliseconds;

            long wsNow = Environment.WorkingSet;
            if (wsNow > peakSample) peakSample = wsNow;
        }

        // 存檔：單獨量（不是每帧都會發生的事）
        {
            var sw = Stopwatch.StartNew();
            SaveFrame(frame, blurred, blurredPath);
            tSave = sw.Elapsed.TotalMilliseconds;
        }

        // 對照組：**同一張**抓圖，只是不模糊
        capture.Grab();
        capture.SnapshotRawInto(rawCopy);
        using (var frame2 = ComposeSingle(rawCopy, co))
            SaveFrame(frame2, rawCopy, noblurPath);

        long wsAfter = Environment.WorkingSet;
        long peakWs = Process.GetCurrentProcess().PeakWorkingSet64;

        // ---------------------------------------------------------------
        // 報表
        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--------------------------------------------------------------------------");
        Console.WriteLine("  stage                                  avg ms      max ms      min ms");
        Console.WriteLine("--------------------------------------------------------------------------");
        PrintRow("1.  BitBlt screen capture", tBlit);
        PrintRow("1b. alpha fixup + raw copy", tFixup);
        PrintRow("2.  separable box blur (3 pass)", tBlur);
        PrintRow("3.  compose + labels (GDI+)", tCompose);

        var tTotal = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
            tTotal[i] = tBlit[i] + tFixup[i] + tBlur[i] + tCompose[i];
        PrintRow("TOTAL (capture+blur+compose)", tTotal);

        var tApp = new double[Iterations];   // 應用程式自己掌控的部分（不含抓螢幕）
        for (int i = 0; i < Iterations; i++)
            tApp[i] = tFixup[i] + tBlur[i] + tCompose[i];
        PrintRow("TOTAL (blur+compose only)", tApp);
        Console.WriteLine("--------------------------------------------------------------------------");
        Console.WriteLine($"  init: geometry coefficient bake (once) = {InitCoefficientsMs,7:F3} ms");
        Console.WriteLine($"  4.   PNG encode + write (once)         = {tSave,7:F3} ms");
        Console.WriteLine("--------------------------------------------------------------------------");
        Console.WriteLine();

        double avgTotal = tTotal.Average(), maxTotal = tTotal.Max();
        double avgBlit = tBlit.Average(), maxBlit = tBlit.Max();
        double avgApp = tApp.Average(), maxApp = tApp.Max();

        Console.WriteLine("=== TIME BUDGET (spec: total < 10 ms) ===");
        Console.WriteLine($"  TOTAL      avg {avgTotal,7:F3} ms  max {maxTotal,7:F3} ms  -> {(avgTotal < 10 && maxTotal < 10 ? "PASS" : "FAIL")}");
        Console.WriteLine($"   BitBlt    avg {avgBlit,7:F3} ms  max {maxBlit,7:F3} ms");
        Console.WriteLine($"   app-side  avg {avgApp,7:F3} ms  max {maxApp,7:F3} ms  -> {(avgApp < 10 && maxApp < 10 ? "PASS" : "FAIL")}");
        Console.WriteLine();

        double mbBase = Mb(wsBaseline), mbSample = Mb(peakSample);
        double mbPeak = Mb(peakWs), mbAfter = Mb(wsAfter);
        double deltaSample = mbSample - mbBase, deltaPeak = mbPeak - mbBase;

        Console.WriteLine("=== MEMORY BUDGET (spec: growth < 5 MB) ===");
        Console.WriteLine($"  working set baseline (after warmup)   = {mbBase,7:F2} MB");
        Console.WriteLine($"  working set peak sampled in loop      = {mbSample,7:F2} MB");
        Console.WriteLine($"  working set after loop                = {mbAfter,7:F2} MB");
        Console.WriteLine($"  PeakWorkingSet64 (process lifetime)   = {mbPeak,7:F2} MB");
        Console.WriteLine($"  delta (sampled peak - baseline)       = {deltaSample,7:F2} MB  -> {(deltaSample < 5.0 ? "PASS" : "FAIL")}");
        Console.WriteLine($"  delta (PeakWorkingSet64 - baseline)   = {deltaPeak,7:F2} MB  -> {(deltaPeak < 5.0 ? "PASS" : "FAIL")}");
        Console.WriteLine();

        bool timePass = avgTotal < 10.0 && maxTotal < 10.0;
        bool memPass = deltaSample < 5.0;
        Console.WriteLine($"VERDICT (spec: total<10 ms AND growth<5 MB) : {(timePass && memPass ? "PASS" : "FAIL")}");
        Console.WriteLine($"  time budget : {(timePass ? "PASS" : "FAIL")}   memory budget : {(memPass ? "PASS" : "FAIL")}");
        Console.WriteLine();

        Console.WriteLine("=== FILES ===");
        Describe(blurredPath);
        Describe(noblurPath);

        if (diagnose)
            Diagnose(capture, screenW, screenH, ox, oy);

        if (show)
        {
            Console.WriteLine();
            Console.WriteLine("--show given: opening a visible window (330x330 at primary screen 0,0).");
            Console.WriteLine("Press any key in that window to close.");
            ShowWindow(blurredPath);
        }

        return 0;
    }

    private static bool Has(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>讀 --name=NN 形式的數值參數。</summary>
    private static double NumArg(string[] args, string name, double fallback)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(a[(name.Length + 1)..], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                return v;
        }
        return fallback;
    }

    /// <summary>讀 --name=字串 形式的參數。</summary>
    private static string StrArg(string[] args, string name, string fallback)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a[(name.Length + 1)..];
        }
        return fallback;
    }

    // ------------------------------------------------------------------
    // 一次完整的 render（暖機用）
    // ------------------------------------------------------------------
    private static void RenderOnce(ScreenCapture capture, byte[] blurred,
                                   System.Drawing.Bitmap frame, System.Drawing.Graphics g, Coefficients co)
    {
        capture.Grab();
        capture.Blur(blurred, BlurRadius, BlurPasses);
        Compose(blurred, frame, co);
        DrawLabels(g, co);
    }

    private static byte[] RunPipeline(ScreenCapture capture, bool skipGrab = false)
    {
        if (!skipGrab) capture.Grab();
        var buf = new byte[CaptureSize * CaptureSize * 4];
        capture.Blur(buf, BlurRadius, BlurPasses);
        return buf;
    }

    // ------------------------------------------------------------------
    // 調色盤：cream ＝ 第一輪 spike 的基準（不得改動）；d ＝ 已定案的「冷鋼薄荷・對比加強」
    // ------------------------------------------------------------------
    /// <summary>
    /// 套用調色盤。<b>cream 分支刻意什麼都不做</b>——現行欄位值就是第一輪 spike 的值，
    /// 這樣 REPORT.md 的每一個數字都能用同一支執行檔重現。
    /// </summary>
    private static void ApplyPalette(string name)
    {
        PaletteName = name.Trim().ToLowerInvariant();
        if (PaletteName != "d") { PaletteName = "cream"; return; }

        // ---- D「冷鋼薄荷・對比加強」（計畫檔 §1；與互動原型的數值一致）----
        DarkVeilAlpha = 0.80;                      // 一整顆連貫的深色玻璃底
        VeilColor = (8, 11, 15);                   // #080B0F（不是純黑）
        CreamAlpha = 0.20;                         // 偶數格 rgba(150,180,205,.20)
        SectorAlphaAlt = 0.13;                     // 奇數格 rgba(70,95,118,.13)
        SectorFill = (150, 180, 205);
        SectorFillAlt = (70, 95, 118);
        Ink = (255, 255, 255);                     // 扇區文字純白
        RimColor = (173, 249, 235);                // 外緣薄荷線：原 #5AF2D6 往白混 50%
        RimAlpha = 0.55;
        InnerHairColor = (255, 255, 255);
        InnerHairAlpha = 0.14;
        CutStrokeColor = (255, 255, 255);
        CutStrokeAlpha = 0.0;                      // 中心改由來源縮圖自己的內圈白線收邊（原型如此）
        GlowColor = (163, 241, 228);               // 柔暈 rgba(163,241,228,.34)（強度沿用既有衰減，只換色）
        SectorHalfGapDeg = SectorCount switch { 2 => 4.0, 3 => 1.5, _ => 1.0 };  // 2 格總缺口 8°、4 格 2°
        DrawSectorSeparators = false;              // D 用「缺口」當分隔，不畫分隔線

        // 設計模式（--design=full）的數值：直接取自互動原型的 D 參數
        CenterCutoutRadius = 47.0;                 // 原型 RI=47（R=110）
        SelFillColor = (0x3F, 0x6B, 0x7A);         // 選中填色 #3F6B7A（不透明）
        SelStrokeColor = (0xFF, 0xC2, 0x4D);       // 選中描邊＝琥珀 #FFC24D
                                                   // （使用者 2026-10-01 拍板；原本的 #5AF2D6 與外緣薄荷線
                                                   //   色相只差約 27°，在彩色桌布上會被誤認為外緣線加粗。
                                                   //   琥珀與薄荷近乎互補，選中一眼就認得出來。）
                                                   // 仍可用 --selcolor=RRGGBB 覆寫做候選比較。
        SelStrokeWidth = 2.0;
        SelExpand = 4.0;
        LabelDimOpacity = 0.42;                    // 未選中的文字變暗
        CapBg = (0xFF, 0xFF, 0xFF);
        CapFg = (0x0A, 0x0F, 0x14);

        LabelBrush.Dispose();
        LabelBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, Ink.R, Ink.G, Ink.B));
    }

    // ------------------------------------------------------------------
    // 合成桌布（--backdrop=dark|light）
    // ------------------------------------------------------------------
    /// <summary>
    /// 造一張 330×330 的合成桌布（BGRA）。
    ///
    /// 為什麼要合成而不是叫使用者換桌布：換真實桌布會動到系統、不可重複、也沒辦法在 CI 跑。
    /// 內容刻意帶**細節**（色塊、細橫線、一條斜邊）——純色底模糊起來跟沒模糊一模一樣，
    /// 那樣出的圖完全無法回答「像不像毛玻璃」。
    /// </summary>
    /// <summary>整張桌布的平均亮度（0–255）。用 4px 間隔取樣就夠，不必逐點。</summary>
    private static double MeanLuma(byte[] bgra)
    {
        double sum = 0; long n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 16)
        {
            double l = 0.2126 * bgra[i + 2] + 0.7152 * bgra[i + 1] + 0.0722 * bgra[i];
            sum += l; n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>把 "--flag=RRGGBB" 解成顏色；格式不對就回傳預設值。</summary>
    private static (byte, byte, byte) HexArg(string[] args, string name, (byte R, byte G, byte B) dflt)
    {
        string v = StrArg(args, name, "");
        if (v.Length != 6) return dflt;
        try
        {
            return (Convert.ToByte(v.Substring(0, 2), 16),
                    Convert.ToByte(v.Substring(2, 2), 16),
                    Convert.ToByte(v.Substring(4, 2), 16));
        }
        catch { return dflt; }
    }

    private static byte[] BuildBackdrop(string kind)
    {
        int S = CaptureSize;
        bool light = kind == "light";
        var buf = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                double u = x / (double)S, v = y / (double)S;
                double rr, gg, bb;
                if (light) { rr = 236 + 14 * u; gg = 240 + 8 * v; bb = 246 + 6 * (1 - v); }   // 近白偏冷
                else { rr = 22 + 10 * u; gg = 26 + 8 * v; bb = 34 + 12 * (1 - v); }           // 近黑偏藍

                // 兩團色塊（模糊後變成柔和的暈）
                double blob1 = Blob(u - 0.22, v - 0.20, 0.16) * (light ? 0.45 : 1.0);
                double blob2 = Blob(u - 0.78, v - 0.74, 0.18) * (light ? 0.55 : 1.0);
                rr += 46 * blob1 + 12 * blob2;
                gg += 30 * blob1 + 44 * blob2;
                bb += 20 * blob1 + 78 * blob2;

                // 細橫線（「文件」的感覺）：模糊前是硬邊，模糊後糊掉——這才是玻璃的證據
                if ((y / 7) % 2 == 0) { rr += light ? -26 : 24; gg += light ? -26 : 24; bb += light ? -22 : 26; }

                // 一條斜邊（橫跨圓盤邊緣，用來看「盤內糊、盤外銳」的對比）
                if (Math.Abs((x - 40) - (y - 60) * 0.6) < 2.0) { rr = light ? 40 : 232; gg = light ? 62 : 240; bb = light ? 92 : 250; }

                int i = (y * S + x) * 4;
                buf[i + 0] = Clamp255(bb);
                buf[i + 1] = Clamp255(gg);
                buf[i + 2] = Clamp255(rr);
                buf[i + 3] = 255;
            }
        }
        return buf;
    }

    private static double Blob(double dx, double dy, double sigma)
        => Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));

    private static byte Clamp255(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    private static void PrintRow(string label, double[] xs)
        => Console.WriteLine($"  {label,-38} {xs.Average(),10:F3} {xs.Max(),10:F3} {xs.Min(),10:F3}");

    private static double Mb(long bytes) => bytes / 1048576.0;

    private static void Describe(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  MISSING {path}"); return; }
        using var bmp = new System.Drawing.Bitmap(path);
        var fi = new FileInfo(path);
        Console.WriteLine($"  OK {path}");
        Console.WriteLine($"     {bmp.Width}x{bmp.Height} px, {fi.Length} bytes, format={bmp.PixelFormat}");
    }

    // ==================================================================
    // 診斷段：為什麼 BitBlt 跟面積無關？
    // ==================================================================
    private static void Diagnose(ScreenCapture capture, int screenW, int screenH, int ox, int oy)
    {
        Console.WriteLine();
        Console.WriteLine("=== DIAGNOSIS ===");

        // 1) 抓取面積 vs 耗時
        Console.WriteLine("BitBlt cost by capture area (20 samples each):");
        foreach (int sz in new[] { 16, 32, 64, 128, 220, 330, 512 })
        {
            int px = Math.Clamp(screenW / 2 - sz / 2, 0, Math.Max(0, screenW - sz));
            int py = Math.Clamp(screenH / 2 - sz / 2, 0, Math.Max(0, screenH - sz));
            using var probe = new ScreenCapture(px, py, sz);
            var bt = new double[20];
            for (int i = 0; i < 20; i++) { probe.Grab(out double b, out _); bt[i] = b; }
            Console.WriteLine($"  {sz,4}x{sz,-4} = {sz * sz,7} px   avg {bt.Average(),7:F3} ms   max {bt.Max(),7:F3} ms");
        }

        // 2) 抓取位置 vs 耗時
        Console.WriteLine("BitBlt cost by screen position (20 samples each, 330x330):");
        foreach (var (label, fx, fy) in new[]
                 {
                     ("top-left", 0.05, 0.05), ("center", 0.50, 0.50),
                     ("bottom-right", 0.90, 0.90), ("cursor+18", -1.0, -1.0)
                 })
        {
            int px, py;
            if (fx < 0) { px = ox; py = oy; }
            else
            {
                px = Math.Clamp((int)(screenW * fx), 0, Math.Max(0, screenW - CaptureSize));
                py = Math.Clamp((int)(screenH * fy), 0, Math.Max(0, screenH - CaptureSize));
            }
            using var probe = new ScreenCapture(px, py, CaptureSize);
            var bt = new double[20];
            for (int i = 0; i < 20; i++) { probe.Grab(out double b, out _); bt[i] = b; }
            Console.WriteLine($"  {label + $" ({px},{py})",-30} avg {bt.Average(),7:F3} ms   max {bt.Max(),7:F3} ms");
        }

        // 3) 其他擷取路徑
        Console.WriteLine("Alternative capture paths (20 samples each, 330x330):");
        CaptureProbe.MeasureAll(screenW / 2 - CaptureSize / 2, screenH / 2 - CaptureSize / 2, CaptureSize);

        // 4) 更新週期同步假說
        Console.WriteLine("Refresh-cycle hypothesis (330x330):");
        {
            int px = Math.Clamp(screenW / 2 - CaptureSize / 2, 0, Math.Max(0, screenW - CaptureSize));
            int py = Math.Clamp(screenH / 2 - CaptureSize / 2, 0, Math.Max(0, screenH - CaptureSize));
            using var probe = new ScreenCapture(px, py, CaptureSize);
            for (int i = 0; i < 5; i++) probe.Grab(out _, out _);

            var tight = new double[20];
            for (int i = 0; i < 20; i++) { probe.Grab(out double b, out _); tight[i] = b; }
            Console.WriteLine($"  tight loop : avg {tight.Average(),7:F3} ms  max {tight.Max(),7:F3} ms  min {tight.Min(),7:F3} ms");

            var spaced = new double[20];
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(30);
                probe.Grab(out double b, out _);
                spaced[i] = b;
            }
            Console.WriteLine($"  spaced 30ms: avg {spaced.Average(),7:F3} ms  max {spaced.Max(),7:F3} ms  min {spaced.Min(),7:F3} ms");

            var four = new double[10];
            for (int i = 0; i < 10; i++)
            {
                var s2 = Stopwatch.StartNew();
                for (int k = 0; k < 4; k++) probe.Grab(out _, out _);
                four[i] = s2.Elapsed.TotalMilliseconds;
            }
            Console.WriteLine($"  4 consecutive calls: avg total {four.Average(),7:F3} ms " +
                              $"(= {four.Average() / 4.0:F3} ms each)");
        }

        // 5) 抓到的內容是不是真的（排除 BitBlt 悄悄失敗）
        capture.Grab(out _, out _);
        Console.WriteLine($"  {capture.DescribeCapture()}");
    }

    // ==================================================================
    // 合成
    // ==================================================================

    /// <summary>
    /// 每像素的混色係數（整數，避免內圈浮點）。
    ///
    /// **踩到的坑（造成 managed heap 損毀，最貴的一個）**：Glow／Veil 一開始宣告成
    /// <c>CaptureSize*CaptureSize</c>（1 byte/像素），但 Blend* 是**以 4 byte/像素定址**的
    /// （premultiplied BGRA）。所以 <c>BlendDisc(glow, 330, ...)</c> 會往一個 108,900 byte 的陣列
    /// 寫到 byte 435,599 —— 直接打穿 managed heap。
    /// 症狀極具誤導性：Precompute 本身完全正常跑完，接著第一次 <c>GC.Collect()</c> 直接以
    /// 「Internal CLR error 0x80131506」崩潰（GC 只是第一個走到壞掉記憶體的人）。
    /// 教訓：**unsafe 指標運算的「元素大小」必須跟陣列宣告一致**；
    /// 這種錯誤不會在寫入當下報錯，只會在下次 GC 時爆炸，而且會讓「配置資源」這種無辜步驟看起來像凶手。
    /// </summary>
    internal sealed class Coefficients
    {
        public readonly byte[] Glow = new byte[CaptureSize * CaptureSize * 4];
        public readonly byte[] Veil = new byte[CaptureSize * CaptureSize * 4];
        /// <summary>暫存畫布：Blend* 寫的是 premultiplied BGRA，所以是 4 byte/像素。</summary>
        public readonly byte[] Cream = new byte[CaptureSize * CaptureSize * 4];
        /// <summary>壓成一個 int：A&lt;&lt;24 | R&lt;&lt;16 | G&lt;&lt;8 | B（premultiplied）。</summary>
        public readonly int[] CreamRgb = new int[CaptureSize * CaptureSize];
        public readonly bool[] Cutout = new bool[CaptureSize * CaptureSize];
        /// <summary>像素是否落在輪盤圓盤內（用來決定光暈可不可以洗進盤面）。</summary>
        public readonly bool[] InDisc = new bool[CaptureSize * CaptureSize];
        /// <summary>圓盤外的柔投影強度（0–255），讓玻璃有厚度。</summary>
        public readonly byte[] Shadow = new byte[CaptureSize * CaptureSize];
    }

    /// <summary>幾何烘表的耗時（只在啟動時付一次）。</summary>
    internal static double InitCoefficientsMs { get; private set; }
    private static Coefficients? _coef;

    /// <summary>
    /// 把輪盤幾何烘成 per-pixel 係數表。**每個行程只做一次**。
    ///
    /// 為什麼可以這樣做：輪盤的幾何是**靜態**的（直徑、扇區數、角度、鏤空半徑都不隨 frame 變），
    /// 每帧真正變的只有底下的背景。所以「這個像素落在哪個扇區／離圓心多遠／在不在鏤空裡」
    /// 這些幾何判斷都只需要算一次。烘完之後每帧就退化成單一條逐像素迴圈，
    /// 內圈沒有 atan2、沒有 sqrt（實測把合成從 4.3 ms 降到 0.7 ms）。
    ///
    /// 這不是把成本藏起來：正式輪盤的生命週期就是「建立一次、顯示很多次」，
    /// 這筆初始化成本攤提到所有 frame 之後趨近於零，而每帧成本才是預算要管的東西。
    /// 初始化耗時本身也印在報表裡（init: geometry coefficient bake）。
    /// </summary>
    internal static Coefficients Precompute()
    {
        if (_coef != null) return _coef;

        // 開工前先驗證每個表都夠大：Blend* 一律以 4 byte/像素定址（見 Coefficients 的註解）。
        {
            var probe = new Coefficients();
            int need = CaptureSize * CaptureSize * 4;
            if (probe.Glow.Length < need || probe.Veil.Length < need || probe.Cream.Length < need)
                throw new InvalidOperationException(
                    $"coefficient table too small: need {need}, got glow={probe.Glow.Length} " +
                    $"veil={probe.Veil.Length} cream={probe.Cream.Length}");
        }

        var sw = Stopwatch.StartNew();

        int S = CaptureSize;
        var co = new Coefficients();
        double cx = S / 2.0, cy = S / 2.0;
        double Rw = WheelDiameter / 2.0;
        double sweep = 360.0 / SectorCount;
        double cut = CenterCutoutRadius;

        unsafe
        {
            // 借用既有的 Blend* 幾何程式碼畫進暫存表，再讀回來。
            // 這樣「幾何定義」只有一份，不會出現「烘表」與「直接繪製」兩套實作走鐘。
            //
            // px() ＝ 把「1× 的設計 px 值」換算成當前解析度；--scale=3 時所有線寬、半徑、
            // 缺口一起變三倍，所以放大後的圖是真的三倍細節，不是把 330 的圖拉大。
            double px(double v) => v * Scale;

            fixed (byte* glow = co.Glow)
            fixed (byte* veil = co.Veil)
            fixed (byte* cream = co.Cream)
            {
                // 外緣柔光暈：由外而內疊同心圓，SourceOver 自然累積成柔和的暈
                // （不是畫筆描邊——描邊會在半徑上留下硬邊，疊圓才是連續衰減）
                for (int k = 14; k >= 1; k--)
                {
                    double rr = Rw + px(3.0) + k * px(2.6);
                    double a = (0.016 + 0.004 * (14 - k)) * GlowAlphaMul;
                    BlendDisc(glow, S, cx, cy, rr, GlowColor, a);
                }

                // 深色薄罩：整顆圓盤壓一層暗幕 = 玻璃底材（cream 是純黑 22%，D 是 #080B0F 80%）
                BlendDisc(veil, S, cx, cy, Rw, VeilColor, DarkVeilAlpha);

                // 髮絲細線（外環兩圈）**必須畫在扇區之前**：原型裡 SVG 疊在玻璃之上，
                // 所以不透明的選中扇區會蓋掉外緣那條線；若畫在之後，選中扇區上會多出一條
                // 橫切的薄荷線，與它的描邊之間夾一道填色細縫，看起來像「兩條線」。
                // 未選中的扇區是半透明的，這條線仍然透得出來（與原型一致）。
                BlendStroke(cream, S, cx, cy, Rw - px(0.6), px(1.4), RimColor, RimAlpha);
                BlendStroke(cream, S, cx, cy, Rw - px(3.4), px(0.8), InnerHairColor, InnerHairAlpha);

                // 扇區疊在暗玻璃**之上**。
                // 第一版把扇區畫在薄罩底下，結果整顆輪盤變成 #302A25 的暗褐色，
                // 與 docs/design/reference/wheel-format-stable.jpg 的亮奶油扇區完全不像。
                for (int i = 0; i < SectorCount; i++)
                {
                    bool odd = i % 2 == 1;
                    bool sel = (i == SelectedSector);
                    double a0 = -90.0 + i * sweep + SectorHalfGapDeg;
                    double a1 = -90.0 + (i + 1) * sweep - SectorHalfGapDeg;
                    double rOut = Rw - px(1.5) + (sel ? px(SelExpand) : 0);

                    if (sel)
                    {
                        // 選中：先用描邊色畫一整塊扇形，再用填色往內縮 px(SelStrokeWidth) 蓋上去，
                        // 就得到一圈**沿著扇形輪廓**的描邊。比另刻一條圓弧描邊函式簡單，
                        // 而且填色與描邊一定對齊（同一組角度、同一個半徑基準）。
                        BlendPie(cream, S, cx, cy, rOut, a0, a1, SelStrokeColor, 0.95);
                        BlendPie(cream, S, cx, cy, rOut - px(SelStrokeWidth), a0, a1, SelFillColor, 1.0);
                    }
                    else
                    {
                        var tint = odd ? SectorFillAlt : SectorFill;
                        double alpha = odd ? (SectorAlphaAlt ?? CreamAlpha) : CreamAlpha;
                        BlendPie(cream, S, cx, cy, rOut, a0, a1, tint, alpha);
                    }
                }

                // 髮絲細線（扇區分隔線 + 鏤空邊緣）——這兩條畫在扇區之後才對
                if (DrawSectorSeparators)
                {
                    for (int i = 0; i < SectorCount; i++)
                    {
                        double a = (-90.0 + i * sweep) * Math.PI / 180.0;
                        double r0 = cut + px(3.0), r1 = Rw - px(2.0);
                        BlendLine(cream, S,
                                  cx + Math.Cos(a) * r0, cy + Math.Sin(a) * r0,
                                  cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1,
                                  SepColor, SepAlpha);
                    }
                }
                BlendStroke(cream, S, cx, cy, cut, px(1.2), CutStrokeColor, CutStrokeAlpha);
            }
        }

        // 把 premultiplied BGRA 整理成一個 int（省一次陣列存取），並標出中心鏤空
        for (int y = 0; y < S; y++)
        {
            double dy = y + 0.5 - cy;
            for (int x = 0; x < S; x++)
            {
                int i = y * S + x;
                int cA = co.Cream[i * 4 + 3];
                co.CreamRgb[i] = cA == 0
                    ? 0
                    : (cA << 24) | (co.Cream[i * 4 + 2] << 16) | (co.Cream[i * 4 + 1] << 8) | co.Cream[i * 4 + 0];
                if (Math.Abs(dy) <= cut)
                {
                    double dx = x + 0.5 - cx;
                    if (dx * dx + dy * dy <= cut * cut) co.Cutout[i] = true;
                }
                // 順手把「圓盤內」也標出來（光暈要不要洗進盤面用得到）
                // ⚠ 半徑必須含**選中扇區的外擴**：互動原型裡選中那一格會凸出玻璃圓盤 4px
                //   （SVG 沒被玻璃的遮罩裁掉），若照 Rw 裁，整條薄荷描邊會消失——實測就是這樣。
                double discR = Rw + (SelectedSector >= 0 ? SelExpand * Scale : 0);
                if (Math.Abs(dy) <= discR)
                {
                    double dx = x + 0.5 - cx;
                    if (dx * dx + dy * dy <= discR * discR) co.InDisc[i] = true;
                }
                // 投影：以**玻璃邊緣**為起點往外衰減（讓玻璃浮起來，而不是貼在桌布上）。
                // ⚠ 這裡刻意**不再依賴上面那個 if/else**：一旦「盤內」半徑含了選中外擴（Rw+4px），
                //   用 else 分支算投影會漏掉那一圈——它被判成盤內、卻又不是玻璃，
                //   結果既不畫玻璃也不畫投影／光暈，輪盤外緣就出現一圈 4px 的透明縫。
                //   （實測：淺色桌布那張，r=338 是桌布色 #E7E4EF，內外兩側都是深色。）
                {
                    double dx = x + 0.5 - cx;
                    double d = Math.Sqrt(dx * dx + dy * dy) - Rw;
                    if (d >= 0 && d < ShadowRadius)
                    {
                        double t = 1.0 - d / ShadowRadius;
                        co.Shadow[i] = (byte)(ShadowMax * t * t);   // 平方衰減＝比較柔和
                    }
                }
            }
        }

        sw.Stop();
        InitCoefficientsMs = sw.Elapsed.TotalMilliseconds;
        _coef = co;
        DumpCoefficients(co);
        return co;
    }

    /// <summary>診斷：把幾個關鍵像素的係數印出來，確認烘表結果符合預期。</summary>
    private static void DumpCoefficients(Coefficients co)
    {
        if (!Has(Environment.GetCommandLineArgs(), "--dump-coef")) return;
        Console.WriteLine("--- coefficient dump (B,G,R,A per table) ---");
        // 三個扇區的中間角，半徑 74（標籤所在半徑）
        foreach (var mid in new[] { -30.0, 90.0, 210.0 })
        {
            double rad = mid * Math.PI / 180.0;
            int x = (int)(CaptureSize / 2.0 + Math.Cos(rad) * 74);
            int y = (int)(CaptureSize / 2.0 + Math.Sin(rad) * 74);
            int i = y * CaptureSize + x;
            Console.WriteLine($"  sector {mid,5} deg @ ({x},{y}) idx={i}:");
            Console.WriteLine($"     glow  B={co.Glow[i * 4 + 0],3} G={co.Glow[i * 4 + 1],3} R={co.Glow[i * 4 + 2],3} A={co.Glow[i * 4 + 3],3}");
            Console.WriteLine($"     veil  B={co.Veil[i * 4 + 0],3} G={co.Veil[i * 4 + 1],3} R={co.Veil[i * 4 + 2],3} A={co.Veil[i * 4 + 3],3}");
            Console.WriteLine($"     cream B={co.Cream[i * 4 + 0],3} G={co.Cream[i * 4 + 1],3} R={co.Cream[i * 4 + 2],3} A={co.Cream[i * 4 + 3],3}");
            Console.WriteLine($"     rgb=0x{co.CreamRgb[i]:X8}  cutout={co.Cutout[i]}");
        }
        // 中心鏤空
        {
            int i = (CaptureSize / 2) * CaptureSize + CaptureSize / 2;
            Console.WriteLine($"  center ({CaptureSize / 2},{CaptureSize / 2}) idx={i}: cutout={co.Cutout[i]} " +
                              $"creamA={co.Cream[i * 4 + 3]} rgb=0x{co.CreamRgb[i]:X8}");
        }
        // 輪盤外的角落
        {
            int i = 10 * CaptureSize + 10;
            Console.WriteLine($"  corner (10,10) idx={i}: cutout={co.Cutout[i]} " +
                              $"glowA={co.Glow[i * 4 + 3]} veilA={co.Veil[i * 4 + 3]} creamA={co.Cream[i * 4 + 3]}");
        }
        // 全表統計
        int nz = 0, sumA = 0;
        for (int i = 0; i < co.CreamRgb.Length; i++)
        {
            int a = co.CreamRgb[i] >>> 24;
            if (a != 0) { nz++; sumA += a; }
        }
        Console.WriteLine($"  CreamRgb nonzero={nz} / {co.CreamRgb.Length}  mean alpha={(nz == 0 ? 0 : sumA / (double)nz):F1}");
    }

    /// <summary>單發版本：自己配置 bitmap（給 --dump-only 與對照圖用）。</summary>
    internal static System.Drawing.Bitmap ComposeSingle(byte[] background, Coefficients co)
    {
        var bmp = new System.Drawing.Bitmap(CaptureSize, CaptureSize,
                                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Compose(background, bmp, co);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        DrawLabels(g, co);
        return bmp;
    }

    /// <summary>
    /// 每一帧的合成：把係數表套用到這張背景上，寫進（可重複使用的）bitmap。
    /// <paramref name="bmp"/> 由呼叫者擁有並重複使用——正式輪盤不該每帧 new 一張 435 KB 的 Bitmap。
    /// </summary>
    internal static void Compose(byte[] background, System.Drawing.Bitmap bmp, Coefficients co)
    {
        int S = CaptureSize;
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, S, S),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* dst = (byte*)data.Scan0;
            int dstStride = data.Stride;
            fixed (byte* glow = co.Glow)
            fixed (byte* veil = co.Veil)
            fixed (bool* cut = co.Cutout)
            fixed (bool* inDisc = co.InDisc)
            fixed (int* creamRgb = co.CreamRgb)
            fixed (byte* shadow = co.Shadow)
            {
                for (int y = 0; y < S; y++)
                {
                    byte* row = dst + (long)y * dstStride;
                    int off = y * S;
                    for (int x = 0; x < S; x++)
                    {
                        int i = off + x;
                        byte* p = row + x * 4;

                        if (cut[i])                       // 中心鏤空：SourceCopy 全透明
                        {
                            p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 0;
                            continue;
                        }

                        int b = background[i * 4 + 0];
                        int g = background[i * 4 + 1];
                        int r = background[i * 4 + 2];

                        int sh = shadow[i];
                        int ga = glow[i * 4 + 3];                 // 光暈 alpha

                        int vA = veil[i * 4 + 3];                 // 薄罩 alpha（圓盤內 > 0）
                        int vB = veil[i * 4 + 0], vG = veil[i * 4 + 1], vR = veil[i * 4 + 2];
                        int cr = creamRgb[i];

                        // 光暈只讓「真正的玻璃像素」擋掉，不能用 InDisc：
                        // InDisc 含選中外擴，用它會把玻璃外那一圈 4px 的光暈也抹掉（同一個透明縫 bug）。
                        if (GlowInsideDisc && (vA != 0 || cr != 0)) ga = 0;

                        int outA;
                        if (inDisc[i] && (vA != 0 || cr != 0))
                        {
                            // ---- 玻璃本體：**不透明** ----
                            // 模糊、薄罩、扇區都已經烘進 RGB 了，所以 alpha 必須是滿的。
                            //
                            // 【第一輪的 bug，2026-10-01 第二輪修正】第一版把 alpha 設成「薄罩之後剩下的
                            // 背景比例」（f = 255 - veilA），於是存圖端與 DWM 會**再跟同一張桌布合成一次**：
                            //      out = (0.2·bg + veil) + 0.8·bg = bg + veil
                            // 剛好把薄罩整個抵銷掉。實測 `--veil=0` 與 `--veil=80` 產出的 PNG
                            // **位元完全相同**（MD5 相同），也就是說第一輪所有「薄罩看起來如何」的
                            // 目視結論（含視覺模型的評語）都不是薄罩造成的。時間與記憶體數字不受影響。
                            int f = 255 - vA;
                            if (cr != 0)
                            {
                                // 扇區疊在**已經壓暗的**背景之上（premultiplied SourceOver）
                                int ca = SectorTranslucent ? cr >>> 24 : 255;
                                int inv = 255 - ca;
                                r = ((cr >> 16) & 0xFF) + (r * f / 255 * inv + 127) / 255 + (vR * inv + 127) / 255;
                                g = ((cr >> 8) & 0xFF) + (g * f / 255 * inv + 127) / 255 + (vG * inv + 127) / 255;
                                b = (cr & 0xFF) + (b * f / 255 * inv + 127) / 255 + (vB * inv + 127) / 255;
                            }
                            else
                            {
                                r = (r * f + 127) / 255 + vR;
                                g = (g * f + 127) / 255 + vG;
                                b = (b * f + 127) / 255 + vB;
                            }
                            outA = 255;                            // ★ 玻璃是不透明的
                        }
                        else
                        {
                            // ---- 圓盤外：這是「圖層」，不是「最終外觀」----
                            // 投影＝黑色圖層（alpha＝投影強度）、光暈＝自己的顏色；
                            // 兩者都**不把桌布烘進來**，這樣餵給 UpdateLayeredWindow 時，
                            // DWM 壓暗的才是**真實的**桌面，而不是一份抓下來就開始過期的舊桌布。
                            int invS = 255 - sh;
                            int gB = (ga * GlowColor.B + 127) / 255;
                            int gG = (ga * GlowColor.G + 127) / 255;
                            int gR = (ga * GlowColor.R + 127) / 255;
                            b = (gB * invS + 127) / 255;
                            g = (gG * invS + 127) / 255;
                            r = (gR * invS + 127) / 255;
                            outA = sh + (ga * invS + 127) / 255;
                        }

                        p[0] = (byte)(b > 255 ? 255 : b);
                        p[1] = (byte)(g > 255 ? 255 : g);
                        p[2] = (byte)(r > 255 ? 255 : r);
                        p[3] = (byte)(outA > 255 ? 255 : outA);
                    }
                }
            }
        }
        bmp.UnlockBits(data);
    }

    private static System.Drawing.Font LabelFont =
        new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
    /// <summary>扇區文字筆刷。**非 readonly**：換調色盤／倍率時要重建（靜態初始化在 Main 之前就跑完了）。</summary>
    private static System.Drawing.SolidBrush LabelBrush =
        new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, Ink.R, Ink.G, Ink.B));
    /// <summary>未選中扇區的文字筆刷（D 的變暗 42%）。</summary>
    private static System.Drawing.SolidBrush LabelBrushDim =
        new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(107, Ink.R, Ink.G, Ink.B));
    private static System.Drawing.Font CapFont =
        new System.Drawing.Font("Segoe UI", 22f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
    private static System.Drawing.Font KeyFont =
        new System.Drawing.Font("Segoe UI", 16f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
    private static System.Drawing.Font KeyCaptionFont =
        new System.Drawing.Font("Microsoft JhengHei", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);

    /// <summary>各扇區數的標籤落點（比例半徑）與字級（px）——直接取自互動原型的量測值。</summary>
    private static double LabelRadiusRatio(int n) => n switch { 2 => 0.66, 3 => 0.72, _ => 0.76 };
    private static double LabelFontUnits(int n) => n switch { 2 => 15.0, 3 => 12.7, _ => 11.3 };

    /// <summary>設計模式的格式表（來源與各扇區的目標格式），與互動原型的 FMT 一致。</summary>
    private static readonly string[][] DesignOuts =
    {
        new[] { "JPG", "BMP" },
        new[] { "JPG", "BMP", "TIF" },
        new[] { "PNG", "JPG", "BMP", "TIF" }
    };
    private static string DesignSrc(int n) => n >= 4 ? "HEIC" : "PNG";

    /// <summary>
    /// 建立繪圖資源（字型、筆刷）。**必須在幾何定案之後呼叫一次**：
    /// 字級要跟著 --scale 走，而 --scale 又要在 ApplyPalette 決定孔徑之後才套用。
    /// 這些資源**長命**（不在每帧配置）——第一輪的教訓就是每帧配置會讓工作集爆掉。
    /// </summary>
    private static void PrepareResources()
    {
        if (DesignMode)
        {
            SectorLabels = DesignOuts[Math.Clamp(SectorCount, 2, 4) - 2];
            if (SelectedSector >= SectorLabels.Length) SelectedSector = SectorLabels.Length - 1;
            CapText = $"{DesignSrc(SectorCount)} → {SectorLabels[Math.Max(0, SelectedSector)]}";
        }

        double fs = DesignMode ? LabelFontUnits(SectorCount) : 10.0;
        LabelFont.Dispose();
        LabelFont = DesignMode
            ? new System.Drawing.Font("Segoe UI", (float)(fs * Scale), System.Drawing.FontStyle.Bold,
                                      System.Drawing.GraphicsUnit.Pixel)
            : new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold,
                                      System.Drawing.GraphicsUnit.Point);

        LabelBrush.Dispose(); LabelBrushDim.Dispose();
        LabelBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, Ink.R, Ink.G, Ink.B));
        LabelBrushDim = new System.Drawing.SolidBrush(
            System.Drawing.Color.FromArgb((int)Math.Round(245 * LabelDimOpacity), Ink.R, Ink.G, Ink.B));

        CapFont.Dispose(); KeyFont.Dispose(); KeyCaptionFont.Dispose();
        CapFont = new System.Drawing.Font("Segoe UI", 17f * Scale, System.Drawing.FontStyle.Bold,
                                          System.Drawing.GraphicsUnit.Pixel);
        SetKeyFonts(1f);
        LayoutKeycap();
    }

    private static float KeyFontPx = 15f, KeyCapFontPx = 12f;

    private static void SetKeyFonts(float mul)
    {
        KeyFont.Dispose(); KeyCaptionFont.Dispose();
        KeyFont = new System.Drawing.Font("Segoe UI", KeyFontPx * Scale * mul,
                                          System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        KeyCaptionFont = new System.Drawing.Font("Microsoft JhengHei", KeyCapFontPx * Scale * mul,
                                                 System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
    }

    /// <summary>鍵帽與說明文字的版面（**只算一次**，不在每帧重算、也不在每帧配置字型）。</summary>
    private static float KeycapX, KeycapY, KeycapW, KeycapH, CaptionX, CaptionY, CaptionW, CaptionH;
    private static void LayoutKeycap()
    {
        // 輪盤下緣到畫布底部只有 (CaptureSize/2 - Rw) px —— 由底部往上排，並在空間不足時自動縮字，
        // 保證「按住觸發鍵」不會被畫布裁掉（第一版硬編 y 值，中文直接被切一半）。
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        float scale(double v) => (float)(v * Scale);
        string key = "space", caption = "按住觸發鍵";
        float discBottom = CaptureSize / 2f + WheelDiameter / 2f;
        float bottom = CaptureSize - scale(8);

        for (int attempt = 0; ; attempt++)
        {
            var ks = g.MeasureString(key, KeyFont);
            var cs = g.MeasureString(caption, KeyCaptionFont);
            float h = ks.Height + scale(10), w = ks.Width + scale(44);
            float capY = bottom - cs.Height - scale(4) - h;
            if (capY >= discBottom + scale(2) || attempt >= 6)
            {
                KeycapW = w; KeycapH = h;
                KeycapX = CaptureSize / 2f - w / 2; KeycapY = capY;
                CaptionW = cs.Width; CaptionH = cs.Height;
                CaptionX = CaptureSize / 2f - cs.Width / 2; CaptionY = bottom - cs.Height;
                Console.WriteLine($"[layout] 鍵帽 {KeycapW:0}x{KeycapH:0} @({KeycapX:0},{KeycapY:0}), " +
                                  $"說明 @({CaptionX:0},{CaptionY:0}) {CaptionW:0}x{CaptionH:0}, " +
                                  $"輪盤下緣={discBottom:0}, 畫布={CaptureSize}");
                return;
            }
            SetKeyFonts((float)Math.Pow(0.88, attempt + 1));   // 空間不夠就一級一級縮
        }
    }
    private static readonly System.Drawing.StringFormat LabelFormat = new System.Drawing.StringFormat
    {
        Alignment = System.Drawing.StringAlignment.Center,
        LineAlignment = System.Drawing.StringAlignment.Center
    };

    /// <summary>
    /// 畫扇區標籤（交給 GDI+，不自己刻字型）。
    ///
    /// 灰階反鋸齒是必要的：ClearType 是次像素渲染、不寫 alpha，
    /// 畫在分層視窗的點陣圖上會出現黑邊（正式程式碼 WheelPresenter 也是同樣理由）。
    /// </summary>
    private static void DrawLabels(System.Drawing.Graphics g, Coefficients co)
    {
        double cx = CaptureSize / 2.0, cy = CaptureSize / 2.0;
        double Rw = WheelDiameter / 2.0;
        double sweep = 360.0 / SectorCount;

        // Graphics 狀態每次都要設，因為它可能被別的呼叫端改過
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

        double lr = Rw * (DesignMode ? LabelRadiusRatio(SectorCount) : 0.74);
        float box = (float)(Rw * 0.95);

        for (int i = 0; i < SectorCount; i++)
        {
            double mid = (-90.0 + (i + 0.5) * sweep) * Math.PI / 180.0;
            float lx = (float)(cx + Math.Cos(mid) * lr);
            float ly = (float)(cy + Math.Sin(mid) * lr);
            var brush = (DesignMode && SelectedSector >= 0 && i != SelectedSector) ? LabelBrushDim : LabelBrush;
            g.DrawString(SectorLabels[i % SectorLabels.Length], LabelFont, brush,
                         new System.Drawing.RectangleF(lx - box / 2, ly - box / 2, box, box), LabelFormat);
        }

        if (DesignMode) DrawDesignOverlay(g);
    }

    /// <summary>
    /// 設計模式的額外元素：中心來源縮圖、白膠囊（原格式 → 目標格式）、鍵帽提示。
    /// 數值全部取自互動原型（`Desktop\viz\tangerine-3c72cda8.html`），原型的 SVG 單位就是真實 px。
    /// </summary>
    private static void DrawDesignOverlay(System.Drawing.Graphics g)
    {
        float cx = CaptureSize / 2f, cy = CaptureSize / 2f;
        double Rw = WheelDiameter / 2.0;
        float ri = (float)CenterCutoutRadius;      // 中心孔半徑（D：47）
        float P(double v) => (float)(v * Scale);   // 參數收 double：C# 沒有 double→float 的隱式轉換（連 1.0 這種常數也不行）

        // ---- 中心來源縮圖（原型的 .tg-thumb：141px 圓、上藍下暗的山景）----
        {
            using var clip = new System.Drawing.Drawing2D.GraphicsPath();
            clip.AddEllipse(cx - ri, cy - ri, ri * 2, ri * 2);
            var saved = g.Save();
            g.SetClip(clip);

            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                       new RectangleF(cx - ri, cy - ri, ri * 2, ri * 2),
                       System.Drawing.Color.FromArgb(0x33, 0x54, 0x7C),
                       System.Drawing.Color.FromArgb(0x15, 0x22, 0x38),
                       System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillRectangle(lg, cx - ri, cy - ri, ri * 2, ri * 2);

            // 山景：原型是 left:-10% right:-10% bottom:-10% height:60% 的 clip-path 多邊形。
            // 兩點刻意偏離原型：
            //  1. 原型只有一層，實測在這麼小的圓裡只會看到一坨暗綠（視覺模型兩輪都說「看不出山」），
            //     所以多墊一層較亮的後山。
            //  2. 原型的山頂落在圓心附近——而圓心正好被白膠囊蓋住，稜線整個被吃掉。
            //     所以整座山往下移、壓扁，讓稜線落在膠囊下緣與圓孔下緣之間那條**看得見的帶狀區**。
            float bw = ri * 2 * 1.2f, bx = cx - ri - ri * 0.2f;
            float bh = ri * 2 * 0.5f, by = cy + ri * 0.30f;
            void Ridge(double[] uv, System.Drawing.Color col)
            {
                var pts = new System.Drawing.PointF[uv.Length / 2];
                for (int k = 0; k < pts.Length; k++)
                    pts[k] = new System.Drawing.PointF(bx + (float)uv[k * 2] * bw, by + (float)uv[k * 2 + 1] * bh);
                using var bb = new System.Drawing.SolidBrush(col);
                g.FillPolygon(bb, pts);
            }
            // 後山（亮一階）
            Ridge(new[] { 0.00, 0.40, 0.30, 0.00, 0.55, 0.34, 0.78, 0.12, 1.00, 0.42, 1.00, 1.40, 0.00, 1.40 },
                  System.Drawing.Color.FromArgb(0x35, 0x60, 0x4B));
            // 前山（原型色 #1E3A2E）
            Ridge(new[] { 0.00, 0.52, 0.26, 0.10, 0.52, 0.44, 0.70, 0.22, 1.00, 0.54, 1.00, 1.40, 0.00, 1.40 },
                  System.Drawing.Color.FromArgb(0x1E, 0x3A, 0x2E));

            g.Restore(saved);

            // 內圈 1px 白線（原型的 inset box-shadow）
            using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(41, 255, 255, 255), P(1.0));
            g.DrawEllipse(ring, cx - ri + P(0.5), cy - ri + P(0.5), ri * 2 - P(1.0), ri * 2 - P(1.0));
        }

        // ---- 白膠囊：原格式 → 目標格式 ----
        // 依使用者 2026-10-01 的指示「縮小＋加一點透明度」：字 22px→17px、padding 8/24→5/16，
        // 底色由不透明改為 alpha 216（0.85）。縮小的目的是**把中心縮圖的主體讓出來**，
        // 透明則是讓縮圖的顏色透上來——所以膠囊必須畫在縮圖「之上」用 SourceOver 合成
        // （縮圖先畫、且不透明，所以在分層視窗裡 alpha 仍是滿的，不會有半透明的破洞）。
        {
            var sz = g.MeasureString(CapText, CapFont);
            float w = Math.Max(P(100), sz.Width + P(32)), h = sz.Height + P(10);
            float x = cx - w / 2, y = cy - h / 2, r = h / 2;

            for (int k = 6; k >= 1; k--)   // 便宜的柔和投影：由外而內疊
            {
                float off = P(8) * k / 6f + P(2);
                using var sh = RoundedRect(x - P(1) * k, y - P(1) * k + off, w + P(2) * k, h + P(2) * k, r + P(1) * k);
                using var sb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(10, 0, 0, 0));
                g.FillPath(sb, sh);
            }
            using (var path = RoundedRect(x, y, w, h, r))
            using (var fb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(CapAlpha, CapBg.R, CapBg.G, CapBg.B)))
                g.FillPath(fb, path);
            using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, CapFg.R, CapFg.G, CapFg.B));
            using var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            g.DrawString(CapText, CapFont, fg, new System.Drawing.RectangleF(x, y, w, h), fmt);
        }

        // ---- 鍵帽提示（原型：白鍵帽 + 「按住觸發鍵」，置中於輪盤正下方）----
        // 版面在 LayoutKeycap() 就算好了（並保證不被畫布裁掉），這裡只負責畫。
        {
            float x = KeycapX, y = KeycapY, w = KeycapW, h = KeycapH;

            using (var hard = RoundedRect(x, y + P(4), w, h, P(10)))
            using (var hb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(115, 0, 0, 0)))
                g.FillPath(hb, hard);
            using (var path = RoundedRect(x, y, w, h, P(10)))
            using (var fb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 255, 255, 255)))
                g.FillPath(fb, path);
            using (var fg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0x14, 0x18, 0x21)))
            using (var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center })
                g.DrawString("space", KeyFont, fg, new System.Drawing.RectangleF(x, y, w, h), fmt);

            // ⚠ 這裡刻意偏離原型：原型只在一張深色桌布上看過，白字直接壓在**淺色桌布**上會整條消失
            //   （實測：淺色桌布上只有 53/714 個取樣點是暗的＝只剩陰影，字本身看不見）。
            //   改成「暗色描邊 + 白色填色」，任何桌布上都讀得到，視覺上仍是原本的白字。
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddString("按住觸發鍵", KeyCaptionFont.FontFamily,
                               (int)KeyCaptionFont.Style, KeyCaptionFont.Size,
                               new System.Drawing.PointF(CaptionX, CaptionY),
                               System.Drawing.StringFormat.GenericTypographic);
                using (var outline = new System.Drawing.Pen(System.Drawing.Color.FromArgb(165, 0, 0, 0), P(3.0)))
                {
                    outline.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawPath(outline, path);
                }
                using var cb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 255, 255, 255));
                g.FillPath(cb, path);
            }
        }
    }

    /// <summary>圓角矩形路徑（GDI+ 沒有內建；膠囊＝半徑取高度一半）。</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        if (r < 0.5f) r = 0.5f;
        if (r > h / 2f) r = h / 2f;
        if (r > w / 2f) r = w / 2f;
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ==================================================================
    // 像素運算（全部 premultiplied BGRA，與 UpdateLayeredWindow 的期待一致）
    // 這些只用來「烘係數表」，不在每一帧的熱路徑上。
    // 注意：所有函式都以 4 byte/像素定址，目標緩衝必須有 size*size*4 bytes。
    // ==================================================================

    /// <summary>把一個圓盤以 alpha 疊上去（SourceOver，premultiplied）。</summary>
    private static unsafe void BlendDisc(byte* dst, int size, double cx, double cy,
                                         double radius, (byte R, byte G, byte B) c, double alpha)
    {
        int A = (int)Math.Round(alpha * 255.0);
        if (A <= 0) return;
        int pa = (c.R * A + 127) / 255, pb = (c.G * A + 127) / 255, pc = (c.B * A + 127) / 255;
        double r2 = radius * radius;
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        int y1 = Math.Min(size - 1, (int)Math.Ceiling(cy + radius));
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        int x1 = Math.Min(size - 1, (int)Math.Ceiling(cx + radius));
        for (int y = y0; y <= y1; y++)
        {
            double dy = y + 0.5 - cy;
            byte* row = dst + (long)y * size * 4;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                if (dx * dx + dy * dy > r2) continue;
                BlendPixel(row + x * 4, pa, pb, pc, A);
            }
        }
    }

    /// <summary>圓環（描邊）：半徑 r 加減 width/2 之間的區域。</summary>
    private static unsafe void BlendStroke(byte* dst, int size, double cx, double cy,
                                           double radius, double width,
                                           (byte R, byte G, byte B) c, double alpha)
    {
        int A = (int)Math.Round(alpha * 255.0);
        if (A <= 0) return;
        int pa = (c.R * A + 127) / 255, pb = (c.G * A + 127) / 255, pc = (c.B * A + 127) / 255;
        double ro = radius + width / 2.0, ri = Math.Max(0.0, radius - width / 2.0);
        double ro2 = ro * ro, ri2 = ri * ri;
        int y0 = Math.Max(0, (int)Math.Floor(cy - ro));
        int y1 = Math.Min(size - 1, (int)Math.Ceiling(cy + ro));
        int x0 = Math.Max(0, (int)Math.Floor(cx - ro));
        int x1 = Math.Min(size - 1, (int)Math.Ceiling(cx + ro));
        for (int y = y0; y <= y1; y++)
        {
            double dy = y + 0.5 - cy;
            byte* row = dst + (long)y * size * 4;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                double d2 = dx * dx + dy * dy;
                if (d2 > ro2 || d2 < ri2) continue;
                BlendPixel(row + x * 4, pa, pb, pc, A);
            }
        }
    }

    /// <summary>
    /// 扇形（角度為度，畫面座標：0°=正右、順時針）。
    ///
    /// **踩到的坑（兩個層次）**：<c>Math.Atan2</c> 回傳 [-180,180]，而扇區是以
    /// 「正上方起算、順時針」表示，範圍本身也會跨過 0°。
    ///   1. 直接 <c>deg &gt;= a0 &amp;&amp; deg &lt;= a1</c>：第三個扇區（152.5°..267.5°）被 atan2
    ///      回報成 -150°，永遠落在範圍外 → 整格消失（實測：sector2 是 #322820 的暗玻璃）。
    ///   2. 只把兩端各自正規化也不夠：第一個扇區是 272.5°..357.5° 再接 0°..27.5°，
    ///      正規化後變成 lo=272.5 &gt; hi=27.5 的空區間 → 換成第一格消失。
    /// 正解：不要比絕對角。把「像素角 - 起始角」取模 360 得到相對角，再跟跨度比。
    /// </summary>
    private static unsafe void BlendPie(byte* dst, int size, double cx, double cy,
                                        double radius, double a0Deg, double a1Deg,
                                        (byte R, byte G, byte B) c, double alpha)
    {
        int A = (int)Math.Round(alpha * 255.0);
        if (A <= 0) return;
        int pa = (c.R * A + 127) / 255, pb = (c.G * A + 127) / 255, pc = (c.B * A + 127) / 255;
        double span = Norm360(a1Deg - a0Deg);
        double r2 = radius * radius;
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        int y1 = Math.Min(size - 1, (int)Math.Ceiling(cy + radius));
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        int x1 = Math.Min(size - 1, (int)Math.Ceiling(cx + radius));
        for (int y = y0; y <= y1; y++)
        {
            double dy = y + 0.5 - cy;
            byte* row = dst + (long)y * size * 4;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                if (dx * dx + dy * dy > r2) continue;
                double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (Norm360(deg - a0Deg) > span) continue;
                BlendPixel(row + x * 4, pa, pb, pc, A);
            }
        }
    }

    /// <summary>髮絲線：半徑 0.5 的圓沿線掃過去（約 1 px 寬，帶反鋸齒）。</summary>
    private static unsafe void BlendLine(byte* dst, int size,
                                         double x0, double y0, double x1, double y1,
                                         (byte R, byte G, byte B) c, double alpha)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int steps = (int)Math.Ceiling(len * 2);
        for (int s = 0; s <= steps; s++)
        {
            double t = steps == 0 ? 0 : (double)s / steps;
            BlendDisc(dst, size, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, 0.5, c, alpha);
        }
    }

    private static double Norm360(double deg)
    {
        double d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>SourceOver 疊一個 premultiplied 像素（dst = src + dst*(1-a)）。</summary>
    private static unsafe void BlendPixel(byte* p, int pa, int pb, int pc, int A)
    {
        int inv = 255 - A;
        p[0] = (byte)(pc + (p[0] * inv + 127) / 255);
        p[1] = (byte)(pb + (p[1] * inv + 127) / 255);
        p[2] = (byte)(pa + (p[2] * inv + 127) / 255);
        p[3] = (byte)(A + (p[3] * inv + 127) / 255);
    }

    // ------------------------------------------------------------------
    private static void SaveFrames(int ox, int oy, byte[] blurredBackground, byte[] rawBackground,
                                   string blurredPath, string noblurPath)
    {
        var co = Precompute();
        using (var f = ComposeSingle(blurredBackground, co))
            SaveFrame(f, blurredBackground, blurredPath);   // 模糊版：底也是模糊後的桌布
        // 對照組：同一張抓下來的圖、其餘完全相同，只是**不模糊**
        using (var f = ComposeSingle(rawBackground, co))
            SaveFrame(f, rawBackground, noblurPath);

        Console.WriteLine($"[dump] wrote {blurredPath}");
        Console.WriteLine($"[dump] wrote {noblurPath}");
        Console.WriteLine($"[dump] capture origin=({ox},{oy}), size={CaptureSize}x{CaptureSize}");
    }

    /// <summary>
    /// 存檔。
    ///
    /// **踩到的坑（第一版做錯，害整張圖失去意義）**：第一版把 ARGB 壓到一個**純色底**
    /// （#202226）再存檔，想說「不然透明區在檢視器裡會變一團黑」。結果適得其反：
    /// 輪盤的整個重點就是「底下桌布透過玻璃看得見」，把桌布換成純色之後，
    /// 玻璃裡那一層灰就**只是灰**——視覺模型直接判定「這是一顆不透明的實心圓盤」，
    /// 整張圖完全無法用來回答「像不像毛玻璃」。
    ///
    /// 正確做法：把**同一張抓下來的真實桌布**當底，再把輪盤疊上去。
    /// 這樣透明區看到真的桌面、玻璃區看到被壓暗的桌面，人眼才有東西可以判斷。
    /// </summary>
    private static void SaveFrame(System.Drawing.Bitmap overlay, byte[] background, string path)
    {
        using var flat = new System.Drawing.Bitmap(overlay.Width, overlay.Height,
                                                   System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(flat))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(ToBitmap(background), 0, 0);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.DrawImageUnscaled(overlay, 0, 0);
        }
        flat.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>把抓下來的原始 BGRA（alpha 固定 255）包成一張 Bitmap。</summary>
    private static System.Drawing.Bitmap ToBitmap(byte[] bgra)
    {
        var bmp = new System.Drawing.Bitmap(CaptureSize, CaptureSize,
                                           System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, CaptureSize, CaptureSize),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* dst = (byte*)data.Scan0;
            for (int y = 0; y < CaptureSize; y++)
            {
                byte* row = dst + (long)y * data.Stride;
                for (int x = 0; x < CaptureSize; x++)
                {
                    int i = y * CaptureSize + x;
                    row[x * 4 + 0] = bgra[i * 4 + 0];
                    row[x * 4 + 1] = bgra[i * 4 + 1];
                    row[x * 4 + 2] = bgra[i * 4 + 2];
                    row[x * 4 + 3] = 255;
                }
            }
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    /// <summary>
    /// 存預覽圖。<paramref name="background"/> 為 null 時改用棋盤格，
    /// 這樣「哪裡是透明的」一目了然（這是唯一能讓透明區在 PNG 裡看得見的辦法）。
    /// </summary>
    private static void SaveFrameRaw(System.Drawing.Bitmap overlay, byte[]? background, string path)
    {
        using var flat = new System.Drawing.Bitmap(overlay.Width, overlay.Height,
                                                   System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(flat))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            if (background != null)
            {
                g.DrawImageUnscaled(ToBitmap(background), 0, 0);
            }
            else
            {
                // 棋盤格：亮/暗灰 16 px 方格
                using var light = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 190, 190, 190));
                using var dark = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 130, 130, 130));
                for (int y = 0; y < overlay.Height; y += 16)
                    for (int x = 0; x < overlay.Width; x += 16)
                        g.FillRectangle(((x / 16 + y / 16) & 1) == 0 ? light : dark, x, y, 16, 16);
            }
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.DrawImageUnscaled(overlay, 0, 0);
        }
        flat.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // ------------------------------------------------------------------
    // 抓游標附近的實際座標（題目要求），並 clamp 進虛擬螢幕
    // ------------------------------------------------------------------
    private static (int X, int Y, int ScreenW, int ScreenH) PickCaptureOrigin()
    {
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        int sx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int sy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; sx = 0; sy = 0; }

        (int mx, int my) = CursorPos;
        // 與正式輪盤同一條規則：游標 +18，並且要讓 330 的框整個留在螢幕內
        int x = mx + 18;
        int y = my + 18;
        x = Math.Clamp(x, sx, Math.Max(sx, sx + sw - CaptureSize));
        y = Math.Clamp(y, sy, Math.Max(sy, sy + sh - CaptureSize));
        return (x, y, sw, sh);
    }

    /// <summary>
    /// 對照圖用：挑一個**有內容**的擷取區域。
    ///
    /// 為什麼需要：抓游標附近常常剛好抓到一大片深色桌布，玻璃底下什麼都沒有，
    /// 這時候「像不像毛玻璃」根本無從判斷（視覺模型就是這樣抱怨的：
    /// 「frosted glass is defined by what you can faintly see through it」）。
    /// 這裡掃描幾個候選區域，挑彩色變異數最大的那個，確保對照圖裡玻璃底下真的有東西。
    /// 只影響 --demo-bg 這條路徑，不影響正式量測。
    /// </summary>
    private static (int X, int Y, int ScreenW, int ScreenH) PickContentRichOrigin()
    {
        int sw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int sh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        int sx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int sy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; sx = 0; sy = 0; }

        var candidates = new List<(int X, int Y)>();
        for (int i = 1; i <= 4; i++)
            for (int j = 1; j <= 3; j++)
            {
                int x = sx + sw * i / 5 - CaptureSize / 2;
                int y = sy + sh * j / 4 - CaptureSize / 2;
                candidates.Add((Math.Clamp(x, sx, Math.Max(sx, sx + sw - CaptureSize)),
                                Math.Clamp(y, sy, Math.Max(sy, sy + sh - CaptureSize))));
            }

        (int X, int Y) best = candidates[0];
        double bestScore = -1;
        foreach (var (cx, cy) in candidates)
        {
            using var probe = new ScreenCapture(cx, cy, CaptureSize);
            probe.Grab();
            double score = probe.LumaVariance();
            if (score > bestScore) { bestScore = score; best = (cx, cy); }
        }
        Console.WriteLine($"demo-bg: chose ({best.X},{best.Y}) with luma-variance {bestScore:F1}");
        return (best.X, best.Y, sw, sh);
    }

    private static (int X, int Y) CursorPos
    {
        get { Native.GetCursorPos(out Native.POINT p); return (p.X, p.Y); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    // ------------------------------------------------------------------
    // --show：唯一會出現可見視窗的路徑，必須明示旗標才會走。
    // ------------------------------------------------------------------
    private static void ShowWindow(string pngPath)
    {
        var form = new System.Windows.Forms.Form
        {
            Text = "GlassSpike (--show)",
            ClientSize = new System.Drawing.Size(CaptureSize, CaptureSize),
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(0, 0),
            BackColor = System.Drawing.Color.FromArgb(32, 34, 38),
            KeyPreview = true
        };
        using var bmp = new System.Drawing.Bitmap(pngPath);
        form.Paint += (_, e) => e.Graphics.DrawImageUnscaled(bmp, 0, 0);
        form.KeyDown += (_, _) => form.Close();
        System.Windows.Forms.Application.Run(form);
    }
}
