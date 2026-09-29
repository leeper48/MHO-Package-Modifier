using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MhoPackageModifier;

/// <summary>
/// Image (PNG, JPG, BMP) -> DXT1 / DXT5 mip chain, for --import-texture, so no outside tool is needed. Format: DXT1 when
/// every alpha is 255 (opaque) or when asked for a 1-bit cut (alpha above --split kept, the masked materials' 1/3 clip
/// = 85), DXT5 otherwise (soft alpha). Mips: box filter down to 1x1; for a DXT1 cut, colour premultiplied and the
/// alpha re-cut at half coverage per level (no dark fringes, the cut-out holds from a distance), as make_mips.py did.
/// The block encoder fits each 4x4 block's colours along their main axis (PCA), then picks the nearest palette entry.
/// With refine (the Ext Mod Manager's icon snapshots, 2026-09-28: smooth rendered shading came out blotchy, 25-30 dB),
/// the endpoints are then improved by least squares on the chosen indices (a few rounds, kept only when the error with
/// the real 565 palette drops), and DXT5 fits colour only to texels that aren't fully transparent (a snapshot's empty
/// background is black and pulled edge blocks dark). Off by default, so existing imports encode exactly as before.
/// Refine also (2026-09-28, Kurt: still a visible loss on a Magik store image) weighs errors as the eye does (luma
/// 0.30 / 0.59 / 0.11: green most) and, for four-colour blocks, tries a cluster fit: the texels in order along the main
/// axis, every split into the palette's four positions solved by least squares (closed form from running sums), the
/// best kept if it beats the other fits with the real 565 palette.
/// </summary>
static class TextureEncode
{
    public sealed record Result(string FourCC, int Width, int Height, List<(int W, int H, byte[] Data)> Levels);

