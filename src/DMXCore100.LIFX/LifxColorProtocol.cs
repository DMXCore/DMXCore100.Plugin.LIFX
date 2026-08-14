using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

internal enum LifxColorMode
{
    Rgb = 3,
    RgbCt = 4,
}

/// <summary>
/// Single-zone LIFX color output: RGB or RGB+CT channel slices become
/// SetColor UDP datagrams to port 56700.
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

    public int GetChannelCount(PluginOutputMappingConfig config) => (int)this.mode;

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
        if (ch.Length < (int)this.mode)
        {
            return false;
        }

        byte red = ch[0];
        byte green = ch[1];
        byte blue = ch[2];
        int kelvin = this.mode switch
        {
            LifxColorMode.Rgb => LifxConstants.DefaultKelvin,
            LifxColorMode.RgbCt => LifxColor.KelvinFromDmx(ch[3]),
            _ => throw Unexpected(this.mode),
        };

        Hsbk color = LifxColor.RgbToHsbk(red / 255.0, green / 255.0, blue / 255.0, kelvin);

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

    private static InvalidOperationException Unexpected(LifxColorMode mode) =>
        new($"Unhandled color mode {mode}");
}
