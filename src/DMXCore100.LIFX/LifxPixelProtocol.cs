using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// Multipixel LIFX output for SuperColour Tube/Luna, Beam, strips, and tiles.
/// Channel count is pixels × 3 from discovered zone geometry.
/// </summary>
internal sealed class LifxPixelProtocol : IPluginOutputProtocol
{
    private readonly LifxDiscovery discovery;
    private readonly LifxDatagramSender? sender;

    public LifxPixelProtocol(LifxDiscovery discovery, LifxDatagramSender? sender = null)
    {
        this.discovery = discovery;
        this.sender = sender;
    }

    public int GetChannelCount(PluginOutputMappingConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
        {
            return 0;
        }

        LifxLight? light = this.discovery.LightFor(config.DestinationAddress.Trim());
        if (light == null || !light.ZoneCapable)
        {
            return 0;
        }

        return Math.Max(1, light.ZoneCount) * 3;
    }

    public async Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = LifxMapping.RequireEndpoint(config);
        string ip = endpoint.Address.ToString();
        LifxLight? light = this.discovery.LightFor(ip);
        if (light == null || !light.ZoneCapable)
        {
            await this.discovery.GetLightsAsync(refresh: true, cancellationToken);
            light = this.discovery.LightFor(ip);
        }

        if (light == null || !light.ZoneCapable)
        {
            throw new InvalidOperationException(
                $"No pixel LIFX device is cached at '{ip}'. Run Discover on the LIFX Pixel protocol first.");
        }

        return new LifxPixelSession(endpoint, light, this.sender);
    }

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LifxLight> lights = await this.discovery.GetLightsAsync(refresh, cancellationToken);
        return lights
            .Where(static light => light.ZoneCapable)
            .Select(static light => new PluginOutputDestinationOption(
                light.Ip,
                LifxDiscovery.DestinationLabel(light)))
            .ToArray();
    }
}

internal sealed class LifxPixelSession : IPluginOutputSession
{
    private readonly IPEndPoint endpoint;
    private readonly LifxLight light;
    private readonly LifxSessionIo io;
    private bool powered;

    public LifxPixelSession(IPEndPoint endpoint, LifxLight light, LifxDatagramSender? sender)
    {
        this.endpoint = endpoint;
        this.light = light;
        this.io = new LifxSessionIo(endpoint, sender);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        int zones = Math.Max(1, this.light.ZoneCount);
        ReadOnlySpan<byte> ch = channelValues.Span;
        var colors = new Hsbk[zones];
        for (int i = 0; i < zones; i++)
        {
            int o = i * 3;
            byte red = o < ch.Length ? ch[o] : (byte)0;
            byte green = o + 1 < ch.Length ? ch[o + 1] : (byte)0;
            byte blue = o + 2 < ch.Length ? ch[o + 2] : (byte)0;
            colors[i] = LifxColor.RgbToHsbk(red / 255.0, green / 255.0, blue / 255.0);
        }

        try
        {
            if (!this.powered)
            {
                await this.io.Send(
                    this.endpoint,
                    this.io.Packets.SetPower(this.light.Target, true),
                    cancellationToken);
                this.powered = true;
            }

            foreach (byte[] packet in this.io.Packets.ZonePackets(this.light, colors, LifxConstants.StreamDurationMs))
            {
                await this.io.Send(this.endpoint, packet, cancellationToken);
            }

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
