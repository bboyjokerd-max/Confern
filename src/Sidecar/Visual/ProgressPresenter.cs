using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Sidecar;

/// <summary>
/// 進度顯示（規格 §4.4／§7.3）：**在輪盤收起的原位置，原地一行 `N/M`**，完成後 2 秒淡出。
///
/// 硬規則：
///  • 只有**一個**視窗、**一行**文字，內容就地更新——不得每個檔案彈一個視窗或逐行洗版；
///  • 部分失敗時顯示 `✓ 8 / ✗ 2`，但**不自動彈出清單**（清單要使用者點才展開）。
///
/// 實作沿用已經驗過穿透／z-order／收層的 <see cref="VisualLayer"/>：不新增第二套視窗機制。
/// </summary>
internal sealed class ProgressPresenter : IDisposable
{
    private const int FadeMs = 2000;         // 規格 §4.4：完成後 2 秒淡出
    private const int FailureHoldMs = 5000;  // 有失敗時留久一點（使用者要看得見 ✗ 幾個）
    private const int FadeOutMs = 350;
    private const int BitmapWidth = 168;
    private const int HorizontalPadding = 14;
    private const int BitmapHeight = 54;

    /// <summary>
    /// 清單最多顯示幾列（規格 §7.3 的實作上限）。超過的部分用 `…還有 N 筆` 收尾——
    /// 沒有限制的清單在「拖了 300 個檔案、壞了 80 個」時會長到蓋住整個畫面，
    /// 那比不顯示還糟：它會擋住使用者正在做的事，而且沒有辦法關掉。
    /// </summary>
    public const int MaxDetailRows = 8;

    private const int DetailRowHeight = 34;      // 一列的固定行高（文字置中）
    private const int DetailPadX = 16;
    private const int DetailPadY = 14;
    private const int DetailTextInsetX = 14;
    private const float DetailFontPx = 14f;

    private readonly VisualLayer _layer;
    private readonly Evidence _evidence;

    private int _x, _y;
    private bool _shown;
    private bool _started;
    private int _tickCount;
    private readonly bool _trace;              // --progress-trace：每次回呼的決策（診斷用）
    private int _updates;
    private ConversionSnapshot _last = ConversionSnapshot.Idle;
    private System.Diagnostics.Stopwatch? _finishedAt;

    /// <summary>最後一次畫在螢幕上的矩形（螢幕座標）。命中區域的唯一來源，不另立第二份幾何。</summary>
    private Rectangle _lastRect;
    private bool _hasRect;

    /// <summary>清單是否展開（規格 §7.3：**不自動彈出**，只有 <see cref="ToggleDetail"/> 會改變它）。</summary>
    private bool _expanded;

    /// <summary>
    /// 最後一次畫出的失敗列（規格 §7.3 清單的內容）。存下來是為了讓「展開」與「重畫」
    /// 拿得到同一份資料——兩者不一致時，畫面會顯示上一次轉檔的清單，而那種錯誤不會拋例外。
    /// </summary>
    private IReadOnlyList<ConversionFailureRow> _rows = Array.Empty<ConversionFailureRow>();

    /// <summary>
    /// tick 時去哪裡拉最新狀態（由 <c>ConversionRunner</c> 設定）。
    /// 這樣「進度從哪來」與「進度怎麼顯示」解耦：顯示層不需要知道 worker 或管道。
    /// </summary>
    public Func<ConversionSnapshot>? Source { get; set; }

    public ProgressPresenter(Evidence evidence, VisualLayer layer, bool trace = false)
    {
        _evidence = evidence;
        _layer = layer;
        _trace = trace;
        _layer.TickRequested += Tick;      // 由圖層的週期回呼驅動，不用輪詢迴圈
    }

    public bool IsVisible => _layer.IsVisible;
    public int Frames => _layer.Frames;

    /// <summary>已經更新過幾次（用來斷言「原地更新」而不是「一直開新視窗」）。</summary>
    public int Updates => _updates;

    /// <summary>
    /// 最後顯示的矩形（螢幕座標，左上角＋尺寸）。尚未畫過時是 <see cref="Rectangle.Empty"/>。
    ///
    /// **單一幾何來源**：這個矩形就是 `_layer.Show(_x - w/2, _y - h/2, bitmap)` 當下算出來的那一個，
    /// 命中判定（<see cref="Contains"/>）直接問它，不另外用量測寬度重算——
    /// 那會變成兩份幾何，而兩份幾何在尺寸或 DPI 一變就不一致。
    /// </summary>
    public Rectangle PillRect => _hasRect ? _lastRect : Rectangle.Empty;

    /// <summary>螢幕座標 (x,y) 是否落在最後顯示的矩形（藥丸，或展開後的清單）內。</summary>
    public bool Contains(int x, int y) => _hasRect && _lastRect.Contains(x, y);

