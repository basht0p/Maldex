using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Maldex.Agent.Commands;

namespace Maldex.Agent.Windows;

/// <summary>Captures the primary screen with GDI. PNG normally; shrinks to JPEG until the real serialised frame fits.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenshotGrabber : IScreenshotGrabber
{
    private static readonly int[] Widths = [1280, 960, 640];
    private static readonly long[] Qualities = [75, 50, 30];

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    /// <summary>Call once at startup so the capture covers the whole physical screen on scaled displays.</summary>
    public static void EnableDpiAwareness()
    {
        try { _ = SetProcessDpiAwareness(2); } catch { /* best effort */ }
    }

    public ScreenshotImage? Grab()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w <= 0 || h <= 0) return null;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));

        var png = Encode(bmp, ImageFormat.Png, null);
        if (FrameBudget.Fits("png", png)) return new ScreenshotImage("png", png);

        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        foreach (var width in Widths)
        {
            var tw = Math.Min(w, width);
            using var small = new Bitmap(bmp, tw, Math.Max(1, (int)((long)h * tw / w)));
            foreach (var q in Qualities)
            {
                var jpeg = Encode(small, ImageFormat.Jpeg, (jpegCodec, q));
                if (FrameBudget.Fits("jpeg", jpeg)) return new ScreenshotImage("jpeg", jpeg);
            }
        }
        return null;
    }

    private static byte[] Encode(Bitmap bmp, ImageFormat format, (ImageCodecInfo Codec, long Quality)? jpeg)
    {
        using var ms = new MemoryStream();
        if (jpeg is { } j)
        {
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, j.Quality);
            bmp.Save(ms, j.Codec, ps);
        }
        else
        {
            bmp.Save(ms, format);
        }
        return ms.ToArray();
    }
}
