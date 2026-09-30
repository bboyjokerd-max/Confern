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
/// </summary>
internal sealed class WheelPresenter
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(236, 33, 150, 243),   // blue
        Color.FromArgb(236, 76, 175, 80),    // green
        Color.FromArgb(236, 255, 152, 0),    // amber
        Color.FromArgb(236, 156, 39, 176),   // purple
        Color.FromArgb(236, 233, 30, 99),    // pink
        Color.FromArgb(236, 0, 188, 212),    // cyan
        Color.FromArgb(236, 255, 87, 34)     // deep orange
    };

    private readonly Evidence _evidence;
    private readonly VisualLayer _layer;

    private string _sourceFormat = "?";
    private IReadOnlyList<string> _targets = Array.Empty<string>();
    private string? _previewText;
    private string? _notice;
    private int _selected;
    private int _cursorX, _cursorY;
    private MonitorEntry? _monitor;

    public bool IsVisible => _layer.IsVisible;
    public int Selected => _selected;
    public string? SelectedTarget => _targets.Count > 0 ? _targets[_selected] : null;

    public WheelPresenter(Evidence evidence, VisualLayer layer)
    {
        _evidence = evidence;
        _layer = layer;
    }

    /// <summary>在游標處顯示輪盤。<paramref name="notice"/> 非空時取代檔名預覽（例如混合選取拒絕）。</summary>
    public void Show(int cursorX, int cursorY, string sourceFormat, IReadOnlyList<string> targets,
                     int selected = 0, string? previewText = null, string? notice = null,
                     MonitorEntry[]? monitors = null)
    {
        _cursorX = cursorX;
        _cursorY = cursorY;
        _sourceFormat = sourceFormat;
        _targets = targets;
        _selected = targets.Count > 0 ? Math.Clamp(selected, 0, targets.Count - 1) : 0;
        _previewText = previewText;
        _notice = notice;

        var all = monitors ?? MonitorLayout.Enumerate();
        _monitor = MonitorLayout.Nearest(cursorX, cursorY, all);

        using var bmp = Compose(out int ox, out int oy);
        _layer.Show(ox, oy, bmp);

        _evidence.Line($"wheel shown cursor=({cursorX},{cursorY}) origin=({ox},{oy}) " +
                       $"size={bmp.Width}x{bmp.Height} source={_sourceFormat} " +
                       $"targets=[{string.Join(",", _targets)}] selected={_selected} " +
                       $"notice={(notice ?? "-")} preview={(previewText ?? "-")}");
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

    public void Hide()
    {
        if (!_layer.IsVisible) return;
        _layer.Hide();
        _evidence.Line("wheel hidden");
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
        // ClearType 是次像素渲染、不寫 alpha；在 layered window 上會出現黑邊。必須用灰階反鋸齒。
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var work = _monitor?.Work ?? new Native.RECT { Left = 0, Top = 0, Right = w * 4, Bottom = h * 4 };
        var (boxX, boxY) = WheelGeometry.FlipEdge(_cursorX, _cursorY, WheelGeometry.BoxPx, work);
        (originX, originY) = WheelGeometry.WindowOrigin(boxX, boxY);

        int boxOriginInBmp = WheelGeometry.BoxOriginInBitmapPx;
        // 翻邊後點陣圖內的游標位置會跟著位移
        float cursorInBmpX = _cursorX - originX;
        float cursorInBmpY = _cursorY - originY;

        float cx = boxOriginInBmp + WheelGeometry.BoxPx / 2f;
        float cy = boxOriginInBmp + WheelGeometry.BoxPx / 2f;

        DrawGhostCard(g, cursorInBmpX, cursorInBmpY);

        if (_targets.Count == 0)
        {
            DrawEmptyWheel(g, cx, cy);
        }
        else
        {
            DrawWheel(g, cx, cy);
        }

        DrawCenter(g, cx, cy);
        DrawPreview(g, boxOriginInBmp, cy + WheelGeometry.BoxPx / 2f);
        return bmp;
    }

    private void DrawWheel(Graphics g, float cx, float cy)
    {
        int count = _targets.Count;
        float sweep = (float)WheelGeometry.SweepDeg(count);
        float r = WheelGeometry.DiameterPx / 2f;

        for (int i = 0; i < count; i++)
        {
            bool sel = i == _selected;
            float rr = r + (sel ? WheelGeometry.SelectedExtraPx : 0);
            using var brush = new SolidBrush(Palette[i % Palette.Length]);
            g.FillPie(brush, cx - rr, cy - rr, rr * 2, rr * 2,
                (float)WheelGeometry.StartDeg + i * sweep, sweep - 1.5f);
        }

        // 選中扇區加一圈白邊，讓「外擴」看得出來
        {
            float rr = r + WheelGeometry.SelectedExtraPx;
            using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f);
            g.DrawPie(pen, cx - rr, cy - rr, rr * 2, rr * 2,
                (float)WheelGeometry.StartDeg + _selected * sweep, sweep - 1.5f);
        }

        using var font = MakeFont(9f);
        using var text = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < count; i++)
        {
            double mid = WheelGeometry.SectorMidDeg(i, count) * Math.PI / 180.0;
            float lx = cx + (float)(Math.Cos(mid) * r * 0.66);
            float ly = cy + (float)(Math.Sin(mid) * r * 0.66);
            g.DrawString(_targets[i], font, text, new RectangleF(lx - 40, ly - 10, 80, 20), format);
        }
    }

    private void DrawEmptyWheel(Graphics g, float cx, float cy)
    {
        float r = WheelGeometry.DiameterPx / 2f;
        using var brush = new SolidBrush(Color.FromArgb(230, 90, 94, 104));
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
    }

    private void DrawCenter(Graphics g, float cx, float cy)
    {
        float r = WheelGeometry.DiameterPx / 2f * 0.44f;
        using var brush = new SolidBrush(Color.FromArgb(244, 24, 26, 32));
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);

        string label = _targets.Count == 0
            ? _sourceFormat
            : $"{_sourceFormat} → {_targets[_selected]}";
        using var font = MakeFont(_targets.Count == 0 ? 10f : 12f);
        using var text = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, text, new RectangleF(cx - r, cy - r, r * 2, r * 2), format);
    }

    private void DrawPreview(Graphics g, float boxOrigin, float boxBottom)
    {
        string? line = _notice ?? _previewText;
        if (string.IsNullOrEmpty(line)) return;

        using var font = MakeFont(9f);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var size = g.MeasureString(line, font);
        float pillW = size.Width + 18;
        float pillH = 20;
        float px = boxOrigin + WheelGeometry.BoxPx / 2f - pillW / 2f;
        float py = boxBottom - WheelGeometry.GlowPx + 2f;

        var pill = _notice != null ? Color.FromArgb(232, 150, 40, 40) : Color.FromArgb(232, 18, 20, 26);
        using var brush = new SolidBrush(pill);
        using var path = RoundedRect(new RectangleF(px, py, pillW, pillH), 6f);
        g.FillPath(brush, path);

        using var text = new SolidBrush(Color.White);
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

        Color accent = _targets.Count > 0 ? Palette[_selected % Palette.Length] : Color.FromArgb(120, 124, 134);
        using var accentBrush = new SolidBrush(accent);
        g.FillRectangle(accentBrush, rect.X + 5, rect.Bottom - 9, rect.Width - 10, 4);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Segoe UI 是 Windows 的標準介面字型；找不到時退回泛用無襯線字型而非崩潰。</summary>
    private static Font MakeFont(float points)
    {
        try { return new Font("Segoe UI", points, FontStyle.Bold, GraphicsUnit.Point); }
        catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, points, FontStyle.Bold, GraphicsUnit.Point); }
    }
}
