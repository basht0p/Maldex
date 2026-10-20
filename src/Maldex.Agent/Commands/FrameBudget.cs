using System.Text.Json;
using Maldex.Core.Agent;

namespace Maldex.Agent.Commands;

public static class FrameBudget
{
    /// <summary>True when the screenshot, once serialised with the real protocol encoder (escaping included), fits one frame.</summary>
    public static bool Fits(string format, byte[] data)
    {
        var msg = new ScreenshotMessage(DateTimeOffset.UtcNow, format, Convert.ToBase64String(data));
        return JsonSerializer.SerializeToUtf8Bytes<AgentMessage>(msg, AgentJson.Options).Length <= AgentFrameReader.MaxFrameBytes;
    }
}
