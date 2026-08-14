using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

internal sealed class LifxSessionIo : IAsyncDisposable
{
    private readonly UdpClient? udp;

    public LifxSessionIo(IPEndPoint endpoint, LifxDatagramSender? sender)
    {
        uint source = (uint)Random.Shared.Next(2, int.MaxValue);
        int sequence = Random.Shared.Next(0, 256);
        Packets = new LifxPackets(source, () => (byte)Interlocked.Increment(ref sequence));
        if (sender != null)
        {
            Send = sender;
        }
        else
        {
            this.udp = new UdpClient(endpoint.AddressFamily);
            UdpClient socket = this.udp;
            Send = async (ep, packet, ct) =>
            {
                await socket.SendAsync(packet, ep, ct);
            };
        }
    }

    public LifxPackets Packets { get; }

    public LifxDatagramSender Send { get; }

    public ValueTask DisposeAsync()
    {
        this.udp?.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class LifxMapping
{
    public static IPEndPoint RequireEndpoint(PluginOutputMappingConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
        {
            throw new InvalidOperationException("Destination address (the LIFX device IP) is required.");
        }

        if (!IPAddress.TryParse(config.DestinationAddress.Trim(), out IPAddress? ip))
        {
            throw new InvalidOperationException(
                $"Destination address '{config.DestinationAddress}' is not a valid IP address.");
        }

        return new IPEndPoint(ip, LifxConstants.Port);
    }
}
