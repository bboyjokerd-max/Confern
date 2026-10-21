using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Sidecar;

/// <summary>
/// 輪盤的**唯一繪製者**（規格 §9.1）。它把狀態畫成一張 ARGB 點陣圖，交給 <see cref="VisualLayer"/> 呈現。
///
/// 契約：
///   - **不判斷副檔名、不知道格式矩陣**——目標格式清單由外部傳入（M1c 的 <c>CodecRegistry</c> 才是格式知識來源）
///   - **不決定收起**——收起一律由 <see cref="ReleaseGuard"/> 決定
///   - 不與 <see cref="HitLayer"/> 直接互動
///
/// 視覺＝方向 **D「冷鋼薄荷・對比加強」**（2026-10-01 拍板）。設計權杖在 <see cref="WheelDesign"/>，
/// 玻璃快照在 <see cref="GlassSurface"/>，幾何在 <see cref="WheelGeometry"/>。本檔只負責**畫**。
///
/// ⚠ **alpha 語意紅線**（實測踩過，見 `docs/aegis/work/2026-10-01-glass-spike/91-round2-d-palette.md` §1）：
/// alpha &lt; 255 的像素，RGB 不可含背景。本檔以**建構方式**保證這件事：
///   1. 圓盤外的柔暈／投影 ⇒ 畫在透明點陣圖上的純圖層（只有顏色與 alpha，沒有背景）
///   2. 圓盤本體 ⇒ 先用**不透明的**模糊桌布填滿路徑，再疊**不透明的**薄罩 ⇒ 該區域 alpha 恆為 255
/// 不要為了「省一點透明度」把背景畫到圓盤外的圖層上，那會讓薄罩被 DWM 再合成一次而精確抵銷。
/// </summary>
internal sealed class WheelPresenter
{
    private readonly Evidence _evidence;
    private readonly VisualLayer _layer;

    private string _sourceFormat = "?";
    private IReadOnlyList<string> _targets = Array.Empty<string>();
    private Func<int, string?>? _preview;
    private Func<int, string?>? _caption;

    /// <summary>
    /// 中央膠囊左右各能伸到哪裡（px，相對輪心的半寬）。由 DrawWheel 的標籤迴圈
    /// **實際量出來的文字內緣**寫入，DrawCapsule 讀它當寬度上限。
    /// 預設 400＝實質無上限（沒有標籤擋路的情況，例如四扇區的格式層）。
    /// </summary>
    private float _capClearHalfPx = 400f;

    /// <summary>這一輪挑出來的膠囊字級（0＝還沒算）。每輪重置，見 CapFontFor。</summary>
    private float _capFitFontPx;
    private IReadOnlyList<ToolIcon>? _icons;
    private WheelMode _mode = WheelMode.Format;
    private string? _notice;
    private int _selected;
    private int _cursorX, _cursorY;
    private int _originX, _originY;      // 目前的視窗原點（滑鼠命中要用它反推圓心）
    private MonitorEntry? _monitor;

    /// <summary>玻璃底材。**每輪拖曳只抓一次**，顯示期間的重畫全部重用（抓一次＝一個 vblank，16.67 ms）。</summary>
    private GlassSurface? _glass;

    /// <summary>底材來源：`screen`（預設，抓真實桌面）／`dark`／`light`（合成桌布，供可重現的驗收）。</summary>
    private readonly string _backdrop;

    /// <summary>來源檔案的縮圖（穿透中心鏤空）。**由呼叫端擁有**，這裡只讀不釋放；null 時畫佔位圖。</summary>
    private Bitmap? _sourceThumbnail;

    /// <summary>
    /// 輪盤的兩層（規格 §9.4:390 的工具輪盤擴充點）。
    /// <see cref="Format"/>＝M1 的既有行為，**一行都不變**；<see cref="Tool"/>＝M3，
    /// 觸發鍵改為「觸發鍵 ＋ Ctrl」。
    ///
    /// 這只是一個模式旗標：**幾何（<see cref="WheelGeometry"/>）、配色（<see cref="WheelDesign"/>）、
    /// 命中測試（<c>SelectAt</c>）兩層完全共用**——第二層不是第二顆輪盤，是同一顆換一組扇區。
    /// </summary>
    internal enum WheelMode
    {
        /// <summary>格式輪盤：扇區是目標格式，圓心顯示「原格式 → 目標格式」。</summary>
        Format,

        /// <summary>工具輪盤：扇區是工具（圖示在上、文字在下），圓心顯示當前 hover 的動作。</summary>
        Tool
    }

    public bool IsVisible => _layer.IsVisible;

    /// <summary>
    /// 目前畫的是哪一層（M3）。提交端要靠它決定「這一輪的選擇是格式還是工具」——
    /// 兩層的提交走同一條 worker 路徑，只有 op 不同（見 <see cref="CommitWheel"/>）。
    /// </summary>
    public WheelMode Mode => _mode;

    public int Selected => _selected;
    public string? SelectedTarget => _targets.Count > 0 ? _targets[_selected] : null;

    /// <summary>目前這一格會顯示的預覽文字（與 <c>Compose</c> 用的是同一個來源）。</summary>
    public string? CurrentPreview => ComposePreviewText();

    /// <summary>
    /// 輸出目的地的**資料夾名**（規格 §9.5，2026-10-05 使用者裁定）。非空時，檔名預覽會接上
    /// <c>→ 名字</c>；<b>null 或空＝輸出與來源檔同一個資料夾（預設值），此時畫面與改動前逐位元相同</b>
    /// ——這是這一條的回歸判準，所以判斷「該不該顯示」**不在這裡**：由呼叫端（知道設定與來源路徑）
    /// 決定，這裡只負責畫。
    ///
    /// 只放資料夾名而不是全路徑：輪盤上塞不下路徑（規格 §9.5 原文）；全路徑寫進日誌。
    /// 與 <see cref="TriggerKeyLabel"/> 同一種作法：由 <see cref="Program"/> 在顯示前設定一次。
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// 檔名預覽那一行的**唯一**組裝點（畫面上、日誌裡、<see cref="CurrentPreview"/> 都用它）。
    /// 三個地方各組一次的話，遲早會出現「日誌說有目的地、畫面沒有」這種查不出來的分歧。
    ///
    /// 拒絕狀態（<see cref="_notice"/>）一律原樣回傳：那一行說的是「為什麼不能做」，
    /// 夾帶目的地的話，m1b 對拒絕訊息的斷言與使用者的理解都會變模糊。
    /// </summary>
    private string? ComposePreviewText()
    {
        if (_notice != null) return _notice;

        string? line = _preview?.Invoke(_selected);
        if (string.IsNullOrEmpty(Destination)) return line;         // 預設：來源檔旁邊，不加任何字

        return string.IsNullOrEmpty(line) ? $"→ {Destination}" : $"{line} → {Destination}";
    }

    /// <summary>非空代表目前是**拒絕狀態**（例如混合選取）：顯示訊息、沒有可執行的選項。</summary>
    public string? Notice => _notice;

    /// <summary>規格 §4.3：拒絕狀態下 Enter 不執行，只允許 Esc。</summary>
    public bool CanCommit => _notice == null && _targets.Count > 0;

    /// <summary>鍵帽上顯示的按鍵名（預設空白鍵）。由 <see cref="Program"/> 依實際觸發鍵設定。</summary>
    public string TriggerKeyLabel { get; set; } = "space";

    /// <summary>診斷：最近一次玻璃底材的來源描述（null＝沒有底材，走純色 fallback）。</summary>
    public string? GlassSource => _glass?.Source;

    public WheelPresenter(Evidence evidence, VisualLayer layer, string backdrop = "screen")
    {
        _evidence = evidence;
        _layer = layer;
        _backdrop = backdrop;
    }