    /// <param name="format">"dxt1", "dxt5" or null (choose).</param>
    public static Result FromImage(string path, string? format, int split, float scale, bool noMips = false, int maxSize = 0, bool refine = false)
    {
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        if (w % 4 != 0 || h % 4 != 0) throw new InvalidDataException($"{w}x{h}: DXT needs sizes divisible by 4");
        var rgba = new byte[w * h * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w * 4);
                for (int x = 0; x < w; x++)                                    // BGRA in memory -> RGBA
                {
                    int s = x * 4, d = (y * w + x) * 4;
                    rgba[d] = Scale(row[s + 2], scale); rgba[d + 1] = Scale(row[s + 1], scale); rgba[d + 2] = Scale(row[s], scale); rgba[d + 3] = row[s + 3];
                }
            }
        }
        finally { bmp.UnlockBits(bd); }

        bool opaque = true, soft = false;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] != 255) opaque = false; if (rgba[i] != 0 && rgba[i] != 255) soft = true; }
        string fmt = format?.ToLowerInvariant() ?? (opaque || !soft ? "dxt1" : "dxt5");
        if (fmt is not ("dxt1" or "dxt5")) throw new ArgumentException($"format '{format}' (dxt1 or dxt5)");
        bool cut = fmt == "dxt1" && !opaque;
        if (cut) for (int i = 3; i < rgba.Length; i += 4) rgba[i] = rgba[i] > split ? (byte)255 : (byte)0;

        var levels = new List<(int, int, byte[])>();
        byte[] level = rgba; int lw = w, lh = h;
        // --max-size N: halve (same filter as the mips) until the image fits.
        while (maxSize > 0 && Math.Max(lw, lh) > maxSize && (lw > 1 || lh > 1)) (level, lw, lh) = Downsample(level, lw, lh, cut);
        w = lw; h = lh;
        while (true)
        {
            levels.Add((lw, lh, fmt == "dxt1" ? EncodeDxt1(level, lw, lh, !opaque, refine) : EncodeDxt5(level, lw, lh, refine)));
            if (noMips || (lw == 1 && lh == 1)) break;
            (level, lw, lh) = Downsample(level, lw, lh, cut);
        }
        return new Result(fmt == "dxt1" ? "DXT1" : "DXT5", w, h, levels);
    }

    static byte Scale(byte v, float s) => s == 1f ? v : (byte)Math.Clamp((int)MathF.Round(v * s), 0, 255);

    static (byte[], int, int) Downsample(byte[] src, int w, int h, bool cut)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                float r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy), s = (sy * w + sx) * 4;
                        float al = src[s + 3] / 255f, wgt = cut ? al : 1f;           // premultiplied for a cut-out
                        r += src[s] * wgt; g += src[s + 1] * wgt; b += src[s + 2] * wgt; a += src[s + 3]; n++;
                    }
                int d = (y * nw + x) * 4;
                float cov = a / n, div = cut ? MathF.Max(a / 255f, 1e-6f) : n;
                dst[d] = (byte)Math.Clamp(r / div, 0, 255); dst[d + 1] = (byte)Math.Clamp(g / div, 0, 255); dst[d + 2] = (byte)Math.Clamp(b / div, 0, 255);
                dst[d + 3] = cut ? (cov >= 128 ? (byte)255 : (byte)0) : (byte)MathF.Round(cov);
            }
        return (dst, nw, nh);
    }

    /// <summary>The 16 texels of block (bx, by), clamped at the image edge (levels under 4x4 repeat their pixels).</summary>
    static (Vector3[] C, byte[] A) Block(byte[] img, int w, int h, int bx, int by)
    {
        var c = new Vector3[16]; var a = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int x = Math.Min(w - 1, bx * 4 + i % 4), y = Math.Min(h - 1, by * 4 + i / 4), s = (y * w + x) * 4;
            c[i] = new Vector3(img[s], img[s + 1], img[s + 2]); a[i] = img[s + 3];
        }
        return (c, a);
    }

    static ushort To565(Vector3 c) => (ushort)(((int)MathF.Round(Math.Clamp(c.X, 0, 255) * 31 / 255f) << 11) | ((int)MathF.Round(Math.Clamp(c.Y, 0, 255) * 63 / 255f) << 5) | (int)MathF.Round(Math.Clamp(c.Z, 0, 255) * 31 / 255f));
    static Vector3 From565(ushort v) => new(((v >> 11) & 31) * 255f / 31, ((v >> 5) & 63) * 255f / 63, (v & 31) * 255f / 31);

    /// <summary>Endpoints along the colours' main axis (PCA by power iteration), over the texels that count.</summary>
    static (Vector3 Lo, Vector3 Hi) Endpoints(Vector3[] c, bool[] use)
    {
        var pts = c.Where((_, i) => use[i]).ToArray();
        if (pts.Length == 0) return (Vector3.Zero, Vector3.Zero);
        var mean = pts.Aggregate(Vector3.Zero, (s, p) => s + p) / pts.Length;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in pts) { var d = p - mean; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        var axis = new Vector3(1, 1, 1);
        for (int k = 0; k < 8; k++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = axis.Length(); if (len < 1e-6f) { axis = new Vector3(1, 1, 1); break; } axis /= len;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in pts) { float t = Vector3.Dot(p - mean, axis); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
        return (mean + axis * lo, mean + axis * hi);
    }

    // Perceptual weights for refine (squared error per channel, R G B): the eye is most sensitive to green.
    static readonly Vector3 Luma = new(0.30f, 0.59f, 0.11f);

    static byte[] ColorBlock(Vector3[] c, bool[] use, bool threeColor, bool refine = false)
    {
        var (lo, hi) = Endpoints(c, use);
        if (refine)
        {
            (lo, hi) = Refine(c, use, threeColor, lo, hi);
            if (!threeColor && ClusterFit(c, use) is { } cf && Error(c, use, false, cf.Lo, cf.Hi, true) < Error(c, use, false, lo, hi, true)) (lo, hi) = cf;
        }
        ushort a = To565(hi), b = To565(lo);
        // 4-colour mode needs c0 > c1; 3-colour mode (index 3 = transparent black) needs c0 <= c1.
        if (threeColor ? a > b : a < b) (a, b) = (b, a);
        if (!threeColor && a == b) { if (b > 0) b--; else a++; }
        Vector3 p0 = From565(a), p1 = From565(b);
        var pal = threeColor ? new[] { p0, p1, (p0 + p1) / 2 } : new[] { p0, p1, (2 * p0 + p1) / 3, (p0 + 2 * p1) / 3 };
        uint idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 3;
            if (use[i])
            {
                float bd = float.MaxValue;
                for (int k = 0; k < pal.Length; k++) { var dv = c[i] - pal[k]; float d = refine ? Vector3.Dot(dv * dv, Luma) : dv.LengthSquared(); if (d < bd) { bd = d; best = k; } }
            }
            idx |= (uint)best << (2 * i);
        }
        var o = new byte[8];
        BitConverter.GetBytes(a).CopyTo(o, 0); BitConverter.GetBytes(b).CopyTo(o, 2); BitConverter.GetBytes(idx).CopyTo(o, 4);
        return o;
    }

    /// <summary>The palette a pair of 565 endpoints gives (the order ColorBlock writes), and the error of the best index per texel.</summary>
    static float Error(Vector3[] c, bool[] use, bool threeColor, Vector3 lo, Vector3 hi, bool perceptual = false)
    {
        ushort a = To565(hi), b = To565(lo);
        if (threeColor ? a > b : a < b) (a, b) = (b, a);
        Vector3 p0 = From565(a), p1 = From565(b);
        var pal = threeColor ? new[] { p0, p1, (p0 + p1) / 2 } : new[] { p0, p1, (2 * p0 + p1) / 3, (p0 + 2 * p1) / 3 };
        float e = 0;
        for (int i = 0; i < 16; i++) if (use[i]) { float bd = float.MaxValue; foreach (var p in pal) { var dv = c[i] - p; bd = MathF.Min(bd, perceptual ? Vector3.Dot(dv * dv, Luma) : dv.LengthSquared()); } e += bd; }
        return e;
    }

    /// <summary>
    /// Cluster fit (four-colour mode): the texels sorted along the main axis, every split into four runs (palette
    /// positions 1, 2/3, 1/3, 0 of the way from hi to lo) solved by least squares in the perceptual space; the endpoints
    /// of the split with the least error. Null when there's nothing to fit.
    /// </summary>
    static (Vector3 Lo, Vector3 Hi)? ClusterFit(Vector3[] c, bool[] use)
    {
        var w = new Vector3(MathF.Sqrt(Luma.X), MathF.Sqrt(Luma.Y), MathF.Sqrt(Luma.Z));   // perceptual space: x·w
        var pts = new List<Vector3>();
        for (int i = 0; i < 16; i++) if (use[i]) pts.Add(c[i] * w);
        int n = pts.Count;
        if (n < 3) return null;
        var mean = pts.Aggregate(Vector3.Zero, (s, p) => s + p) / n;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in pts) { var d = p - mean; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        var axis = new Vector3(1, 1, 1);
        for (int k = 0; k < 8; k++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = axis.Length(); if (len < 1e-6f) return null; axis /= len;
        }
        pts.Sort((a, b) => Vector3.Dot(b, axis).CompareTo(Vector3.Dot(a, axis)));   // hi end first
        // Running sums over the sorted points.
        var pre = new Vector3[n + 1];
        for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + pts[i];
        float total2 = pts.Sum(p => p.LengthSquared());
        float bestE = float.MaxValue; Vector3 bestHi = default, bestLo = default;
        // Runs: [0,i) at hi (α 1), [i,j) at 2/3, [j,k) at 1/3, [k,n) at lo (α 0).
        for (int i = 0; i <= n; i++)
            for (int j = i; j <= n; j++)
                for (int k = j; k <= n; k++)
                {
                    int n1 = i, n2 = j - i, n3 = k - j, n4 = n - k;
                    float saa = n1 + n2 * (4f / 9) + n3 * (1f / 9);
                    float sbb = n4 + n2 * (1f / 9) + n3 * (4f / 9);
                    float sab = (n2 + n3) * (2f / 9);
                    Vector3 s1 = pre[i], s2 = pre[j] - pre[i], s3 = pre[k] - pre[j], s4 = pre[n] - pre[k];
                    var sac = s1 + s2 * (2f / 3) + s3 * (1f / 3);
                    var sbc = s4 + s2 * (1f / 3) + s3 * (2f / 3);
                    float det = saa * sbb - sab * sab;
                    if (MathF.Abs(det) < 1e-6f) continue;
                    var hi = (sac * sbb - sbc * sab) / det;
                    var lo = (sbc * saa - sac * sab) / det;
                    // Σ|αhi + βlo − c|² = Σ|c|² + saa|hi|² + sbb|lo|² + 2 sab hi·lo − 2 hi·sac − 2 lo·sbc
                    float e = total2 + saa * hi.LengthSquared() + sbb * lo.LengthSquared() + 2 * sab * Vector3.Dot(hi, lo) - 2 * Vector3.Dot(hi, sac) - 2 * Vector3.Dot(lo, sbc);
                    if (e < bestE) { bestE = e; bestHi = hi; bestLo = lo; }
                }
        if (bestE == float.MaxValue) return null;
        var max = new Vector3(255);
        return (Vector3.Clamp(bestLo / w, Vector3.Zero, max), Vector3.Clamp(bestHi / w, Vector3.Zero, max));
    }

    /// <summary>
    /// Least-squares endpoints: with each texel's palette position fixed (1, 0, 2/3, 1/3 of the way from hi to lo; 1/2 in
    /// three-colour mode), hi and lo that minimise the squared error; then re-assign and repeat. Kept only while the real
    /// (565-rounded) error drops, so the result is never worse than the PCA fit.
    /// </summary>
    static (Vector3 Lo, Vector3 Hi) Refine(Vector3[] c, bool[] use, bool threeColor, Vector3 lo, Vector3 hi)
    {
        float best = Error(c, use, threeColor, lo, hi);
        float[] weights = threeColor ? [1f, 0f, 0.5f] : [1f, 0f, 2f / 3, 1f / 3];
        for (int round = 0; round < 4; round++)
        {
            float saa = 0, sab = 0, sbb = 0; Vector3 sac = Vector3.Zero, sbc = Vector3.Zero;
            for (int i = 0; i < 16; i++)
            {
                if (!use[i]) continue;
                // nearest palette position on the (unrounded) line from hi to lo
                int k = 0; float bd = float.MaxValue;
                for (int j = 0; j < weights.Length; j++) { var p = hi * weights[j] + lo * (1 - weights[j]); float d = Vector3.DistanceSquared(c[i], p); if (d < bd) { bd = d; k = j; } }
                float al = weights[k], be = 1 - al;
                saa += al * al; sab += al * be; sbb += be * be; sac += c[i] * al; sbc += c[i] * be;
            }
            float det = saa * sbb - sab * sab;
            if (MathF.Abs(det) < 1e-6f) break;
            var nhi = (sac * sbb - sbc * sab) / det;
            var nlo = (sbc * saa - sac * sab) / det;
            nhi = Vector3.Clamp(nhi, Vector3.Zero, new Vector3(255)); nlo = Vector3.Clamp(nlo, Vector3.Zero, new Vector3(255));
            float e = Error(c, use, threeColor, nlo, nhi);
            if (e >= best - 1e-3f) break;
            best = e; lo = nlo; hi = nhi;
        }
        return (lo, hi);
    }

    static byte[] EncodeDxt1(byte[] img, int w, int h, bool alpha, bool refine = false)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var o = new byte[bw * bh * 8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var (c, a) = Block(img, w, h, bx, by);
                var use = a.Select(v => !alpha || v >= 128).ToArray();
                bool anyCut = alpha && use.Any(u => !u);
                ColorBlock(c, use, anyCut, refine).CopyTo(o, (by * bw + bx) * 8);
            }
        return o;
    }

    static byte[] EncodeDxt5(byte[] img, int w, int h, bool refine = false)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var o = new byte[bw * bh * 16];
        var all = Enumerable.Repeat(true, 16).ToArray();
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var (c, a) = Block(img, w, h, bx, by);
                byte a0 = a.Max(), a1 = a.Min();
                var blk = new byte[16];
                blk[0] = a0; blk[1] = a1;
                // 8-value alpha ramp (a0 > a1); equal endpoints -> every index 0.
                var ramp = new float[8]; ramp[0] = a0; ramp[1] = a1;
                for (int k = 1; k < 7; k++) ramp[k + 1] = ((7 - k) * a0 + k * a1) / 7f;
                ulong bits = 0;
                for (int i = 0; i < 16; i++)
                {
                    int best = 0; float bd = float.MaxValue;
                    if (a0 != a1) for (int k = 0; k < 8; k++) { float d = MathF.Abs(a[i] - ramp[k]); if (d < bd) { bd = d; best = k; } }
                    bits |= (ulong)best << (3 * i);
                }
                for (int k = 0; k < 6; k++) blk[2 + k] = (byte)(bits >> (8 * k));
                // refine: colour fitted to the texels that show (a fully transparent texel's colour is never seen)
                var use = refine && a.Any(v => v > 0) ? a.Select(v => v > 0).ToArray() : all;
                ColorBlock(c, use, false, refine).CopyTo(blk, 8);
                blk.CopyTo(o, (by * bw + bx) * 16);
            }
        return o;
    }
}
