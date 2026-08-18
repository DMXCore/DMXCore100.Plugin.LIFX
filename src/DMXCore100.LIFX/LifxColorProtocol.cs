using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// Single-zone LIFX color output: an RGB / RGBW (+CT) channel slice, 8- or
/// 16-bit per the <see cref="LifxColorMode"/>, becomes SetColor UDP
/// datagrams to port 56700.
/// </summary>
internal sealed class LifxColorProtocol : IPluginOutputProtocol
{
    private readonly LifxColorMode mode;
    private readonly LifxDiscovery discovery;
    private readonly LifxDatagramSender? sender;

    public LifxColorProtocol(LifxColorMode mode, LifxDiscovery discovery, LifxDatagramSender? sender = null)
    {
        this.mode = mode;
        this.discovery = discovery;
        this.sender = sender;
    }

    public int GetChannelCount(PluginOutputMappingConfig config) => this.mode.ChannelCount;

    public Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = LifxMapping.RequireEndpoint(config);
        byte[] target = this.discovery.TargetFor(endpoint.Address.ToString());
        return Task.FromResult<IPluginOutputSession>(
            new LifxColorSession(this.mode, endpoint, target, this.sender));
    }

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LifxLight> lights = await this.discovery.GetLightsAsync(refresh, cancellationToken);
        return lights
            .Select(static light => new PluginOutputDestinationOption(
                light.Ip,
                LifxDiscovery.DestinationLabel(light)))
            .ToArray();
    }
}

internal sealed class LifxColorSession : IPluginOutputSession
{
    private readonly LifxColorMode mode;
    private readonly IPEndPoint endpoint;
    private readonly byte[] target;
    private readonly LifxSessionIo io;
    private bool powered;

    public LifxColorSession(
        LifxColorMode mode,
        IPEndPoint endpoint,
        byte[] target,
        LifxDatagramSender? sender)
    {
        this.mode = mode;
        this.endpoint = endpoint;
        this.target = target;
        this.io = new LifxSessionIo(endpoint, sender);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> ch = channelValues.Span;
        if (ch.Length < this.mode.ChannelCount)
        {
            return false;
        }

        Hsbk color = this.mode.ToHsbk(ch);

        try
        {
            if (!this.powered)
            {
                await this.io.Send(
                    this.endpoint,
                    this.io.Packets.SetPower(this.target, true),
                    cancellationToken);
                this.powered = true;
            }

            await this.io.Send(
                this.endpoint,
                this.io.Packets.SetColor(this.target, color, LifxConstants.StreamDurationMs),
                cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => this.io.DisposeAsync();
}