    /// <summary>
    /// 在游標處顯示輪盤。<paramref name="notice"/> 非空時取代檔名預覽（例如混合選取拒絕）；
    /// <paramref name="preview"/> 以「當前選中索引」為輸入，因此切換扇區時輸出檔名會跟著更新。
    ///
    /// 2026-10-05（M3 工具輪盤）：新增 <paramref name="mode"/> 與 <paramref name="caption"/>。
    /// **第二層只是同一顆輪盤換一組扇區**——<see cref="WheelGeometry"/> 早就對扇區數泛化，
    /// 所以這裡不新增任何幾何或色票；只有兩處不同：
    /// ①工具扇區是「圖示在上、文字在下」（格式扇區只有一行字）；
    /// ②圓心膠囊顯示**當前 hover 的工具動作**（格式輪盤顯示「原格式 → 目標格式」）。
    /// </summary>
    public void Show(int cursorX, int cursorY, string sourceFormat, IReadOnlyList<string> targets,
                     int selected = 0, Func<int, string?>? preview = null, string? notice = null,
                     MonitorEntry[]? monitors = null, Bitmap? sourceThumbnail = null,
                     WheelMode mode = WheelMode.Format, Func<int, string?>? caption = null,
                     IReadOnlyList<ToolIcon>? icons = null)
    {
        _cursorX = cursorX;
        _cursorY = cursorY;
        _sourceFormat = sourceFormat;
        _targets = targets;
        _selected = targets.Count > 0 ? Math.Clamp(selected, 0, targets.Count - 1) : 0;
        _preview = preview;
        _caption = caption;
        _icons = icons;
        _mode = mode;
        // 圖示的計數每一輪從零開始（見 DrawnToolIcons）：留著上一輪的數字會讓「這一輪少畫一個」
        // 看起來像正常——那正是這個計數要防的那種靜默錯誤。
        _iconsDrawn = 0;
        _iconsDrawnLabels.Clear();
        _notice = notice;
        _sourceThumbnail = sourceThumbnail;

        var all = monitors ?? MonitorLayout.Enumerate();
        _monitor = MonitorLayout.Nearest(cursorX, cursorY, all);

        // ⚠ 順序是契約：**先抓玻璃，再顯示**。視覺層一旦可見，抓到的就是輪盤自己
        //   （分層視窗會出現在畫面上），而且這個錯誤不會拋例外，只會讓畫面「有點怪」。
        RefreshGlass();

        using var bmp = Compose(out int ox, out int oy);
        _layer.Show(ox, oy, bmp);

        // 記下來給滑鼠命中用（SelectAt 需要由視窗原點反推圓心）
        _originX = ox;
        _originY = oy;

        // ⚠ mode= 只能**附加在行尾**：S7.8 的斷言比對 'wheel shown cursor='，插在中間會直接弄紅它。
        _evidence.Line($"wheel shown cursor=({cursorX},{cursorY}) origin=({ox},{oy}) " +
                       $"size={bmp.Width}x{bmp.Height} source={_sourceFormat} " +
                       $"targets=[{string.Join(",", _targets)}] selected={_selected} " +
                       $"notice={(notice ?? "-")} preview={(ComposePreviewText() ?? "-")} " +
                       $"mode={(_mode == WheelMode.Tool ? "tool" : "format")}");
    }

    /// <summary>
    /// 取得這一輪的玻璃底材。**每輪只做一次**，而且必須在視覺層顯示前呼叫。
    ///
    /// 為什麼不是「非同步事後補」：抓螢幕要 16.67 ms（一個 vblank，與面積無關），
    /// 但非同步的抓取會在視覺層已經顯示之後才執行，拍到的就是輪盤自己。
    /// 所以改成同步一次、只付一次（首次顯示仍在 100 ms 預算內，見計畫 §4.1）。
    /// </summary>
    private void RefreshGlass()
    {
        _glass?.Dispose();
        _glass = null;

        int w = WheelGeometry.BitmapWidthPx;
        int h = WheelGeometry.BitmapHeightPx;

        if (string.Equals(_backdrop, "dark", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_backdrop, "light", StringComparison.OrdinalIgnoreCase))
        {
            _glass = GlassSurface.Synthetic(_backdrop, w, h);
        }
        else
        {
            var work = _monitor?.Work ?? new Native.RECT { Left = 0, Top = 0, Right = w * 4, Bottom = h * 4 };
            var (wx, wy) = WheelGeometry.FlipEdge(_cursorX, _cursorY, work);
            var (ox, oy) = WheelGeometry.WindowOrigin(wx, wy);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _glass = GlassSurface.CaptureFromScreen(ox, oy, w, h);
            clock.Stop();
            if (_glass == null)
                _evidence.Line("glass capture FAILED -> solid fallback (DRM/safe desktop/no interactive desktop)");
            else
                _evidence.Line($"glass captured in {clock.Elapsed.TotalMilliseconds:F1} ms " +
                               "(one-off per drag; every repaint reuses this surface)");
        }

        if (_glass != null)
            _evidence.Line($"glass ready source={_glass.Source} meanLuma={_glass.MeanLuma:F0} " +
                           $"glow={(WheelDesign.IsLightBackdrop(_glass.MeanLuma) ? "teal(light backdrop)" : "mint(dark backdrop)")}");
    }

    /// <summary>環狀切換選中扇區並重畫（規格 §4.3：←→↑↓）。</summary>
    public int SelectNext(int delta)
    {
        if (_targets.Count == 0) return -1;
        _selected = ((_selected + delta) % _targets.Count + _targets.Count) % _targets.Count;
        Repaint();
        _evidence.Line($"wheel selection changed -> index={_selected} target={_targets[_selected]}");
        return _selected;
    }

    /// <summary>
    /// 滑鼠所在位置對應的扇區 → 直接改變選取（規格 §4.3：扇區可直接點擊）。
    ///
    /// 拖曳途中座標只有 OLE `IDropTarget::DragOver` 拿得到（拖放迴圈會吃掉滑鼠訊息），
    /// 所以這個方法是那條路徑的落點。回傳 -1 表示「游標不在任何扇區上 → 維持原選取」：
    /// 中心圓盤與輪盤外都屬於這一類，否則游標經過中心就會把選取跳掉。
    /// </summary>
    public int SelectAt(int screenX, int screenY)
    {
        if (_targets.Count == 0) return -1;

        var (cx, cy) = CenterOnScreen;
        double dx = screenX - cx;
        double dy = screenY - cy;

        if (!WheelGeometry.Inside(dx, dy)) return -1;
        if (Math.Sqrt(dx * dx + dy * dy) <= WheelGeometry.CenterRadiusPx) return -1;

        int idx = WheelGeometry.SectorAt(dx, dy, _targets.Count);
        if (idx < 0 || idx >= _targets.Count) return -1;
        if (idx == _selected) return idx;

        _selected = idx;
        Repaint();
        // 刻意留在 Line（不降級成 Detail）：拖曳時這行確實會反覆出現，但驗收腳本 m1b S8.9 斷言它
        // ≥1 次，而腳本預設跑在診斷關閉之下——降級會讓那條斷言在設定完全正常的情況下紅燈。
        _evidence.Line($"wheel hover selection -> index={idx} target={_targets[idx]} cursor=({screenX},{screenY})");
        return idx;
    }

    /// <summary>目前輪盤圓心在螢幕上的位置（驗證與診斷用；不參與繪製決策）。</summary>
    public (int X, int Y) CenterOnScreen
    {
        get
        {
            int c = WheelGeometry.CenterInBitmapPx;
            return (_originX + c, _originY + c);
        }
    }

    public void Hide()
    {
        if (!_layer.IsVisible) return;
        _layer.Hide();
        _evidence.Line("wheel hidden");
    }

