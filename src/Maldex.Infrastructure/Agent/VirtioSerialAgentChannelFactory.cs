using System.Net.Sockets;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Agent;

public sealed class VirtioSerialAgentChannelFactory(IOptionsMonitor<AgentChannelOptions> options) : IAgentChannelFactory
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var path = Path.GetFullPath(Path.Combine(o.RunDir, vm.Name, "agent.sock"));
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                return new VirtioSerialAgentChannel(new NetworkStream(socket, ownsSocket: true));
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            if (TimeProvider.System.GetElapsedTime(started) >= o.ConnectTimeout)
                throw new TimeoutException($"Agent socket {path} did not become available within {o.ConnectTimeout}.");
            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }
}
