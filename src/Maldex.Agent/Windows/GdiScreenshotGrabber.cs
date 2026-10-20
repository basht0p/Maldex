using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;

namespace Maldex.Agent.Windows;

/// <summary>Captures the primary screen with GDI. PNG normally; downscaled JPEG when it would break the frame cap.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenshotGrabber : IScreenshotGrabber
{
    // Base64 inflates by 4/3; leave headroom for the JSON envelope.
    private const int MaxRawBytes = (AgentFrameReader.MaxFrameBytes - 4096) / 4 * 3;
    private const int MaxWidth = 1280;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public ScreenshotImage? Grab()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w <= 0 || h <= 0) return null;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));

        var png = Encode(bmp, ImageFormat.Png);
        if (png.Length <= MaxRawBytes) return new ScreenshotImage("png", png);

        var tw = Math.Min(w, MaxWidth);
        using var small = new Bitmap(bmp, tw, (int)((long)h * tw / w));
        var jpeg = Encode(small, ImageFormat.Jpeg);
        return jpeg.Length <= MaxRawBytes ? new ScreenshotImage("jpeg", jpeg) : null;
    }

    private static byte[] Encode(Bitmap bmp, ImageFormat format)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, format);
        return ms.ToArray();
    }
}