    /// <summary>釋放底材。輪盤收起後就沒有理由留著一張模糊桌布（約 0.5 MB）。</summary>
    public void ReleaseGlass()
    {
        _glass?.Dispose();
        _glass = null;
    }

    /// <summary>
    /// 把當前這一幀寫成 PNG（診斷用：**不截使用者螢幕**，只存本程式自己畫的點陣圖）。
    /// 輪盤本身是透明的，直接存 ARGB 在很多檢視器上會看不出內容，
    /// 因此合成到中性底色上再存（alpha 資訊在 `Show` 的路徑才有效，這裡是給人看的）。
    /// </summary>
    public void DumpTo(string path)
    {
        using var frame = Compose(out _, out _);
        using var flat = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.FromArgb(255, 32, 34, 38));
            g.DrawImageUnscaled(frame, 0, 0);
        }
        flat.Save(path, ImageFormat.Png);
        _evidence.Line($"wheel frame dumped to {path} ({flat.Width}x{flat.Height}, flattened onto #202226)");
    }

    /// <summary>
    /// 同上，但**保留 alpha**。這是驗 alpha 語意紅線唯一的方式：
    /// 扁平化之後「圓盤本體是否 alpha=255」「圓盤外是否為純圖層」都看不出來，
    /// 而這兩件事正是這個設計能被正確合成的全部理由（見類別註解）。
    /// </summary>
    public void DumpRawTo(string path)
    {
        using var frame = Compose(out _, out _);
        frame.Save(path, ImageFormat.Png);
        _evidence.Line($"wheel frame (raw ARGB, alpha preserved) dumped to {path} " +
                       $"({frame.Width}x{frame.Height})");
    }

    private void Repaint()
    {
        using var bmp = Compose(out int ox, out int oy);
        _layer.Show(ox, oy, bmp);
    }

    private Bitmap Compose(out int originX, out int originY)
    {
        int w = WheelGeometry.BitmapWidthPx;
        int h = WheelGeometry.BitmapHeightPx;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // ClearType 是次像素渲染、不寫 alpha；在 layered window 上會出現黑邊。必須用灰階反鋸齒。
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var work = _monitor?.Work ?? new Native.RECT { Left = 0, Top = 0, Right = w * 4, Bottom = h * 4 };
        var (wheelX, wheelY) = WheelGeometry.FlipEdge(_cursorX, _cursorY, work);
        (originX, originY) = WheelGeometry.WindowOrigin(wheelX, wheelY);

        float c = WheelGeometry.CenterInBitmapPx;
        float r = WheelGeometry.DiameterPx / 2f;

        // 玻璃（柔暈＋投影＋圓盤本體）。扇區、文字、膠囊都疊在它上面。
        DrawGlass(g, c, c, r);

        // 鬼影卡畫在玻璃**之後**：它落在圓盤外，但會與柔暈重疊，
        // 若先畫就會被柔暈染上一層薄荷／深青（卡片是前景 UI，不該被背景暈影響）。
        DrawGhostCard(g, WheelGeometry.CursorInBitmapPx, WheelGeometry.CursorInBitmapPx);

        if (_targets.Count == 0) DrawEmptyWheel(g, c, c, r);
        else DrawWheel(g, c, c, r);

        DrawCenter(g, c, c);
        DrawKeycap(g, c, c + r);
        DrawPreview(g, c, c + r);
        return bmp;
    }

    // ==================================================================
    // 玻璃：柔暈／投影（純圖層）＋ 圓盤本體（不透明）
    // ==================================================================

    private void DrawGlass(Graphics g, float cx, float cy, float r)
    {
        double luma = _glass?.MeanLuma ?? 0.0;

        // 1) 柔暈（由外而內疊加）。強度與厚度都是**實測設計稿**得來的值（見 WheelDesign 的註記）。
        //    從盤緣外 GlowStartPx 才開始，否則光暈會貼著玻璃邊變成一條亮環。
        //    WheelDesign.GlowEnabled = false 時整個不畫（2026-10-01 實機排除法用）。
        if (WheelDesign.GlowEnabled)
        {
            Color glow = WheelDesign.Glow(luma);
            double rimAcc = WheelDesign.GlowRimAlpha(luma) / 255.0;
            DrawSoftRing(g, cx, cy, r + WheelDesign.GlowStartPx,
                         WheelDesign.GlowSpanPx, WheelDesign.GlowRings, rimAcc, glow);
        }

        // 2) 柔投影（讓玻璃浮起來，而不是貼在桌布上）。淺色桌布要更重。
        int shadowMax = WheelDesign.ShadowMax(luma);
        DrawSoftRing(g, cx, cy, r, 26.0, 13, shadowMax / 255.0, Color.Black);

        // 3) 圓盤本體：**不透明**。順序＝先鋪模糊桌布、再疊薄罩，兩者都不透明 ⇒ alpha 恆為 255。
        using var disc = new GraphicsPath();
        disc.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        if (_targets.Count > 0 && _selected >= 0 && _selected < _targets.Count)
        {
            // 選中扇區會凸出圓盤 4px（原型如此）。圓盤判定必須包含它，否則整條描邊會被裁掉。
            float rr = r + WheelDesign.SelExpandPx;
            double step = 360.0 / _targets.Count;
            double sweep = WheelDesign.SweepDeg(_targets.Count);
            float start = (float)(WheelGeometry.StartDeg + _selected * step + (step - sweep) / 2.0);
            disc.AddPath(RoundedSectorPath(cx, cy, rr, start, sweep, WheelDesign.SectorCornerPx), false);
        }

        if (_glass != null)
        {
            using var tex = new TextureBrush(_glass.Blurred, WrapMode.Clamp);
            g.FillPath(tex, disc);
        }
        else
        {
            using var solid = new SolidBrush(Color.FromArgb(255, 16, 19, 24));
            g.FillPath(solid, disc);
        }
        using (var veil = new SolidBrush(WheelDesign.Veil))
            g.FillPath(veil, disc);
    }

    /// <summary>
    /// 由外而內疊出柔和的環狀漸層。<paramref name="spanPx"/> 是衰減的厚度，
    /// <paramref name="maxAcc"/> 是**在盤緣（距離 0）的累積不透明度**（0–1）。
    /// 每一圈的不透明度由「目標累積值」反推，所以疊出來的曲線與設計值一致，不會愈疊愈濃；
    /// 最內一圈畫在距離 0（＝圓盤本身，會被不透明的玻璃蓋掉，只是讓累積值在盤緣剛好等於
    /// <paramref name="maxAcc"/>）。目標值隨距離二次衰減。
    /// </summary>
    private static void DrawSoftRing(Graphics g, float cx, float cy, float r, double spanPx,
                                     int steps, double maxAcc, Color colour)
    {
        if (spanPx <= 0 || steps <= 0 || maxAcc <= 0) return;
        double acc = 0;
        for (int j = steps; j >= 0; j--)
        {
            double d = spanPx * j / steps;
            double t = 1.0 - d / spanPx;
            double target = maxAcc * t * t;
            if (target <= acc) continue;
            int alpha = Math.Clamp((int)Math.Round((target - acc) / (1.0 - acc) * 255), 0, 255);
            if (alpha == 0) continue;
            float rr = r + (float)d;
            using var brush = new SolidBrush(Color.FromArgb(alpha, colour));
            g.FillEllipse(brush, cx - rr, cy - rr, rr * 2, rr * 2);
            // 記帳要用**實際畫出的整數 alpha**：用未四捨五入的值記帳，
            // 十幾圈累積下來總量會短少一到兩成（實測盤緣 52 只畫出 40）。
            acc = 1.0 - (1.0 - acc) * (1.0 - alpha / 255.0);
        }
    }

    // ==================================================================
    // 扇區與標籤
    // ==================================================================

    /// <summary>
    /// 「外緣尖點帶圓角」的扇形路徑（由圓心往外畫），取代 FillPie／DrawPie 的直角尖點。
    ///
    /// 圓角＝同時與**外圓內切**、與**徑向邊相切**的圓弧。推導：圓角圓心必須距輪心 (ro−cr)
    /// （內切）且距徑向邊 cr（相切），所以它偏離徑向邊的角度 α = asin(cr/(ro−cr))；
    /// 尖點兩邊夾 90°，故圓弧掃角固定是 90°+α。
    /// a0 側：從徑向邊上的切點掃到外圓切點（起角 a0−90°）；
    /// 外圓弧只畫中間 (a0+α .. a1−α)；a1 側：從外圓切點掃到徑向邊上的切點（起角 a1−α）。
    /// 三段在切點上精確接合（GDI+ 的 AddArc 會自動補上零長度的連接線），不會有縫也不會重疊。
    ///
    /// 內側不做圓角：扇形一路填到圓心，半徑 47 以內由來源縮圖圓孔蓋住（<see cref="DrawCenter"/>
    /// 在 DrawWheel 之後才畫）。
    /// </summary>
    private static GraphicsPath RoundedSectorPath(float cx, float cy, float ro, double startDeg,
                                                  double sweepDeg, float cornerPx)
    {
        var path = new GraphicsPath();
        double a0 = startDeg, a1 = startDeg + sweepDeg;
        double cr = Math.Min(cornerPx, ro * 0.25);          // 半徑很小（填描邊內緣）時自動退化
        if (cr < 0.5 || sweepDeg <= 0.0)
        {
            path.AddPie(cx - ro, cy - ro, ro * 2, ro * 2, (float)a0, (float)sweepDeg);
            return path;
        }
        double rc = ro - cr;
        double alpha = Math.Asin(Math.Min(1.0, cr / rc)) * 180.0 / Math.PI;
        double Rad(double deg) => deg * Math.PI / 180.0;
        PointF At(double deg, double rad) => new PointF(
            cx + (float)(Math.Cos(Rad(deg)) * rad), cy + (float)(Math.Sin(Rad(deg)) * rad));
        void CornerArc(double centreDeg, double startAngle)
        {
            PointF c = At(centreDeg, rc);
            path.AddArc(c.X - (float)cr, c.Y - (float)cr, (float)(cr * 2), (float)(cr * 2),
                        (float)startAngle, (float)(90.0 + alpha));
        }

        CornerArc(a0 + alpha, a0 - 90.0);
        path.AddArc(cx - ro, cy - ro, ro * 2, ro * 2,
                    (float)(a0 + alpha), (float)(sweepDeg - 2.0 * alpha));
        CornerArc(a1 - alpha, a1 - alpha);
        path.AddLine(At(a1, rc * Math.Cos(Rad(alpha))), new PointF(cx, cy));
        path.CloseFigure();
        return path;
    }

    private void DrawWheel(Graphics g, float cx, float cy, float r)
    {
        _capClearHalfPx = 400f;   // 每輪重算：沒有標籤擋路時回到實質無上限
        _capFitFontPx = 0f;
        int count = _targets.Count;
        double step = 360.0 / count;
        double sweep = WheelDesign.SweepDeg(count);
        float half = (float)((step - sweep) / 2.0);

        // ① 外緣兩條髮絲線。**必須畫在扇區之前**：原型是 SVG 疊在玻璃之上，
        //    不透明的選中扇區會蓋掉外緣線；畫在之後會橫切選中扇區，
        //    與它的描邊之間夾一道填色細縫，看起來像「兩條線」（實測踩過）。
        using (var rim = new Pen(WheelDesign.Rim, WheelDesign.RimPx))
            g.DrawEllipse(rim, cx - r + WheelDesign.RimPx / 2f, cy - r + WheelDesign.RimPx / 2f,
                          (r - WheelDesign.RimPx / 2f) * 2, (r - WheelDesign.RimPx / 2f) * 2);
        using (var hair = new Pen(WheelDesign.InnerHair, WheelDesign.InnerHairPx))
        {
            float hr = r - 3.4f;
            g.DrawEllipse(hair, cx - hr, cy - hr, hr * 2, hr * 2);
        }

        // ② 扇區
        for (int i = 0; i < count; i++)
        {
            bool sel = i == _selected;
            float start = (float)(WheelGeometry.StartDeg + i * step + half);
            if (sel)
            {
                float rr = r + WheelDesign.SelExpandPx;
                using (var pen = new Pen(WheelDesign.SelStroke, WheelDesign.SelStrokePx))
                using (var outline = RoundedSectorPath(cx, cy, rr, start, sweep, WheelDesign.SectorCornerPx))
                {
                    g.DrawPath(pen, outline);
                    // DrawPath 只畫外框：補上扇形本體（填到描邊的內緣，不要留 1px 的透明細縫）
                    using var fillBg = new SolidBrush(WheelDesign.SelFill);
                    float ri = rr - WheelDesign.SelStrokePx / 2f;
                    using var body = RoundedSectorPath(cx, cy, ri, start, sweep, WheelDesign.SectorCornerPx);
                    g.FillPath(fillBg, body);
                }
            }
            else
            {
                using var fill = new SolidBrush(i % 2 == 0 ? WheelDesign.FillA : WheelDesign.FillB);
                using var wedge = RoundedSectorPath(cx, cy, r, start, sweep, WheelDesign.SectorCornerPx);
                g.FillPath(fill, wedge);
            }
        }

        // ③ 標籤（未選中的變暗，讓選中更明確）
        using var font = MakeFontPx(WheelDesign.LabelFontPx(count));
        using var bright = new SolidBrush(WheelDesign.Label);
        using var dim = new SolidBrush(Color.FromArgb(
            (int)Math.Round(255 * WheelDesign.LabelDimOpacity), WheelDesign.Label));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float ratio = WheelDesign.LabelRadiusRatio(count);
        // 工具扇區是「**圖示在上、文字在下**」（參考版式；見 ui-redesign:51）。文字因此往下
        // 讓開圖示的高度，圖示畫在同一個扇區中線的上方。
        // **格式扇區一行都不動**：不按 Ctrl 的那條路徑必須與改動前逐位元相同（計畫 T8）。
        float iconLift = _mode == WheelMode.Tool ? WheelDesign.ToolIconPx / 2f + 2f : 0f;
        for (int i = 0; i < count; i++)
        {
            double mid = WheelGeometry.SectorMidDeg(i, count) * Math.PI / 180.0;
            float lx = cx + (float)(Math.Cos(mid) * r * ratio);
            float ly = cy + (float)(Math.Sin(mid) * r * ratio);
            var ink = i == _selected ? bright : dim;

            // 中央膠囊的橫向淨空：只把「縱向會被膠囊掃到」的標籤算進來。
            // 四扇區（格式層）的標籤落在 45° 上、垂直就閃開膠囊 ⇒ 這裡不會命中，
            // 上限維持 400（實質無上限）⇒ 格式層的膠囊一個位元都不會變。
            float capHalfH = WheelDesign.CapFontPx * 0.8f + WheelDesign.CapPadYPx + 2f;
            float labelTop = ly - 12f + iconLift;
            float labelBottom = ly + 12f + iconLift;
            if (labelTop < cy + capHalfH && labelBottom > cy - capHalfH)
            {
                float labelInkW = g.MeasureString(_targets[i], font).Width;
                _capClearHalfPx = Math.Min(_capClearHalfPx, Math.Abs(lx - cx) - labelInkW / 2f);
            }
            if (_mode == WheelMode.Tool && _icons != null && i < _icons.Count)
            {
                if (DrawToolIcon(g, _icons[i], ink, lx, ly - iconLift))
                {
                    _iconsDrawn++;
                    _iconsDrawnLabels.Add($"{_icons[i]}");
                }
            }
            g.DrawString(_targets[i], font, ink,
                         new RectangleF(lx - 40, ly - 12 + iconLift, 80, 24), format);
        }
    }

    /// <summary>
    /// 這一輪真的畫出來的工具圖示數（工具輪盤專用；<see cref="Show"/> 每次都歸零）。
    ///
    /// 為什麼要有這個計數：`DrawToolIcon` 的 <c>switch</c> **沒有 default**，所以第五顆工具
    /// 在 2026-10-07 之前是**空白圖示**——而畫面上「少一個圖示」與「那個扇區本來就沒有圖示」
    /// 從一張點陣圖上分不出來（實測 2026-10-07：對工具輪盤的 PNG 量每個扇區的墨水，五格是
    /// 513/447/613/411/433，差了 118 個像素——而**標籤字本身的墨水遠大於圖示**，
    /// 所以那個差在腳本裡挑不出一個站得住的門檻）。
    /// 這裡改成讓**真正的那個 switch** 自己回報「我有沒有畫東西」：有 default 時回 false。
    /// 計數因此與繪製同一個來源，不可能是第二份規則（比在測試裡用幾何猜位置可靠得多）。
    /// </summary>
    public int DrawnToolIcons => _iconsDrawn;

    /// <summary>畫出來的那幾個圖示（診斷輸出用，順序＝扇區順序）。</summary>
    public IReadOnlyList<string> DrawnToolIconLabels => _iconsDrawnLabels;

    private int _iconsDrawn;
    private readonly List<string> _iconsDrawnLabels = new();

    /// <summary>
    /// 工具扇區的線稿圖示。**全部用程式繪製、用既有的墨水色**（<see cref="WheelDesign.Label"/>）：
    /// 本專案沒有圖示資產，而引進一張 PNG 會同時破壞「配色唯一權威」與離線不變式。
    /// 圖示尺寸取自 <see cref="WheelDesign.ToolIconPx"/>，不在此硬編。
    ///
    /// 回傳值＝**這個圖示真的有被畫出來**（每一個 <c>case</c> 都回 true）。這不是額外的邏輯，
    /// 而是讓「少一個 case」變成當場看得見的事實，見 <see cref="DrawnToolIcons"/>。
    /// </summary>
    private static bool DrawToolIcon(Graphics g, ToolIcon icon, Brush ink, float cx, float cy)
    {
        float s = WheelDesign.ToolIconPx;
        using var pen = new Pen(ink, Math.Max(1.4f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (icon)
        {
            case ToolIcon.Rotate90:
                // 順時針 90 度的開放弧 ＋ 箭頭。弧從左側 180° 起、順時針走到 12 點方向。
                var box = new RectangleF(cx - s / 2f, cy - s / 2f, s, s);
                g.DrawArc(pen, box, 200f, -250f);
                // 箭頭接在弧的終點（約 -50° ＝右上）並指向切線方向。
                float ax = cx + (float)(Math.Cos(-50.0 * Math.PI / 180) * s / 2f);
                float ay = cy + (float)(Math.Sin(-50.0 * Math.PI / 180) * s / 2f);
                float a = s * 0.22f;
                g.DrawLine(pen, ax, ay, ax - a, ay - a * 0.2f);
                g.DrawLine(pen, ax, ay, ax + a * 0.2f, ay + a);
                return true;

            case ToolIcon.StripMetadata:
                // 標籤（tag）＋ 斜線：**被劃掉的標籤**。標籤的尖角朝左、右邊是矩形，
                // 中間一個小孔。17px 之下只有這個對比撐得住（弧箭頭與它一眼可分辨）。
                using (var tag = new GraphicsPath())
                {
                    tag.AddPolygon(new[]
                    {
                        new PointF(cx - s * 0.10f, cy - s * 0.42f),
                        new PointF(cx + s * 0.42f, cy - s * 0.42f),
                        new PointF(cx + s * 0.42f, cy + s * 0.42f),
                        new PointF(cx - s * 0.10f, cy + s * 0.42f),
                        new PointF(cx - s * 0.44f, cy),
                    });
                    tag.CloseFigure();
                    g.DrawPath(pen, tag);
                }
                // 標籤的穿孔。**直接用扇區的墨水筆刷**（不另外 new 一個 SolidBrush）：
                // 這樣選中／未選中的兩段不透明度會自動跟著走，不會多出一個要同步的顏色。
                g.FillEllipse(ink, cx - s * 0.12f, cy - s * 0.08f, s * 0.16f, s * 0.16f);
                // 斜線畫在標籤**上面**（同一支筆、同一個墨水色）：這條線就是「去掉」的意思本體。
                g.DrawLine(pen, cx - s * 0.52f, cy + s * 0.52f, cx + s * 0.52f, cy - s * 0.52f);
                return true;

            case ToolIcon.Compress:
                // 兩個相向的箭頭：上箭頭往下、下箭頭往上，箭尖在圓心相遇（＝往中間壓）。
                // 形狀全部由 s 推導（與另外兩顆同一個規矩），所以圖示尺寸一改三顆一起動。
                //
                // 實測的兩個關鍵數字（17px 之下）：箭尖相距 0.20s ≈ 3.4px，而筆寬是
                // max(1.4, s/9) ≈ 1.9px ⇒ 兩個尖端之間還留得住約 1.5px 的空隙，
                // 不會糊成一條線；再靠近就會看不出「兩支」。
                float tip = s * 0.10f;       // 兩個箭尖到圓心的距離
                float wing = s * 0.30f;      // 箭翼的橫向張開（半寬）

                // 上箭頭：柱（上）＋ 翼（往下收成箭尖）
                g.DrawLine(pen, cx, cy - s * 0.48f, cx, cy - wing);
                g.DrawLine(pen, cx - wing, cy - wing, cx, cy - tip);
                g.DrawLine(pen, cx + wing, cy - wing, cx, cy - tip);

                // 下箭頭：翼（往上收成箭尖）＋ 柱（下）
                g.DrawLine(pen, cx - wing, cy + wing, cx, cy + tip);
                g.DrawLine(pen, cx + wing, cy + wing, cx, cy + tip);
                g.DrawLine(pen, cx, cy + wing, cx, cy + s * 0.48f);
                return true;

            case ToolIcon.Crop:
                // 斜的裁切框 ＋ 兩條對角線（「這是一塊選取範圍」）。全部由 s 推導（與其他三顆同一個規矩）。
                //
                // 為什麼是斜的：正的矩形與 `Compress` 的上下對稱直線在 17px 下會撞形；
                // 傾斜約 20° 之後，四條邊的方向與另外三顆都不同，一眼可分。
                // 兩條對角線是「框內被選中」在線稿裡的通用寫法；實測在 17px、筆寬 1.9px 之下，
                // 對角線與框邊仍留得住約 3px 的空隙（框半寬 0.40s ≈ 6.8px、對角線由角往內縮 0.16s），
                // 不會糊成一團。
                float halfW = s * 0.40f, halfH = s * 0.30f, skew = s * 0.17f;
                var c1 = new PointF(cx - halfW + skew, cy - halfH);
                var c2 = new PointF(cx + halfW + skew, cy - halfH);
                var c3 = new PointF(cx + halfW - skew, cy + halfH);
                var c4 = new PointF(cx - halfW - skew, cy + halfH);
                g.DrawLine(pen, c1, c2);
                g.DrawLine(pen, c2, c3);
                g.DrawLine(pen, c3, c4);
                g.DrawLine(pen, c4, c1);
                // 兩條對角線：由角往框內縮 0.16s，免得四個端點在轉角上疊成一團墨。
                float inset = s * 0.16f;
                g.DrawLine(pen, cx - halfW + skew + inset, cy - halfH + inset,
                                cx + halfW - skew - inset, cy + halfH - inset);
                g.DrawLine(pen, cx + halfW + skew - inset, cy - halfH + inset,
                                cx - halfW - skew + inset, cy + halfH - inset);
                return true;

            case ToolIcon.ReadQr:
                // QR 的**三個尋像圖案＋右下角的資料點**（`ToolIcon.ReadQr` 的註解說明為什麼是這三個框）。
                //
                // 為什麼不是「一整塊點陣方格」：17px 之下點陣方格會糊成一塊灰，而這個專案的其他四顆
                // 都是**線稿**（見 DrawToolIcon 的類別說明）——實心塊會比另外四顆重一個級別，
                // 在同一顆輪盤上一眼就看得出來「這一顆是貼上去的」。三個框則是同一種線稿語言。
                //
                // 尺寸全部由 s 推導（與另外四顆同一個規矩）：
                //   · 三個框＝邊長 0.24s 的正方形，圓心距輪心 0.30s（左上／右上／左下）；
                //   · 右下角四個小點＝資料區的示意，放在框外圍的對角線上（0.28s 與 0.40s）。
                //
                // 實測（17px，筆寬 max(1.4, s/9) ≈ 1.89px）：框與框之間的水平淨空
                // 2*(0.30s − 0.12s) − 1.89 ≈ 4.2px；最近的那個點與框內緣相距
                // (0.28s − 0.045s) − (0.30s − 0.12s) ≈ 0.055s ≈ 0.9px，加上點自己的半徑 0.045s
                // 之後仍留得住約 1.6px——四個記號不會糊成一團（這是 Crop 那一顆同一條紀律：
                // 相鄰記號的淨空必須大於筆寬的一半）。
                float qb = s * 0.24f;          // 框的邊長
                float qd = s * 0.30f;          // 框心到輪心的距離
                float qr = qb / 2f;
                g.DrawRectangle(pen, cx - qd - qr, cy - qd - qr, qb, qb);   // 左上
                g.DrawRectangle(pen, cx + qd - qr, cy - qd - qr, qb, qb);   // 右上
                g.DrawRectangle(pen, cx - qd - qr, cy + qd - qr, qb, qb);   // 左下
                // 資料點：**實心**（與 StripMetadata 的穿孔同一種作法——直接用扇區的墨水筆刷，
                // 選中／未選中的兩段不透明度因此自動跟著走，不會多出一個要同步的顏色）。
                float dr = Math.Max(1f, s * 0.045f);
                for (int row = 0; row < 2; row++)
                {
                    for (int col = 0; col < 2; col++)
                    {
                        float dx = (0.28f + 0.12f * col) * s;
                        float dy = (0.28f + 0.12f * row) * s;
                        g.FillEllipse(ink, cx + dx - dr, cy + dy - dr, dr * 2, dr * 2);
                    }
                }
                return true;

            case ToolIcon.MergePdf:
                // 合併：**左邊兩張小頁 ＋ 一條收束的線 ＋ 右邊一張大頁**（多 → 一）。
                // 形狀為什麼是這個（以及刻意不用哪些畫法）寫在 ToolIcon.MergePdf 的註解裡，
                // 這裡只記尺寸。
                //
                // 版面（以 17px 為例）：整張圖 16.15 x 10.2 px，筆寬 max(1.4, s/9) = 1.89px。
                //   左兩張小頁  x ∈ [-0.47s,-0.05s]，半高 0.30s；**共用一條邊**（中間那條只畫一次）
                //   右一張大頁  x ∈ [+0.17s,+0.51s]，半高 0.25s（比小頁矮 ⇒ 一眼看得出誰是結果）
                //   收束線：左兩張的右緣（±0.30s）走到大頁的左緣（±0.25s）
                // 為什麼共用一條邊而不是畫兩張有間隙的紙：間隙要 > 筆寬 1.89px 才分得開，
                // 那會讓左邊那一疊吃掉 0.44s 以上，剩下的寬度不夠再放第三個框。
                // 共用邊在 17px 下讀起來是「一疊紙」，那正是要說的事。
                float mSmallH = s * 0.30f;          // 小頁半高
                float mSmallW = s * 0.21f;          // 小頁寬（兩張 ＋ 共用邊）
                float mLargeH = s * 0.25f;          // 大頁半高
                float mLargeW = s * 0.34f;          // 大頁寬
                float mLeft = cx - s * 0.47f;       // 左緣
                float mGapX = cx - s * 0.05f;       // 兩張小頁的右緣（＝收束線的起點）
                float mRight0 = cx + s * 0.17f;     // 大頁左緣

                // 左邊：兩張共用一條邊的小頁。外框畫兩次，中間那一條邊只畫一次（避免疊成兩倍粗）。
                g.DrawRectangle(pen, mLeft, cy - mSmallH, mSmallW, mSmallH * 2f);
                g.DrawRectangle(pen, mLeft + mSmallW, cy - mSmallH, mSmallW, mSmallH * 2f);
                g.DrawLine(pen, mLeft + mSmallW, cy - mSmallH, mLeft + mSmallW, cy + mSmallH);
                // 右邊：一張大頁（結果）
                g.DrawRectangle(pen, mRight0, cy - mLargeH, mLargeW, mLargeH * 2f);
                // 收束的線：上緣與下緣各一條，把兩張小頁的開口收進大頁
                g.DrawLine(pen, mGapX, cy - mSmallH, mRight0, cy - mLargeH);
                g.DrawLine(pen, mGapX, cy + mSmallH, mRight0, cy + mLargeH);
                return true;

            case ToolIcon.RemoveBg:
                // 去背：**四個角標 ＋ 一個人的剪影**（圓頭 ＋ 梯形身）。
                // 形狀為什麼是這個、以及刻意不用哪些畫法（剪刀／棋盤格／完整外框）寫在
                // ToolIcon.RemoveBg 的註解裡，這裡只記尺寸。
                //
                // 版面（以 17px 為例）：整張圖 16.15 x 16.15 px，筆寬 max(1.4, s/9) = 1.89px。
                //   角標：四個 0.16s 的 L 形，圍出 0.82s 的方框（x,y ∈ ±0.41s）
                //   頭：   圓心 (cx, cy − 0.17s)、半徑 0.115s ⇒ 頭頂在 −0.285s
                //   身：   上緣 ±0.16s（在 cy + 0.02s）、下緣 ±0.30s（在 cy + 0.41s ＝ 角標的下緣線）
                //
                // ⚠ 兩個淨空是量出來的（17px、筆寬 1.89px）：
                //   · 頭頂（−0.285s）到角標上緣線（−0.41s）：0.125s ≈ 2.1px ⇒ 兩者分得開；
                //   · 身的左右緣（下緣 ±0.30s）到角標左右線（±0.41s）：0.11s ≈ 1.9px ⇒ 剛好等於
                //     一個筆寬。**再寬一點的身就會與角標黏在一起**，所以 0.30s 是這個尺寸的上限
                //     （與 Crop／ReadQr 同一條紀律：相鄰記號的淨空必須大於筆寬的一半）。
                float rbCorner = s * 0.41f;    // 角標圍出的半寬／半高
                float rbArm = s * 0.16f;       // 每一支 L 形手臂的長度
                g.DrawLine(pen, cx - rbCorner, cy - rbCorner, cx - rbCorner + rbArm, cy - rbCorner);
                g.DrawLine(pen, cx - rbCorner, cy - rbCorner, cx - rbCorner, cy - rbCorner + rbArm);
                g.DrawLine(pen, cx + rbCorner, cy - rbCorner, cx + rbCorner - rbArm, cy - rbCorner);
                g.DrawLine(pen, cx + rbCorner, cy - rbCorner, cx + rbCorner, cy - rbCorner + rbArm);
                g.DrawLine(pen, cx - rbCorner, cy + rbCorner, cx - rbCorner + rbArm, cy + rbCorner);
                g.DrawLine(pen, cx - rbCorner, cy + rbCorner, cx - rbCorner, cy + rbCorner - rbArm);
                g.DrawLine(pen, cx + rbCorner, cy + rbCorner, cx + rbCorner - rbArm, cy + rbCorner);
                g.DrawLine(pen, cx + rbCorner, cy + rbCorner, cx + rbCorner, cy + rbCorner - rbArm);
                // 頭（圓）
                float headR = s * 0.115f;
                g.DrawEllipse(pen, cx - headR, cy - s * 0.17f - headR, headR * 2f, headR * 2f);
                // 身（梯形：肩窄、底寬——反過來會讀成「一個箱子」而不是「一個人」）
                var shoulders = new[]
                {
                    new PointF(cx - s * 0.16f, cy + s * 0.02f),
                    new PointF(cx + s * 0.16f, cy + s * 0.02f),
                    new PointF(cx + s * 0.30f, cy + s * 0.41f),
                    new PointF(cx - s * 0.30f, cy + s * 0.41f),
                };
                g.DrawLine(pen, shoulders[0], shoulders[1]);
                g.DrawLine(pen, shoulders[1], shoulders[2]);
                g.DrawLine(pen, shoulders[2], shoulders[3]);
                g.DrawLine(pen, shoulders[3], shoulders[0]);
                return true;
        }
        // 沒有任何 case 命中（＝`ToolIcon` 多了一個值而這裡忘了畫）：**大聲回報「沒畫」**。
        // 這一行取代了 2026-10-07 之前那個空的 switch 尾巴——它當時讓第五顆工具靜默地變成空白圖示。
        return false;
    }

    private void DrawEmptyWheel(Graphics g, float cx, float cy, float r)
    {
        using var brush = new SolidBrush(Color.FromArgb(120, 150, 156, 170));
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        using var rim = new Pen(WheelDesign.Rim, WheelDesign.RimPx);
        g.DrawEllipse(rim, cx - r, cy - r, r * 2, r * 2);
    }

    // ==================================================================
    // 中心：來源縮圖穿透 ＋ 白膠囊
    // ==================================================================

    private void DrawCenter(Graphics g, float cx, float cy)
    {
        float ri = (float)WheelGeometry.CenterRadiusPx;

        // 來源縮圖（真實的優先，取不到才用佔位漸層）。整個裁在圓孔內。
        var saved = g.Clip;
        using (var hole = new GraphicsPath())
        {
            hole.AddEllipse(cx - ri, cy - ri, ri * 2, ri * 2);
            g.SetClip(hole, CombineMode.Intersect);
            if (_sourceThumbnail != null)
            {
                // 置中裁切（cover）：短邊填滿圓孔，長邊置中。
                float scale = Math.Max(ri * 2 / _sourceThumbnail.Width, ri * 2 / _sourceThumbnail.Height);
                float dw = _sourceThumbnail.Width * scale, dh = _sourceThumbnail.Height * scale;
                g.DrawImage(_sourceThumbnail, cx - dw / 2, cy - dh / 2, dw, dh);
            }
            else
            {
                DrawThumbnailPlaceholder(g, cx, cy, ri);
            }
            g.Clip = saved;
        }

        using (var ring = new Pen(WheelDesign.ThumbRing, 1f))
            g.DrawEllipse(ring, cx - ri + 0.5f, cy - ri + 0.5f, (ri - 0.5f) * 2, (ri - 0.5f) * 2);

        DrawCapsule(g, cx, cy);
    }

    /// <summary>縮圖取不到時的佔位圖（漸層＋兩層山）。**只是佔位**，真實產品會放來源檔的縮圖。</summary>
    private static void DrawThumbnailPlaceholder(Graphics g, float cx, float cy, float ri)
    {
        using (var bg = new LinearGradientBrush(new RectangleF(cx - ri, cy - ri, ri * 2, ri * 2),
                                                WheelDesign.ThumbTop, WheelDesign.ThumbBottom, 90f))
            g.FillRectangle(bg, cx - ri, cy - ri, ri * 2, ri * 2);

        void Ridge(Color colour, float baseY, float peak, float width)
        {
            using var path = new GraphicsPath();
            path.AddPolygon(new[]
            {
                new PointF(cx - width, baseY),
                new PointF(cx - width * 0.35f, baseY - peak),
                new PointF(cx, baseY - peak * 0.55f),
                new PointF(cx + width * 0.45f, baseY - peak * 1.05f),
                new PointF(cx + width, baseY),
                new PointF(cx + width, cy + ri * 1.6f),
                new PointF(cx - width, cy + ri * 1.6f)
            });
            using var brush = new SolidBrush(colour);
            g.FillPath(brush, path);
        }

        // 後山較亮、前山較暗（原型的山頂正好被膠囊蓋住，這裡刻意壓低並分兩層，見 91 號文件 §8.2）
        Ridge(WheelDesign.ThumbRidgeBack, cy + ri * 0.10f, ri * 0.62f, ri * 1.5f);
        Ridge(WheelDesign.ThumbRidgeFront, cy + ri * 0.42f, ri * 0.70f, ri * 1.6f);
    }


    /// <summary>膠囊可用的總寬（px）＝左右標籤淨空 ×2（見 _capClearHalfPx）。</summary>
    private float CapsuleWidthLimit() =>
        Math.Max(72f, (_capClearHalfPx - WheelDesign.CapLabelGapPx) * 2f);

    /// <summary>第 i 顆扇區在膠囊裡要顯示的那句話（格式層是「來源 → 目標」）。</summary>
    private string CaptionOf(int i) =>
        _mode == WheelMode.Tool && _caption != null
            ? (_caption(i) ?? _targets[i])
            : _targets.Count == 0
                ? _sourceFormat
                : $"{_sourceFormat} → {_targets[i]}";

    /// <summary>
    /// 這一輪膠囊該用多大字級：**整個輪盤共用一個**，取「所有說明都放得進淨空」的最大值。
    ///
    /// 為什麼不是每顆各自挑：膠囊一次只顯示一顆（hover 到哪顆就換哪顆），
    /// 若字級跟著字數跳，使用者會看到字忽大忽小。四扇區的格式層淨空是無限大（見 _capClearHalfPx），
    /// 這裡永遠回 CapFontPx ⇒ 格式層一個位元都不會變。
    /// </summary>
    private float CapFontFor(Graphics g, float limit)
    {
        if (_capFitFontPx > 0f) { return _capFitFontPx; }

        int count = Math.Max(1, _targets.Count);
        for (int px = (int)WheelDesign.CapFontPx; px >= WheelDesign.CapFontFloorPx; px--)
        {
            bool fits = true;
            for (int i = 0; i < count && fits; i++)
            {
                if (MeasureCapText(g, CaptionOf(i), px) + WheelDesign.CapPadXPx * 2f > limit) { fits = false; }
            }
            if (fits || px == (int)WheelDesign.CapFontFloorPx) { _capFitFontPx = px; break; }
        }
        return _capFitFontPx;
    }

    /// <summary>量字寬（自己開字型、自己關）——只給膠囊「挑字級」用。</summary>
    private static float MeasureCapText(Graphics g, string text, float px)
    {
        using var f = MakeFontPx(px);
        return g.MeasureString(text, f).Width;
    }    /// <summary>中心膠囊：白底半透明、深色字 <c>原格式 → 目標格式</c>（字 17px、padding 5·16、alpha .85）。</summary>
    private void DrawCapsule(Graphics g, float cx, float cy)
    {
        // 工具輪盤的膠囊顯示**當前 hover 的工具動作**（參考版式「中心顯示 hover 動作」）；
        // 格式輪盤維持「原格式 → 目標格式」。兩者都只是字串——膠囊不知道兩者的差別。
        string label = CaptionOf(_selected);

        // 字級：**整個輪盤共用一個**（見 CapFontFor）——hover 換顆時不會忽大忽小。
        float limit = CapsuleWidthLimit();
        float fontPx = CapFontFor(g, limit);
        using var font = MakeFontPx(fontPx);
        var size = g.MeasureString(label, font);
        // 寬度：**不得超過淨空**——寧可把左右 padding 吃掉，也不壓到標籤的起筆。
        float w = Math.Min(Math.Max(WheelDesign.CapMinWidthPx, size.Width + WheelDesign.CapPadXPx * 2), limit);
        float h = size.Height + WheelDesign.CapPadYPx * 2;
        float x = cx - w / 2f, y = cy - h / 2f, rad = h / 2f;

        // 柔和下投影（由外而內疊）
        for (int k = 5; k >= 1; k--)
        {
            using var sp = RoundedRect(new RectangleF(x - k, y - k + 3f + k * 0.6f, w + k * 2, h + k * 2), rad + k);
            using var sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using (var path = RoundedRect(new RectangleF(x, y, w, h), rad))
        using (var brush = new SolidBrush(WheelDesign.CapBg))
            g.FillPath(brush, path);

        using var fg = new SolidBrush(WheelDesign.CapFg);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, fg, new RectangleF(x, y, w, h), format);
    }

    // ==================================================================
    // 輪盤下方：鍵帽 ＋ 說明 ＋ 檔名預覽
    // ==================================================================

    /// <summary>
    /// 白色鍵帽 ＋ 說明文字，位置由輪盤下緣往下排（**不可硬編 y**：
    /// 硬編會讓說明文字被點陣圖下緣切掉，第一版就是這樣壞的）。
    /// 觸發鍵在輪盤可見期間恆為按住狀態（見 <see cref="DragDetector"/>），所以這塊永遠顯示。
    /// </summary>
    private void DrawKeycap(Graphics g, float cx, float wheelBottom)
    {
        using var keyFont = MakeFontPx(WheelDesign.KeyFontPx);
        var keySize = g.MeasureString(TriggerKeyLabel, keyFont);
        float kw = keySize.Width + WheelDesign.KeyPadXPx * 2;
        float kh = WheelGeometry.KeycapPx;
        float kx = cx - kw / 2f;
        float ky = wheelBottom + WheelGeometry.KeyGapPx;

        for (int k = 4; k >= 1; k--)
        {
            using var sp = RoundedRect(new RectangleF(kx - k, ky - k + 2f + k * 0.5f, kw + k * 2, kh + k * 2), 8f + k);
            using var sb = new SolidBrush(Color.FromArgb(12, 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using (var path = RoundedRect(new RectangleF(kx, ky, kw, kh), 8f))
        using (var brush = new SolidBrush(WheelDesign.KeyPill))
            g.FillPath(brush, path);

        using var fg = new SolidBrush(WheelDesign.KeyPillFg);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(TriggerKeyLabel, keyFont, fg, new RectangleF(kx, ky, kw, kh), format);

        // 說明文字：白字身 ＋ 暗色描邊（原型的純白字在淺色桌布上會整個消失）
        float captionTop = ky + kh + WheelGeometry.CaptionGapPx;
        using var capFont = MakeFontPx(WheelDesign.KeyCapFontPx);
        using var outline = new Pen(WheelDesign.KeyCaptionOutline, WheelDesign.KeyCaptionOutlinePx)
        {
            LineJoin = LineJoin.Round
        };
        using var capPath = new GraphicsPath();
        using var capFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        capPath.AddString(WheelDesign.KeyCaptionText, capFont.FontFamily, (int)capFont.Style, capFont.Size,
                          new PointF(0, 0), capFormat);
        var bounds = capPath.GetBounds();
        using (var shift = new Matrix())
        {
            // 把「墨跡中心」平移到目標位置：水平＝圓心，垂直＝說明文字帶的中心
            shift.Translate(cx - (bounds.Left + bounds.Width / 2f),
                            captionTop + WheelGeometry.CaptionPx / 2f - (bounds.Top + bounds.Height / 2f));
            capPath.Transform(shift);
        }
        g.DrawPath(outline, capPath);
        using (var capFill = new SolidBrush(WheelDesign.KeyCaptionFill))
            g.FillPath(capFill, capPath);
    }

    private void DrawPreview(Graphics g, float cx, float wheelBottom)
    {
        // 目的地（規格 §9.5）就在這一行裡：檔名預覽是使用者按 Enter 前唯一會看的地方。
        string? line = ComposePreviewText();
        if (string.IsNullOrEmpty(line)) return;

        float py = wheelBottom + WheelGeometry.KeyGapPx + WheelGeometry.KeycapPx +
                   WheelGeometry.CaptionGapPx + WheelGeometry.CaptionPx + WheelGeometry.PreviewGapPx;
        using var font = MakeFontPx(11f);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var size = g.MeasureString(line, font);
        float pillW = Math.Max(120f, size.Width + 20f);
        float pillH = WheelGeometry.PreviewPx;
        float px = cx - pillW / 2f;

        var pill = _notice != null
            ? Color.FromArgb(230, WheelDesign.NoticeFill)
            : Color.FromArgb(214, 10, 13, 18);
        using (var path = RoundedRect(new RectangleF(px, py, pillW, pillH), pillH / 2f))
        using (var brush = new SolidBrush(pill))
            g.FillPath(brush, path);

        using var text = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
        g.DrawString(line, font, text, new RectangleF(px, py, pillW, pillH), format);
    }

    /// <summary>自繪鬼影（規格 §4.3）：游標旁 32 px 半透明檔案卡，顏色跟著選中扇區走。</summary>
    private void DrawGhostCard(Graphics g, float cursorX, float cursorY)
    {
        float s = WheelGeometry.GhostPx;
        var rect = new RectangleF(cursorX + 6, cursorY + 6, s, s);

        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        using var shadowPath = RoundedRect(new RectangleF(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), 5f);
        g.FillPath(shadow, shadowPath);

        using var fill = new SolidBrush(Color.FromArgb(205, 255, 255, 255));
        using var path = RoundedRect(rect, 5f);
        g.FillPath(fill, path);

        using var border = new Pen(Color.FromArgb(220, 40, 42, 50), 1.4f);
        g.DrawPath(border, path);

        // 摺角
        using var corner = new SolidBrush(Color.FromArgb(220, 210, 212, 220));
        g.FillPolygon(corner, new[]
        {
            new PointF(rect.Right - 10, rect.Top),
            new PointF(rect.Right, rect.Top + 10),
            new PointF(rect.Right - 10, rect.Top + 10)
        });

        Color accent = _targets.Count > 0 ? WheelDesign.SelStroke : Color.FromArgb(120, 124, 134);
        using var accentBrush = new SolidBrush(accent);
        g.FillRectangle(accentBrush, rect.X + 5, rect.Bottom - 9, rect.Width - 10, 4);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// Segoe UI 是 Windows 的標準介面字型；找不到時退回泛用無襯線字型而非崩潰。
    /// **單位是像素**：設計稿的 15px／12.7px 是畫面像素，用 points 會隨 DPI 飄掉。
    /// </summary>
    private static Font MakeFontPx(float px)
    {
        try { return new Font(Typeface.Primary, px, FontStyle.Bold, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel); }
    }
}