    /// <summary>
    /// 清單是否展開。規格 §7.3 的「清單**不自動彈出**」就是靠這個欄位：它只在
    /// <see cref="ToggleDetail"/> 裡被翻動，沒有任何計時器或狀態變化會去動它。
    /// </summary>
    public bool IsExpanded => _expanded;

    /// <summary>
    /// 展開／收起失敗清單（規格 §7.3）。
    ///
    /// ⚠ **目前沒有任何地方會從真實點擊呼叫它**：那一下點擊要不要被吃掉，屬於容器層的裁決
    /// （專屬可點視窗 vs 現成的全域勾子），尚未拍板，而那種改動會動到穿透／z-order 的契約。
    /// 這一版先把行為本身做成可驗證的（自測與 dump 走同一條路），接線留給裁決之後。
    /// </summary>
    public void ToggleDetail()
    {
        _expanded = !_expanded;
        _evidence.Line($"progress detail: expanded={_expanded} rows={_rows.Count} " +
                       "(spec 7.3: the list only appears on an explicit toggle)");
        if (_shown) RedrawAlpha(255);
    }

    /// <summary>輪盤收起的位置（規格 §4.4：進度就顯示在這裡）。</summary>
    public void Start(int x, int y)
    {
        _x = x;
        _y = y;
        _shown = false;
        _started = true;
        _updates = 0;
        _tickCount = 0;
        _finishedAt = null;
        _last = ConversionSnapshot.Idle;
        _rows = Array.Empty<ConversionFailureRow>();
        _expanded = false;      // 新的一次轉檔＝收起的清單（§7.3：不自動彈出）
        _hasRect = false;
        _layer.StartTick(80);
    }

    /// <summary>由 UI 的 tick 呼叫：內容有變才重畫（同一張圖原地更新）。</summary>
    public void Update(ConversionSnapshot snapshot)
    {
        if (snapshot == _last && _shown) return;
        _last = snapshot;
        // 失敗列的**唯一來源**：快照自己（ConversionSnapshot.DetailRows）。這裡不做任何字串切割，
        // 否則「檔名／原因怎麼分」會變成第二份規則，與規格 §7.1 的組法默默分歧。
        _rows = snapshot.DetailRows;
        // 淡出時鐘：**第一次畫出「已完成」的那一幀**就開始計時，不靠狀態邊緣。
        //
        // 這裡原本寫成 justFinished = snapshot.Finished && !_last.Finished，是個定時炸彈：
        // 本方法的初始狀態是 ConversionSnapshot.Idle，而 Idle.Finished 本來就是 true
        // （Idle 的意思是「沒事做」）。所以只要**第一次回呼發生在轉檔已經結束之後**，
        // 那個邊緣就永遠不會出現 → _finishedAt 永遠是 null → 進度那一行永遠不淡出。
        // 這個情況不是理論：UI 執行緒在 ConversionClient.Start 等 worker 連線時被擋住
        // 約 300 ms，而 worker 的實際工作是 3–8 ms，所以經常「回呼來得太晚」。
        // 2026-10-01 由 S12.10d 抓到（當時 6 次出現 1 次）；--progress-trace 的 elapsed=-
        // 是決定性證據。改成本條件後與時序無關。
        if (snapshot.Finished && _finishedAt == null)
            _finishedAt = System.Diagnostics.Stopwatch.StartNew();
        RedrawAlpha(255);
    }

    /// <summary>淡出與收尾；同時負責去來源拉最新狀態（唯一的驅動來源）。</summary>
    private void Tick()
    {
        _tickCount++;
        if (_trace)
            // 高頻行：轉檔期間每個 tick 都寫一次 → 走 Detail（診斷開啟時才寫）。
            _evidence.Detail($"progress tick #{_tickCount} started={_started} shown={_shown} " +
                           $"finished={_last.Finished} elapsed={_finishedAt?.ElapsedMilliseconds.ToString() ?? "-"} " +
                           $"timerAlive={_layer.TickAlive}");
        if (!_started) return;

        if (Source != null) Update(Source());     // Update 內部有「沒變就不重畫」的守衛
        if (!_shown) return;

        if (_last.Finished && _finishedAt != null)
        {
            int hold = _last.Failed > 0 ? FailureHoldMs : FadeMs;
            long elapsed = _finishedAt.ElapsedMilliseconds;
            if (elapsed >= hold + FadeOutMs)
            {
                _evidence.Line($"progress: hiding after {elapsed} ms " +
                               $"(updates={_updates}, ticks={_tickCount}, hold={hold} ms)");
                _layer.Hide();
                _layer.StartTick(0);
                _shown = false;
                _started = false;
                return;
            }
            if (elapsed >= hold)
            {
                int alpha = (int)(255 * (1.0 - (double)(elapsed - hold) / FadeOutMs));
                RedrawAlpha(Math.Clamp(alpha, 0, 255));
            }
        }
    }

