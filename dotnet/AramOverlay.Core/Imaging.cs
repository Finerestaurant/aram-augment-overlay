using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace AramOverlay.Core;

/// <summary>
/// A decoded frame: BGRA8, tightly packed, no stride surprises.
///
/// Everything downstream -- the gate, the rarity strip, the OCR crops -- reads
/// pixels out of one of these, so decoding happens once per frame and the rest
/// is plain array work.
/// </summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public Frame(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Index(int x, int y) => (y * Width + x) * 4;

    /// <summary>The box, rescaled from 1920x1080 space onto this frame and clamped.</summary>
    public Box Fit(Box box)
    {
        double sx = (double)Width / Config.BaseW, sy = (double)Height / Config.BaseH;
        var b = box.Scaled(sx, sy);
        return new Box(Math.Clamp(b.X0, 0, Width), Math.Clamp(b.Y0, 0, Height),
                       Math.Clamp(b.X1, 0, Width), Math.Clamp(b.Y1, 0, Height));
    }

    public Frame Crop(Box box)
    {
        int w = Math.Max(0, box.Width), h = Math.Max(0, box.Height);
        var outBytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(Bgra, Index(box.X0, box.Y0 + y), outBytes, y * w * 4, w * 4);
        return new Frame(w, h, outBytes);
    }

    /// <summary>Mean of the grayscale values inside the box, as the Python side
    /// computes it: the OpenCV luma weights over BGR.</summary>
    public double Mean(Box box)
    {
        var b = Fit(box);
        long n = 0;
        double sum = 0;
        for (int y = b.Y0; y < b.Y1; y++)
        {
            for (int x = b.X0; x < b.X1; x++)
            {
                int i = Index(x, y);
                sum += Gray(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
                n++;
            }
        }
        return n == 0 ? 0.0 : sum / n;
    }

    /// <summary>OpenCV's BGR->GRAY coefficients, rounded the way cvtColor does.</summary>
    public static double Gray(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    public byte[] ToGray()
    {
        var gray = new byte[Width * Height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            gray[i] = (byte)Math.Clamp((int)Math.Round(Gray(Bgra[p + 2], Bgra[p + 1], Bgra[p])), 0, 255);
        return gray;
    }
}

public static class Imaging
{
    /// <summary>Decode PNG or JPEG bytes -- whatever OBS was asked for.</summary>
    public static async Task<Frame> DecodeAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream.GetOutputStreamAt(0));
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        return FromSoftwareBitmap(bitmap);
    }

    // Pixels move through WinRT's own IBuffer rather than the classic
    // IMemoryBufferByteAccess COM interface: under CsWinRT a projected object
    // will not cast to a hand-declared ComImport interface, and this way there
    // is no unsafe code to get wrong either.
    public static Frame FromSoftwareBitmap(SoftwareBitmap bitmap)
    {
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
        bitmap.CopyToBuffer(buffer);
        CryptographicBuffer.CopyToByteArray(buffer, out byte[] outBytes);
        return new Frame(w, h, outBytes);
    }

    public static SoftwareBitmap ToSoftwareBitmap(Frame frame)
    {
        return SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(frame.Bgra),
            BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// INTER_AREA over all four channels -- how the detection frame is brought
    /// down from full resolution, and how OpenCV does it on the Python side.
    /// </summary>
    public static Frame ResizeArea(Frame src, int width, int height)
    {
        if (width == src.Width && height == src.Height)
            return src;

        double scaleX = (double)src.Width / width, scaleY = (double)src.Height / height;
        var dst = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            double sy0 = y * scaleY, sy1 = (y + 1) * scaleY;
            int iy0 = (int)Math.Floor(sy0), iy1 = Math.Min(src.Height, (int)Math.Ceiling(sy1));
            for (int x = 0; x < width; x++)
            {
                double sx0 = x * scaleX, sx1 = (x + 1) * scaleX;
                int ix0 = (int)Math.Floor(sx0), ix1 = Math.Min(src.Width, (int)Math.Ceiling(sx1));

                double b = 0, g = 0, r = 0, a = 0, weight = 0;
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
                        double w = wx * wy;
                        int p = (sy * src.Width + sx) * 4;
                        b += src.Bgra[p] * w;
                        g += src.Bgra[p + 1] * w;
                        r += src.Bgra[p + 2] * w;
                        a += src.Bgra[p + 3] * w;
                        weight += w;
                    }
                }
                if (weight <= 0)
                    weight = 1;
                int q = (y * width + x) * 4;
                dst[q] = Round(b / weight);
                dst[q + 1] = Round(g / weight);
                dst[q + 2] = Round(r / weight);
                dst[q + 3] = Round(a / weight);
            }
        }
        return new Frame(width, height, dst);
    }

    private static byte Round(double v) =>
        (byte)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>
    /// Lanczos-3 resample, matching what PIL was asked for on the Python side.
    ///
    /// The OCR path upscales small card titles -- 범람 first reads at 4x -- and
    /// which filter does it changes what the recogniser sees, so this is the
    /// same filter rather than whatever a graphics API defaults to.
    /// </summary>
    public static Frame Resize(Frame src, int width, int height)
    {
        if (width == src.Width && height == src.Height)
            return src;
        var horizontal = ResampleAxis(src.Bgra, src.Width, src.Height, width, horizontalPass: true);
        var result = ResampleAxis(horizontal, width, src.Height, height, horizontalPass: false);
        return new Frame(width, height, result);
    }

    private static byte[] ResampleAxis(byte[] src, int srcW, int srcH, int outSize, bool horizontalPass)
    {
        int inSize = horizontalPass ? srcW : srcH;
        int otherSize = horizontalPass ? srcH : srcW;
        int outW = horizontalPass ? outSize : srcW;
        var dst = new byte[(horizontalPass ? outSize * srcH : srcW * outSize) * 4];

        double scale = (double)inSize / outSize;
        double support = 3.0 * Math.Max(1.0, scale);      // PIL widens the kernel when shrinking
        double filterScale = Math.Max(1.0, scale);

        // Sized once for the widest kernel any output pixel can need, rather
        // than per iteration -- a stackalloc in this loop is a stack overflow
        // waiting for a large enough image.
        var weights = new double[inSize + 1];

        for (int o = 0; o < outSize; o++)
        {
            double centre = (o + 0.5) * scale;
            int lo = Math.Max(0, (int)Math.Ceiling(centre - support - 0.5));
            int hi = Math.Min(inSize, (int)Math.Floor(centre + support + 0.5) + 1);

            double total = 0;
            for (int i = lo; i < hi; i++)
            {
                double w = Lanczos3((i + 0.5 - centre) / filterScale);
                weights[i - lo] = w;
                total += w;
            }
            if (total == 0)
                total = 1;

            for (int other = 0; other < otherSize; other++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (int i = lo; i < hi; i++)
                {
                    int sx = horizontalPass ? i : other;
                    int sy = horizontalPass ? other : i;
                    int p = (sy * srcW + sx) * 4;
                    double w = weights[i - lo];
                    b += src[p] * w;
                    g += src[p + 1] * w;
                    r += src[p + 2] * w;
                    a += src[p + 3] * w;
                }
                int dx = horizontalPass ? o : other;
                int dy = horizontalPass ? other : o;
                int q = (dy * outW + dx) * 4;
                dst[q] = Clamp(b / total);
                dst[q + 1] = Clamp(g / total);
                dst[q + 2] = Clamp(r / total);
                dst[q + 3] = Clamp(a / total);
            }
        }
        return dst;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x >= 3.0)
            return 0.0;
        if (x < 1e-9)
            return 1.0;
        double px = Math.PI * x;
        return 3.0 * Math.Sin(px) * Math.Sin(px / 3.0) / (px * px);
    }
}
