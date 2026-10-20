using System.Runtime.CompilerServices;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>Emits a <see cref="ScreenshotMessage"/> every interval (default 10 s). A failed grab is skipped.</summary>
public sealed class ScreenshotEventSource(IScreenshotGrabber grabber, TimeProvider time, TimeSpan? interval = null) : IEventSource
{
    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(10), time);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) yield break;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            ScreenshotImage? image = null;
            try { image = grabber.Grab(); }
            catch (Exception ex) { AgentLog.Write("Screenshot failed: " + ex.Message); }

            if (image is not null)
                yield return new ScreenshotMessage(time.GetUtcNow(), image.Format, Convert.ToBase64String(image.Data));
        }
    }
}