    private void RedrawAlpha(int alpha)
    {
        // 展開／收起的決定只在 _expanded；兩條路都走**同一個** alpha 參數，
        // 所以清單與藥丸的淡出是同一個節奏（沒有第二套 alpha 規則）。
        using Bitmap bitmap = _expanded
            ? ComposeDetail(_rows, alpha)
            : Compose(_last.Line, _last.Done, _last.Total, _last.Finished, _last.Failed, alpha);
        // 以輪盤中心對齊，往上偏一點，避免正好壓在游標下方
        int left = _x - bitmap.Width / 2, top = _y - bitmap.Height / 2;
        _layer.Show(left, top, bitmap);
        // 記下真正送出去的矩形：命中判定問的是「螢幕上那一塊」，
        // 不是「我以為它有多大」。展開後矩形變大，命中區域也跟著變大。
        _lastRect = new Rectangle(left, top, bitmap.Width, bitmap.Height);
        _hasRect = true;
        _shown = true;
        _updates++;
        _evidence.Line($"progress: {_last.Line} done={_last.Done}/{_last.Total} " +
                       $"current={_last.Current ?? "-"} finished={_last.Finished} alpha={alpha} " +
                       $"expanded={_expanded} rect={_lastRect.X},{_lastRect.Y},{_lastRect.Width},{_lastRect.Height} " +
                       $"(update #{_updates})");
    }

    public void Hide()
    {
        _layer.Hide();
        _layer.StartTick(0);
        _shown = false;
    }

    /// <summary>
    /// 畫出失敗清單（規格 §7.3）。
    ///
    /// 抽成 static 的理由與 <see cref="Compose"/> **完全相同**：要能在沒有畫面的情況下 dump 出來
    /// 驗字形與裁切。✓／✗ 這種符號若字型缺字會變成方框（方框有墨水，所以「檔案存在」什麼都證明不了），
    /// 而清單是這個功能唯一的產出——看不到就等於沒驗過。
    ///
    /// 語彙沿用同一套，不是新發明：
    ///   • 錯誤紅＝<see cref="WheelDesign.NoticeFill"/>（產品裡「這是錯誤」只有一個紅）；
    ///   • 圓角＝與藥丸同一種（半高膠囊／固定圓角的同一族），不是另一種形狀語言；
    ///   • 字型＝與藥丸共用的 <see cref="MakeFontPx"/>；alpha＝與藥丸相同的算法；
    ///   • 寬度＝**依文字量測**（固定寬度會裁掉中文原因，那就等於使用者看不到原因）；
    ///   • 上限＝<see cref="MaxDetailRows"/> 列 ＋ `…還有 N 筆`，避免清單蓋住整個畫面。
    /// </summary>
    public static Bitmap ComposeDetail(IReadOnlyList<ConversionFailureRow> rows, int alpha)
    {
        int shown = Math.Min(rows.Count, MaxDetailRows);
        int hidden = Math.Max(0, rows.Count - shown);
        int lines = shown + (hidden > 0 ? 1 : 0);

        using var font = MakeFontPx(DetailFontPx);
        var texts = new string[lines];
        for (int i = 0; i < shown; i++) texts[i] = rows[i].Text;
        if (hidden > 0) texts[lines - 1] = $"…還有 {hidden} 筆";

        // 寬度依文字量測（同 Compose）。**不可改成固定寬度**：規格 §7.1 的原因字串
        // （`broken.png 無法解碼`）比檔名長得多，裁掉就等於使用者看不到原因，
        // 而那種失敗在畫面上看起來只是「字被切掉」——不會有任何錯誤訊息。
        int width = BitmapWidth;
        using (var measureBitmap = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(measureBitmap))
        {
            foreach (string text in texts)
            {
                float needed = measure.MeasureString(text, font).Width;
                width = Math.Max(width, (int)Math.Ceiling(needed) + DetailPadX * 2 + HorizontalPadding);
            }
        }

        int height = lines * DetailRowHeight + DetailPadY * 2;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int a = Math.Clamp(alpha, 0, 255);
        // 錯誤紅與藥丸同一個來源、同一個不透明度（236）：清單是藥丸的展開，
        // 兩者色差會讓人以為是兩個不同的東西。
        Color panel = Color.FromArgb((int)(236 * a / 255), WheelDesign.NoticeFill);
        // 圓角半徑＝藥丸的半高：同一種「膠囊／圓角矩形」的語彙，不是第二種形狀。
        using (var path = RoundedRect(new RectangleF(1, 1, width - 2, height - 2), BitmapHeight / 2f))
        using (var brush = new SolidBrush(panel))
        {
            g.FillPath(brush, path);
        }

        using (var ink = new SolidBrush(Color.FromArgb(a, 245, 247, 250)))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            // NoWrap ＋ EllipsisCharacter：真的塞不下時**截斷並加省略號**，不是折到第二行。
            // 折行的代價不只是難看：那一列會需要兩倍行高，而固定的行高會把第二行裁掉，
            // 使用者看到的是半截中文——比「被截斷且有省略號」更難判斷自己在看什麼。
            // （這段註解是驗收套件用「固定寬度」的假故障逼出來的：那時 real 那一列
            //   在像素上變成兩個墨跡帶，也就是文字被折行了。正常寬度下永遠不會發生。）
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            for (int i = 0; i < lines; i++)
            {
                var row = new RectangleF(DetailTextInsetX, DetailPadY + i * DetailRowHeight,
                                         width - DetailTextInsetX * 2, DetailRowHeight);
                g.DrawString(texts[i], font, ink, row, format);
            }
        }

