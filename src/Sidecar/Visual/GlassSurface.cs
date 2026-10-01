using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sidecar;

/// <summary>
/// 快照式毛玻璃底材（計畫 §4）。**全程式唯一抓螢幕與做模糊的地方**。
///
/// 為什麼一定要快照：DWM 的亞克力／Mica 只對一般視窗生效，per-pixel alpha 分層視窗沒有東西可畫。
///
/// 為什麼只抓一次：實測 <c>BitBlt</c> 的成本＝**一個 60 Hz vblank（16.67 ms）**，
/// 與抓取面積、API 路徑都無關（連完全不搬像素的 <c>DwmFlush()</c> 本身都是 16.67 ms）。
/// 所以它只能在拖曳開始時付一次，輪盤顯示期間的所有重畫都必須重用同一張。
/// 見 `docs/aegis/work/2026-10-01-glass-spike/91-round2-d-palette.md` §5。
///
/// ⚠ **抓取時機必須在視覺層顯示之前**：分層視窗一旦可見，抓到的就是輪盤自己（自我指涉）。
/// 這個錯誤不會報錯，只會讓畫面「有點怪」，所以由 <see cref="WheelPresenter"/> 的順序保證。
///
/// ⚠ **alpha 紅線**（見 91 號文件 §1）：本類別產出的點陣圖是**不透明的背景影像**，
/// 它只能被畫在「alpha 已是 255 的玻璃本體」裡；圓盤外那片投影／光暈必須是純圖層、
/// 不可把這張圖烘進它們的 RGB，否則薄罩會被 DWM 再合成一次而精確抵銷。
/// </summary>
internal sealed class GlassSurface : IDisposable
{
    /// <summary>模糊後的桌布（尺寸＝輪盤點陣圖尺寸，座標一一對應）。</summary>
    public Bitmap Blurred { get; }

    /// <summary>平均亮度（0–255）。用來決定光暈策略，見 <see cref="WheelDesign.IsLightBackdrop"/>。</summary>
    public double MeanLuma { get; }

    /// <summary>true＝合成桌布（自動化驗收用，沒有碰真實螢幕）。</summary>
    public bool IsSynthetic { get; }

    /// <summary>證據行用的來源描述。</summary>
    public string Source { get; }

    private GlassSurface(Bitmap blurred, double meanLuma, bool synthetic, string source)
    {
        Blurred = blurred;
        MeanLuma = meanLuma;
        IsSynthetic = synthetic;
        Source = source;
    }

