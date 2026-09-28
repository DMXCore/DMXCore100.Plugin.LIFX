using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger? log;

    public LifxColorProtocol(
        LifxColorMode mode,
        LifxDiscovery discovery,
        LifxDatagramSender? sender = null,
        ILogger? log = null)
    {
        this.mode = mode;
        this.discovery = discovery;
        this.sender = sender;
        this.log = log;
    }

    public int GetChannelCount(PluginOutputMappingConfig config) => this.mode.ChannelCount;

    public Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = LifxMapping.RequireEndpoint(config);
        LifxLight? light = this.discovery.LightFor(endpoint.Address.ToString());
        byte[] target = light?.Target ?? new byte[8];
        this.log?.LogDebug(
            "LIFX {Ip}: opened {Protocol} session, target {Target}, device {Device}, {Channels} channel(s) per update via SetColor",
            endpoint.Address,
            this.mode.ProtocolId,
            LifxMapping.DescribeTarget(target),
            light == null ? "not in the discovery cache" : $"'{light.Label}' {light.ModelName}",
            this.mode.ChannelCount);

        return Task.FromResult<IPluginOutputSession>(
            new LifxColorSession(this.mode, endpoint, target, this.sender, this.log, light?.ZoneCapable == true));
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
    private readonly byte[] target;
    private readonly LifxSessionIo io;
    private readonly bool multizone;

    public LifxColorSession(
        LifxColorMode mode,
        IPEndPoint endpoint,
        byte[] target,
        LifxDatagramSender? sender,
        ILogger? log = null,
        bool multizone = false)
    {
        this.mode = mode;
        this.target = target;
        this.multizone = multizone;
        this.io = new LifxSessionIo(endpoint, sender, log);
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
            await this.io.SendFrameAsync(
                this.target,
                [this.io.Packets.SetColor(this.target, color, LifxConstants.StreamDurationMs)],
                this.multizone,
                cancellationToken);
            this.io.Delivered(() => $"SetColor {LifxPackets.DescribeHsbk(color)}");
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
