namespace AramOverlay.Core;

/// <summary>A single-channel 8-bit image.</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public GrayImage Crop(int x0, int y0, int w, int h)
    {
        var outBytes = new byte[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(Pixels, (y0 + y) * Width + x0, outBytes, y * w, w);
        return new GrayImage(w, h, outBytes);
    }
}

/// <summary>
/// The four OpenCV operations this tool actually uses, reimplemented.
///
/// Pulling in OpenCvSharp for these would add ~30 MB of native DLLs to what is
/// otherwise a self-contained exe, and the usage is narrow: one colour
/// conversion, a 3x3 Sobel, an area-averaging resize of a fixed template, and a
/// correlation at a known position with no search window. Each is written to
/// match OpenCV's own arithmetic, because the gate thresholds are measurements
/// of what OpenCV returned.
/// </summary>
public static class Cv
{
    /// <summary>
    /// BGR to gray, BT.601 weights, rounded half to even.
    ///
    /// Bit-exact agreement with OpenCV is not reachable and was not chased.
    /// Measured over 300k random colours, cvtColor matches none of the obvious
    /// implementations exactly: the classic fixed point (1868/9617/4899 >> 14)
    /// agrees on 99.74%, this float form on 99.86%, and every other shift is
    /// worse -- OpenCV's SIMD path takes different routes for different pixels,
    /// so its own output is not a stable target across builds or CPUs. The
    /// residual disagreement is +/-1 on a quarter of a percent of pixels, which
    /// moves a region average by ~0.002 against thresholds that have margins of
    /// 10 or more. SelfTest holds the port to the verdicts rather than the bytes.
    /// </summary>
    public static GrayImage ToGray(Frame frame)
    {
        var gray = new byte[frame.Width * frame.Height];
        var src = frame.Bgra;
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            gray[i] = (byte)Math.Round(0.114 * src[p] + 0.587 * src[p + 1] + 0.299 * src[p + 2],
                                       MidpointRounding.ToEven);
        return new GrayImage(frame.Width, frame.Height, gray);
    }

    /// <summary>
    /// Gradient magnitude, normalised to 0..255.
    ///
    /// The reroll button is a translucent panel, so the map shows through it and
    /// the raw pixels change with whatever is behind -- the same button scored
    /// 0.46 on one map and 0.99 on another. The shape does not change, so the
    /// match runs on gradients: over 29 confirmed windows and 14 frames without,
    /// the worst positive and best negative sit 0.64 apart against 0.25 for raw
    /// grayscale.
    /// </summary>
    public static byte[] Edges(GrayImage img)
    {
        int w = img.Width, h = img.Height;
        var mag = new float[w * h];
        float min = float.MaxValue, max = float.MinValue;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Sobel 3x3 with BORDER_REFLECT_101, OpenCV's default.
                float gx = 0, gy = 0;
                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = Reflect101(y + ky, h);
                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = Reflect101(x + kx, w);
                        float v = img.Pixels[sy * w + sx];
                        gx += v * SobelX[(ky + 1) * 3 + (kx + 1)];
                        gy += v * SobelY[(ky + 1) * 3 + (kx + 1)];
                    }
                }
                float m = MathF.Sqrt(gx * gx + gy * gy);
                mag[y * w + x] = m;
                if (m < min) min = m;
                if (m > max) max = m;
            }
        }

        var outBytes = new byte[w * h];
        double span = max - min;
        if (span > 0)
        {
            double scale = 255.0 / span;
            for (int i = 0; i < mag.Length; i++)
                // numpy's astype(uint8) truncates; it does not round.
                outBytes[i] = (byte)Math.Clamp((int)((mag[i] - min) * scale), 0, 255);
        }
        return outBytes;
    }

    private static readonly float[] SobelX = { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
    private static readonly float[] SobelY = { -1, -2, -1, 0, 0, 0, 1, 2, 1 };

    private static int Reflect101(int i, int len)
    {
        if (len == 1)
            return 0;
        while (i < 0 || i >= len)
        {
            if (i < 0)
                i = -i;
            if (i >= len)
                i = 2 * (len - 1) - i;
        }
        return i;
    }

    /// <summary>
    /// TM_CCOEFF_NORMED at one position: both images are the same size, so there
    /// is nothing to slide. The degenerate-denominator handling is OpenCV's --
    /// a flat patch scores 0 rather than dividing by zero.
    /// </summary>
    public static double MatchCcoeffNormed(byte[] image, byte[] template)
    {
        int n = image.Length;
        double imageMean = 0, templateMean = 0;
        for (int i = 0; i < n; i++)
        {
            imageMean += image[i];
            templateMean += template[i];
        }
        imageMean /= n;
        templateMean /= n;

        double num = 0, imageSq = 0, templateSq = 0;
        for (int i = 0; i < n; i++)
        {
            double a = image[i] - imageMean, b = template[i] - templateMean;
            num += a * b;
            imageSq += a * a;
            templateSq += b * b;
        }

        double denom = Math.Sqrt(imageSq) * Math.Sqrt(templateSq);
        if (Math.Abs(num) < denom)
            return num / denom;
        if (Math.Abs(num) < denom * 1.125)
            return num > 0 ? 1.0 : -1.0;
        return 0.0;
    }

    /// <summary>
    /// INTER_AREA: each output pixel is the average of the source pixels its
    /// footprint covers, edges weighted by how much of them falls inside. Used
    /// only to bring the templates down to the detection frame's scale.
    /// </summary>
    public static GrayImage ResizeArea(GrayImage src, int width, int height)
    {
        if (width == src.Width && height == src.Height)
            return src;

        double scaleX = (double)src.Width / width, scaleY = (double)src.Height / height;
        var outBytes = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            double sy0 = y * scaleY, sy1 = (y + 1) * scaleY;
            int iy0 = (int)Math.Floor(sy0), iy1 = Math.Min(src.Height, (int)Math.Ceiling(sy1));
            for (int x = 0; x < width; x++)
            {
                double sx0 = x * scaleX, sx1 = (x + 1) * scaleX;
                int ix0 = (int)Math.Floor(sx0), ix1 = Math.Min(src.Width, (int)Math.Ceiling(sx1));

                double sum = 0, weight = 0;
                for (int sy = iy0; sy < iy1; sy++)
                {
                    double wy = Math.Min(sy + 1, sy1) - Math.Max(sy, sy0);
                    if (wy <= 0)
                        continue;
                    for (int sx = ix0; sx < ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1, sx1) - Math.Max(sx, sx0);
                        if (wx <= 0)
                            continue;
                        sum += src.Pixels[sy * src.Width + sx] * wx * wy;
                        weight += wx * wy;
                    }
                }
                outBytes[y * width + x] = (byte)Math.Clamp(
                    (int)Math.Round(weight > 0 ? sum / weight : 0, MidpointRounding.AwayFromZero),
                    0, 255);
            }
        }
        return new GrayImage(width, height, outBytes);
    }

    /// <summary>OpenCV's 8-bit BGR->HSV: hue 0..179, saturation and value 0..255.</summary>
    public static (double Hue, double Sat, double Val) ToHsv(byte b, byte g, byte r)
    {
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int diff = max - min;

        double hue;
        if (diff == 0)
            hue = 0;
        else if (max == r)
            hue = 30.0 * (g - b) / diff;
        else if (max == g)
            hue = 60.0 + 30.0 * (b - r) / diff;
        else
            hue = 120.0 + 30.0 * (r - g) / diff;
        if (hue < 0)
            hue += 180;

        double sat = max == 0 ? 0 : 255.0 * diff / max;
        return (hue, sat, max);
    }
}
