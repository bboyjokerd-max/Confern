// follow-probe.cs — "does the alpha edge follow the image?" probe.
//
// Question it answers: on a wide soft halo, is the alpha edge correlated with the image
// (=> genuine hair, must be kept) or not (=> a smeared ramp that may safely be narrowed)?
//
//   a         = cov(I, p) / (var(I) + eps)        the guided filter's own slope
//   explained = |a| * sqrt(var(I))                how much of p's local variation the image explains
//
// Usage from PowerShell:
//   Add-Type -Path $env:TEMP\follow-probe.cs -ReferencedAssemblies System.Drawing
//   [FollowProbe]::Follow($imagePath, $alphaPngPath, 8, 2.5)

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class FollowProbe
{
    public static byte[] Plane(string p, out int w, out int h)
    {
        using (var b = new Bitmap(p))
        {
            w = b.Width; h = b.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), buf, y * w * 4, w * 4);
            b.UnlockBits(d);
            return buf;
        }
    }

    static void Box(float[] src, float[] dst, int w, int h, int r)
    {
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int lo = x - r < 0 ? 0 : x - r, hi = x + r >= w ? w - 1 : x + r;
                float s = 0;
                for (int i = lo; i <= hi; i++) s += src[y * w + i];
                tmp[y * w + x] = s / (hi - lo + 1);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int lo = y - r < 0 ? 0 : y - r, hi = y + r >= h ? h - 1 : y + r;
                float s = 0;
                for (int i = lo; i <= hi; i++) s += tmp[i * w + x];
                dst[y * w + x] = s / (hi - lo + 1);
            }
    }

    // Mean transition-band width in pixels: distance to the nearest solid pixel plus distance
    // to the nearest empty pixel. Proportional to how smeared the edge is.
    static double BandMean(byte[] a, int w, int h)
    {
        var dO = new int[w * h];
        var dC = new int[w * h];
        const int BIG = 1 << 28;
        for (int i = 0; i < w * h; i++)
        {
            int A = a[i * 4 + 3];
            dO[i] = A >= 250 ? 0 : BIG;
            dC[i] = A == 0 ? 0 : BIG;
        }
        for (int i = 0; i < w * h; i++)
        {
            int A = a[i * 4 + 3];
            if (A == 0 || A >= 255) continue;
        }
        // two-pass chamfer, forward then backward
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                for (int e = 0; e < 2; e++)
                {
                    var d = e == 0 ? dO : dC;
                    int v = d[i];
                    if (y > 0)
                    {
                        v = Math.Min(v, d[i - w] + 1);
                        if (x > 0) v = Math.Min(v, d[i - w - 1] + 1);
                        if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 1);
                    }
                    if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                    d[i] = v;
                }
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                for (int e = 0; e < 2; e++)
                {
                    var d = e == 0 ? dO : dC;
                    int v = d[i];
                    if (y < h - 1)
                    {
                        v = Math.Min(v, d[i + w] + 1);
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + 1);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 1);
                    }
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 1);
                    d[i] = v;
                }
            }
        long n = 0;
        double s = 0;
        for (int i = 0; i < w * h; i++)
        {
            int A = a[i * 4 + 3];
            if (A == 0 || A >= 255) continue;
            s += dO[i] + dC[i];
            n++;
        }
        return n == 0 ? 0 : s / n;
    }

    public static string Follow(string imgPath, string alphaPath, int radius, double sharpen)
    {
        int w, h;
        var img = Plane(imgPath, out w, out h);
        int aw, ah;
        var al = Plane(alphaPath, out aw, out ah);
        if (w != aw || h != ah) return "size mismatch";
        int n = w * h;
        var I = new float[n];
        var P = new float[n];
        for (int i = 0; i < n; i++)
        {
            I[i] = (0.299f * img[i * 4 + 2] + 0.587f * img[i * 4 + 1] + 0.114f * img[i * 4]) / 255f;
            P[i] = al[i * 4 + 3] / 255f;
        }
        var mI = new float[n];
        var mP = new float[n];
        var t1 = new float[n];
        var t2 = new float[n];
        Box(I, mI, w, h, radius);
        Box(P, mP, w, h, radius);
        for (int i = 0; i < n; i++) t1[i] = I[i] * I[i];
        Box(t1, t2, w, h, radius);
        for (int i = 0; i < n; i++) t2[i] -= mI[i] * mI[i];              // t2 = var(I)
        for (int i = 0; i < n; i++) t1[i] = I[i] * P[i];
        Box(t1, t1, w, h, radius);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (t1[i] - mI[i] * mP[i]) / (t2[i] + 1e-4f);
        var explained = new float[n];
        for (int i = 0; i < n; i++) explained[i] = Math.Abs(a[i]) * (float)Math.Sqrt(t2[i]);

        var sb = new System.Text.StringBuilder();
        sb.AppendFormat("radius={0} sharpen={1:F1} | baseline bandMean={2:F1}px\n", radius, sharpen, BandMean(al, w, h));

        // distribution of "explained" over partial pixels
        sb.Append("  explained histogram over partial pixels: ");
        long[] buckets = new long[5];
        long tot = 0;
        for (int i = 0; i < n; i++)
        {
            int A = al[i * 4 + 3];
            if (A == 0 || A >= 255) continue;
            tot++;
            float e = explained[i];
            if (e < 0.02f) buckets[0]++;
            else if (e < 0.05f) buckets[1]++;
            else if (e < 0.10f) buckets[2]++;
            else if (e < 0.20f) buckets[3]++;
            else buckets[4]++;
        }
        for (int b = 0; b < 5; b++)
            sb.AppendFormat("[{0}:{1:F1}%] ", b == 0 ? "<0.02" : b == 1 ? "0.02-0.05" : b == 2 ? "0.05-0.10" : b == 3 ? "0.10-0.20" : ">=0.20",
                tot == 0 ? 0 : 100.0 * buckets[b] / tot);
        sb.Append("\n");

        double[] ts = new double[] { 0.02, 0.05, 0.10, 0.20 };
        foreach (double t in ts)
        {
            var mod = (byte[])al.Clone();
            long halo = 0, follow = 0, zeroed = 0;
            double followAlpha = 0, haloAlpha = 0;
            for (int i = 0; i < n; i++)
            {
                byte A = al[i * 4 + 3];
                if (A == 0 || A >= 255) continue;
                if (explained[i] >= t) { follow++; followAlpha += A; }
                else
                {
                    halo++; haloAlpha += A;
                    double q = 0.5 + sharpen * (A / 255.0 - 0.5);
                    if (q < 0) q = 0;
                    if (q > 1) q = 1;
                    byte nq = (byte)Math.Round(q * 255);
                    mod[i * 4 + 3] = nq;
                    if (nq == 0) zeroed++;
                }
            }
            sb.AppendFormat("  t={0:F2}: halo={1,7} (meanA={2,3:F0}) follow={3,7} (meanA={4,3:F0}) | pushed to 0={5,7} | hairTouched=0 (by construction) | bandAfter={6,5:F1}px\n",
                t, halo, halo == 0 ? 0 : haloAlpha / halo, follow, follow == 0 ? 0 : followAlpha / follow, zeroed, BandMean(mod, w, h));
        }
        return sb.ToString();
    }
}