    /// <summary>
    /// 抓螢幕並模糊。**失敗一律回傳 null**（DRM／安全桌面／沒有互動式桌面），
    /// 呼叫端必須有純色 fallback——抓不到不是例外狀況，是預期內的環境之一。
    /// </summary>
    public static GlassSurface? CaptureFromScreen(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        Bitmap? raw = null;
        try
        {
            raw = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(raw))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            var blurred = Blur(raw, WheelDesign.BlurRadiusPx);
            return new GlassSurface(blurred, ComputeMeanLuma(blurred), false, $"screen ({x},{y}) {w}x{h}");
        }
        catch (Exception)
        {
            return null;   // 呼叫端會退回純色玻璃；這裡刻意不吞掉證據，由呼叫端記一行
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>
    /// 合成桌布（`dark`／`light`）：**可重現**的驗收底材。
    /// 自動化驗收不能依賴「當時桌面長什麼樣」，深淺兩端的可讀性也必須能重跑。
    /// </summary>
    public static GlassSurface Synthetic(string kind, int w, int h)
    {
        bool light = string.Equals(kind, "light", StringComparison.OrdinalIgnoreCase);
        using var raw = BuildSyntheticWallpaper(light, w, h);
        var blurred = Blur(raw, WheelDesign.BlurRadiusPx);
        return new GlassSurface(blurred, ComputeMeanLuma(blurred), true, $"synthetic '{kind}' {w}x{h}");
    }

    /// <summary>取樣平均亮度（每 16 像素取一點就夠，不必逐像素）。</summary>
    public static double ComputeMeanLuma(Bitmap bmp)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bd.Stride;
            var row = new byte[stride];
            double sum = 0;
            long n = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(bd.Scan0 + y * stride, row, 0, stride);
                for (int i = 0; i + 3 < bmp.Width * 4; i += 64)
                {
                    sum += 0.2126 * row[i + 2] + 0.7152 * row[i + 1] + 0.0722 * row[i];
                    n++;
                }
            }
            return n == 0 ? 0 : sum / n;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>3 次可分離 box blur，等效高斯 σ ≈ 17.5（＝設計的 blur(18px)）。</summary>
    public static Bitmap Blur(Bitmap src, int radius)
    {
        int w = src.Width, h = src.Height;
        var rPlane = new byte[w * h];
        var gPlane = new byte[w * h];
        var bPlane = new byte[w * h];

        var bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bd.Stride;
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * stride, row, 0, stride);
                int off = y * w;
                for (int x = 0; x < w; x++)
                {
                    bPlane[off + x] = row[x * 4 + 0];
                    gPlane[off + x] = row[x * 4 + 1];
                    rPlane[off + x] = row[x * 4 + 2];
                }
            }
        }
        finally { src.UnlockBits(bd); }

        BlurPlane(rPlane, w, h, radius);
        BlurPlane(gPlane, w, h, radius);
        BlurPlane(bPlane, w, h, radius);

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = dd.Stride;
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                for (int x = 0; x < w; x++)
                {
                    row[x * 4 + 0] = bPlane[off + x];
                    row[x * 4 + 1] = gPlane[off + x];
                    row[x * 4 + 2] = rPlane[off + x];
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, dd.Scan0 + y * stride, stride);
            }
        }
        finally { dst.UnlockBits(dd); }
        return dst;
    }

    /// <summary>單一色版的 3 次 box blur（水平＋垂直為一次，邊緣以夾邊處理）。</summary>
    private static void BlurPlane(byte[] plane, int w, int h, int radius)
    {
        var line = new byte[Math.Max(w, h)];
        var outp = new byte[line.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                Array.Copy(plane, off, line, 0, w);
                BoxLine(line, outp, w, radius);
                Array.Copy(outp, 0, plane, off, w);
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) line[y] = plane[y * w + x];
                BoxLine(line, outp, h, radius);
                for (int y = 0; y < h; y++) plane[y * w + x] = outp[y];
            }
        }
    }

    /// <summary>一維 box blur（滑動視窗，O(n)）。</summary>
    private static void BoxLine(byte[] src, byte[] dst, int n, int r)
    {
        if (n <= 0) return;
        int window = 2 * r + 1;
        int sum = 0;
        for (int i = -r; i <= r; i++) sum += src[Math.Clamp(i, 0, n - 1)];
        for (int i = 0; i < n; i++)
        {
            dst[i] = (byte)(sum / window);
            sum -= src[Math.Clamp(i - r, 0, n - 1)];
            sum += src[Math.Clamp(i + r + 1, 0, n - 1)];
        }
    }

    /// <summary>
    /// 合成桌布：底色漸層 ＋ 三顆柔光斑 ＋ 一條斜向硬邊 ＋ 細格紋。
    /// 硬邊與格紋是**故意的**：模糊有沒有真的生效，看這兩個地方最清楚
    /// （純漸層就算完全不模糊也看不出差別）。
    /// </summary>
    private static Bitmap BuildSyntheticWallpaper(bool light, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color top = light ? Color.FromArgb(255, 236, 240, 247) : Color.FromArgb(255, 27, 32, 40);
        Color bottom = light ? Color.FromArgb(255, 205, 214, 228) : Color.FromArgb(255, 12, 15, 20);
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), top, bottom, 90f))
            g.FillRectangle(lg, 0, 0, w, h);

        // 柔光斑
        var blobs = new (float fx, float fy, float fr, Color c)[]
        {
            (0.22f, 0.24f, 0.34f, light ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 90, 140, 210)),
            (0.78f, 0.30f, 0.26f, light ? Color.FromArgb(70, 200, 220, 255) : Color.FromArgb(60, 40, 120, 110)),
            (0.60f, 0.82f, 0.30f, light ? Color.FromArgb(60, 255, 220, 190) : Color.FromArgb(55, 160, 110, 60))
        };
        foreach (var (fx, fy, fr, c) in blobs)
        {
            float cx = w * fx, cy = h * fy, rr = Math.Min(w, h) * fr;
            using var path = new GraphicsPath();
            path.AddEllipse(cx - rr, cy - rr, rr * 2, rr * 2);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = c,
                SurroundColors = new[] { Color.FromArgb(0, c.R, c.G, c.B) }
            };
            g.FillPath(pgb, path);
        }

        // 斜向硬邊（模糊可見性的基準）
        using (var pen = new Pen(light ? Color.FromArgb(120, 90, 105, 130) : Color.FromArgb(110, 210, 220, 235), 3f))
            g.DrawLine(pen, -10, h * 0.62f, w + 10, h * 0.34f);

        // 細格紋
        using (var pen = new Pen(light ? Color.FromArgb(40, 60, 70, 90) : Color.FromArgb(38, 235, 240, 250), 1f))
        {
            for (int x = 0; x < w; x += 14) g.DrawLine(pen, x, 0, x, h);
            for (int y = 0; y < h; y += 14) g.DrawLine(pen, 0, y, w, y);
        }
        return bmp;
    }

    public void Dispose() => Blurred.Dispose();
}