        // 分隔線：讓「還有 N 筆」看得出來是收尾，而不是第 9 筆失敗。
        if (hidden > 0)
        {
            float y = DetailPadY + shown * DetailRowHeight;
            using var hair = new Pen(Color.FromArgb((int)(90 * a / 255), 255, 255, 255), 1f);
            g.DrawLine(hair, DetailPadX, y, width - DetailPadX, y);
        }

        return bitmap;
    }

    /// <summary>
    /// 畫出那一行。抽成 static 是為了讓 `--progress-dump` 能在**沒有畫面**的情況下驗證字形
    /// （✓／✗ 這種符號若字型缺字會變成方框，光看程式碼看不出來）。
    /// </summary>
    public static Bitmap Compose(string text, int done, int total, bool finished, int failed, int alpha)
    {
        using var font = MakeFontPx(15f);
        // 寬度依文字量測：規格 §7.1 的失敗訊息（`broken.png 無法解碼`）比 `✓ 1` 長得多，
        // 固定寬度會把它裁掉 —— 那就等於使用者看不到原因。
        int width;
        using (var measureBitmap = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(measureBitmap))
        {
            float needed = measure.MeasureString(text, font).Width;
            width = Math.Max(BitmapWidth, (int)Math.Ceiling(needed) + HorizontalPadding * 2);
        }
        var bitmap = new Bitmap(width, BitmapHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int a = Math.Clamp(alpha, 0, 255);
        // 同一個產品裡的「錯誤紅」只有一個來源（視覺方向 D 的 WheelDesign）。
        Color pill = failed > 0
            ? Color.FromArgb((int)(236 * a / 255), WheelDesign.NoticeFill)
            : Color.FromArgb((int)(236 * a / 255), 24, 26, 32);

        // 圓角改成整顆藥丸（半高）：與方向 D 的膠囊／鍵帽／預覽列同一套語彙。
        // 原本固定 12px，在同一個畫面裡與輪盤的元件不是同一種形狀語言。
        using (var path = RoundedRect(new RectangleF(1, 1, width - 2, BitmapHeight - 2), (BitmapHeight - 2) / 2f))
        using (var brush = new SolidBrush(pill))
        {
            g.FillPath(brush, path);
        }

        using (var textBrush = new SolidBrush(Color.FromArgb(a, 245, 247, 250)))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        })
        {
            g.DrawString(text, font, textBrush,
                         new RectangleF(0, 2, width, BitmapHeight - 14), format);
        }

        // 底部細長進度條：完成比例一目了然（沒有 total 時不畫，避免假的 100%）
        if (total > 0)
        {
            var track = new RectangleF(12, BitmapHeight - 9, width - 24, 3);
            using (var trackBrush = new SolidBrush(Color.FromArgb((int)(90 * a / 255), 255, 255, 255)))
                g.FillRectangle(trackBrush, track);

            float ratio = Math.Clamp((float)done / total, 0f, 1f);
            if (ratio > 0)
            {
                Color barColor = failed > 0
                    ? Color.FromArgb((int)(235 * a / 255), 239, 83, 80)
                    : Color.FromArgb((int)(235 * a / 255), 102, 187, 106);
                using var barBrush = new SolidBrush(barColor);
                g.FillRectangle(barBrush, new RectangleF(track.X, track.Y, track.Width * ratio, track.Height));
            }
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// **單位是像素**，與 <see cref="WheelPresenter"/> 一致。原本用 Point：
    /// 在 96 DPI 下 15pt ＝ 20px（比輪盤的 17px 膠囊字還大），而且會隨 DPI 縮放——
    /// 150 % 縮放時同一張 54px 高的卡片會裝不下。
    /// </summary>
    private static Font MakeFontPx(float px)
    {
        try { return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel); }
    }

    public void Dispose() => _layer.TickRequested -= Tick;
}
