namespace Maldex.Agent.Commands;

/// <summary>An encoded screen image. Format is "png" or "jpeg".</summary>
public sealed record ScreenshotImage(string Format, byte[] Data);

public interface IScreenshotGrabber
{
    /// <summary>Captures the screen; null when nothing could be captured. Result must fit the frame cap once base64-encoded.</summary>
    ScreenshotImage? Grab();
}
