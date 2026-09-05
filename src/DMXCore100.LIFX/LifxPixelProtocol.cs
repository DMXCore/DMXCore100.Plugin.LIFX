using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// Multipixel LIFX output for SuperColour Tube/Luna, Beam, strips, and tiles.
/// Channel count is pixels × the per-pixel width of the mapping's Color mode
/// (RGB, RGBW, +CT, 8- or 16-bit) from discovered zone geometry.
/// </summary>
internal sealed class LifxPixelProtocol : IPluginOutputProtocol
{
    public const string PixelsOptionKey = "pixels";
    public const string ColorModeOptionKey = "colorMode";

    /// <summary>
    /// The Boolean 16-bit toggle that preceded the Color mode field. Still
    /// honored (as RGB 16-bit) for mappings saved before it existed; the
    /// Color mode field wins whenever it is set.
    /// </summary>
    public const string SixteenBitOptionKey = "sixteenBit";

    private readonly LifxDiscovery discovery;
    private readonly LifxDatagramSender? sender;

    public LifxPixelProtocol(LifxDiscovery discovery, LifxDatagramSender? sender = null)
    {
        this.discovery = discovery;
        this.sender = sender;
    }

    /// <summary>
    /// The per-pixel channel layout of a mapping: its Color mode field, else
    /// the legacy 16-bit toggle (RGB 16-bit when on), else RGB.
    /// </summary>
    public static LifxColorMode ColorModeOf(PluginOutputMappingConfig config)
    {
        if (config.Options.TryGetValue(ColorModeOptionKey, out string? storedMode)
            && LifxColorMode.FromOptionValue(storedMode) is LifxColorMode mode)
        {
            return mode;
        }

        bool legacySixteenBit = config.Options.TryGetValue(SixteenBitOptionKey, out string? stored)
            && bool.TryParse(stored, out bool sixteenBit)
            && sixteenBit;

        return legacySixteenBit ? LifxColorMode.Rgb16 : LifxColorMode.Rgb;
    }

    public int GetChannelCount(PluginOutputMappingConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
        {
            return 0;
        }

        int perPixel = ColorModeOf(config).ChannelCount;

        // The stored mapping option is authoritative: it survives restarts
        // and is stamped by Discover, so the channel count never depends on
        // the RAM discovery cache
        if (config.Options.TryGetValue(PixelsOptionKey, out string? stored)
            && int.TryParse(stored, out int pixels)
            && pixels > 0
            && pixels <= int.MaxValue / perPixel)
        {
            return pixels * perPixel;
        }

        LifxLight? light = this.discovery.LightFor(config.DestinationAddress.Trim());
        if (light == null || !light.ZoneCapable)
        {
            return 0;
        }

        return LifxPixelMap.PixelCount(light) * perPixel;
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

        return new LifxPixelSession(endpoint, light, ColorModeOf(config), this.sender);
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
                LifxDiscovery.DestinationLabel(light))
            {
                // Stamped into the mapping's Pixels field on pick, so the
                // channel count persists with the configuration
                Options = new Dictionary<string, string>
                {
                    [PixelsOptionKey] = LifxPixelMap.PixelCount(light).ToString(),
                },
            })
            .ToArray();
    }
}

internal sealed class LifxPixelSession : IPluginOutputSession
{
    private readonly IPEndPoint endpoint;
    private readonly LifxLight light;
    private readonly LifxColorMode mode;
    private readonly LifxSessionIo io;
    private bool powered;

    public LifxPixelSession(IPEndPoint endpoint, LifxLight light, LifxColorMode mode, LifxDatagramSender? sender)
    {
        this.endpoint = endpoint;
        this.light = light;
        this.mode = mode;
        this.io = new LifxSessionIo(endpoint, sender);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        int pixels = LifxPixelMap.PixelCount(this.light);
        int perPixel = this.mode.ChannelCount;
        ReadOnlySpan<byte> ch = channelValues.Span;
        var pixelColors = new Hsbk[pixels];
        for (int i = 0; i < pixels; i++)
        {
            // Pixels past the end of the slice read as black
            int o = Math.Min(i * perPixel, ch.Length);
            pixelColors[i] = this.mode.ToHsbk(ch[o..]);
        }

        // Dead zones (SuperColour Tube 2-4) stay black
        Hsbk[] colors = LifxPixelMap.ToDeviceZones(this.light, pixelColors);

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
