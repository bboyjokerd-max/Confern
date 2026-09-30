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
    private const int BitmapHeight = 54;

    private readonly VisualLayer _layer;
    private readonly Evidence _evidence;

    private int _x, _y;
    private bool _shown;
    private bool _started;
    private int _updates;
    private ConversionSnapshot _last = ConversionSnapshot.Idle;
    private System.Diagnostics.Stopwatch? _finishedAt;

    /// <summary>
    /// tick 時去哪裡拉最新狀態（由 <c>ConversionRunner</c> 設定）。
    /// 這樣「進度從哪來」與「進度怎麼顯示」解耦：顯示層不需要知道 worker 或管道。
    /// </summary>
    public Func<ConversionSnapshot>? Source { get; set; }

    public ProgressPresenter(Evidence evidence, VisualLayer layer)
    {
        _evidence = evidence;
        _layer = layer;
        _layer.TickRequested += Tick;      // 用 SetTimer 驅動，不用輪詢迴圈
    }

    public bool IsVisible => _layer.IsVisible;
    public int Frames => _layer.Frames;

    /// <summary>已經更新過幾次（用來斷言「原地更新」而不是「一直開新視窗」）。</summary>
    public int Updates => _updates;

    /// <summary>輪盤收起的位置（規格 §4.4：進度就顯示在這裡）。</summary>
    public void Start(int x, int y)
    {
        _x = x;
        _y = y;
        _shown = false;
        _started = true;
        _updates = 0;
        _finishedAt = null;
        _last = ConversionSnapshot.Idle;
        _layer.StartTick(80);
    }

    /// <summary>由 UI 的 tick 呼叫：內容有變才重畫（同一張圖原地更新）。</summary>
    public void Update(ConversionSnapshot snapshot)
    {
        if (snapshot == _last && _shown) return;
        bool justFinished = snapshot.Finished && !_last.Finished;
        _last = snapshot;
        if (justFinished) _finishedAt = System.Diagnostics.Stopwatch.StartNew();
        RedrawAlpha(255);
    }

    /// <summary>淡出與收尾；同時負責去來源拉最新狀態（唯一的驅動來源）。</summary>
    private void Tick()
    {
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
                               $"(updates={_updates}, hold={hold} ms)");
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
        using Bitmap bitmap = Compose(_last.Line, _last.Done, _last.Total, _last.Finished, _last.Failed, alpha);
        // 以輪盤中心對齊，往上偏一點，避免正好壓在游標下方
        _layer.Show(_x - BitmapWidth / 2, _y - BitmapHeight / 2, bitmap);
        _shown = true;
        _updates++;
        _evidence.Line($"progress: {_last.Line} done={_last.Done}/{_last.Total} " +
                       $"current={_last.Current ?? "-"} finished={_last.Finished} alpha={alpha} " +
                       $"(update #{_updates})");
    }

    public void Hide()
    {
        _layer.Hide();
        _layer.StartTick(0);
        _shown = false;
    }

    /// <summary>
    /// 畫出那一行。抽成 static 是為了讓 `--progress-dump` 能在**沒有畫面**的情況下驗證字形
    /// （✓／✗ 這種符號若字型缺字會變成方框，光看程式碼看不出來）。
    /// </summary>
    public static Bitmap Compose(string text, int done, int total, bool finished, int failed, int alpha)
    {
        var bitmap = new Bitmap(BitmapWidth, BitmapHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int a = Math.Clamp(alpha, 0, 255);
        Color pill = failed > 0
            ? Color.FromArgb((int)(236 * a / 255), 150, 40, 40)
            : Color.FromArgb((int)(236 * a / 255), 24, 26, 32);

        using (var path = RoundedRect(new RectangleF(1, 1, BitmapWidth - 2, BitmapHeight - 2), 12f))
        using (var brush = new SolidBrush(pill))
        {
            g.FillPath(brush, path);
        }

        using (var font = MakeFont(15f))
        using (var textBrush = new SolidBrush(Color.FromArgb(a, 245, 247, 250)))
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        })
        {
            g.DrawString(text, font, textBrush,
                         new RectangleF(0, 2, BitmapWidth, BitmapHeight - 14), format);
        }

        // 底部細長進度條：完成比例一目了然（沒有 total 時不畫，避免假的 100%）
        if (total > 0)
        {
            var track = new RectangleF(12, BitmapHeight - 9, BitmapWidth - 24, 3);
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

    private static Font MakeFont(float points)
    {
        try { return new Font("Segoe UI", points, FontStyle.Bold, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, points, FontStyle.Bold, GraphicsUnit.Point); }
    }

    public void Dispose() => _layer.TickRequested -= Tick;
}
