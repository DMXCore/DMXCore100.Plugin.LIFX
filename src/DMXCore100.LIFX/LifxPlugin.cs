using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// LIFX output plugin: registers color and pixel protocols plus a color
/// fixture profile so devices can be mapped on the Outputs page and patched
/// like any other fixture. UDP SetColor / Set64 / SetPower go to port 56700;
/// the host rate-limits, dedupes, and coalesces latest-wins.
/// </summary>
public class LifxPlugin : IPlugin
{
    public const string ColorProtocolId = "LIFX_COLOR";
    public const string ColorCtProtocolId = "LIFX_COLOR_CT";
    public const string ColorRgbwProtocolId = "LIFX_COLOR_RGBW";
    public const string ColorRgbwCtProtocolId = "LIFX_COLOR_RGBW_CT";
    public const string Color16ProtocolId = "LIFX_COLOR_16";
    public const string ColorCt16ProtocolId = "LIFX_COLOR_CT_16";
    public const string ColorRgbw16ProtocolId = "LIFX_COLOR_RGBW_16";
    public const string ColorRgbwCt16ProtocolId = "LIFX_COLOR_RGBW_CT_16";
    public const string PixelProtocolId = "LIFX_PIXEL";
    public const string ColorProfileCode = "LIFX_COLOR";
    public const string PortType = "LIFX";

    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(5);

    private readonly List<IDisposable> registrations = [];
    private readonly LifxDiscoverFunc? discoverOverride;
    private readonly LifxDatagramSender? sendOverride;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object persistGate = new();
    private Task persistInFlight = Task.CompletedTask;

    public LifxPlugin()
        : this(null, null)
    {
    }

    internal LifxPlugin(LifxDiscoverFunc? discoverOverride, LifxDatagramSender? sendOverride)
    {
        this.discoverOverride = discoverOverride;
        this.sendOverride = sendOverride;
        Info = new()
        {
            // Id/Name/Version come from the csproj (PluginId,
            // PluginDisplayName, Version) via the SDK-generated
            // PluginBuildInfo, always in sync with the generated manifest.json
            Id = PluginBuildInfo.Id,
            Name = PluginBuildInfo.Name,
            Version = PluginBuildInfo.Version,
            Description = "Drives LIFX WiFi bulbs and SuperColour / pixel fixtures from DMX over the LAN protocol.",
        };
    }

    public PluginInfo Info { get; }

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var discovery = new LifxDiscovery(this.discoverOverride);

        // Seed the discovery cache from persisted state so targets and pixel
        // geometry survive restarts; every completed scan refreshes the blob
        discovery.Seed(LifxLightState.Deserialize(await host.GetStateJsonAsync(cancellationToken)));
        var persistWrites = new SemaphoreSlim(1, 1);
        discovery.ScanCompleted = lights =>
        {
            string json = LifxLightState.Serialize(lights);
            lock (this.persistGate)
            {
                if (this.lifetime.IsCancellationRequested)
                {
                    return Task.FromCanceled(this.lifetime.Token);
                }

                Task persist = this.PersistStateAsync(host, persistWrites, json);
                this.persistInFlight = persist;
                return persist;
            }
        };

        // One profile personality per color protocol, same name, so the
        // fixture editor can prefill the personality from a mapping
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(new PluginFixtureProfileDescriptor
        {
            Code = ColorProfileCode,
            Name = "Color Bulb",
            Manufacturer = "LIFX",
            Personalities = LifxColorMode.All
                .Select(static mode => new PluginFixturePersonality
                {
                    Name = mode.Personality,
                    Channels = mode.ProfileChannels(),
                })
                .ToArray(),
        }));

        foreach (LifxColorMode mode in LifxColorMode.All)
        {
            this.registrations.Add(host.Outputs.RegisterOutputProtocol(
                Descriptor(mode.ProtocolId, ColorDisplayName(mode), mode.Personality),
                new LifxColorProtocol(mode, discovery, this.sendOverride)));
        }

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            PixelDescriptor(),
            new LifxPixelProtocol(discovery, this.sendOverride)));

        host.SetConnectionState(true, "LIFX output ready");
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Task inFlight;
        lock (this.persistGate)
        {
            this.lifetime.Cancel();
            inFlight = this.persistInFlight;
        }

        try
        {
            await inFlight;
        }
        catch (Exception)
        {
        }

        foreach (IDisposable registration in this.registrations)
        {
            registration.Dispose();
        }

        this.registrations.Clear();
    }

    private async Task PersistStateAsync(IPluginHost host, SemaphoreSlim persistWrites, string json)
    {
        using var persistCts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        persistCts.CancelAfter(PersistTimeout);
        await persistWrites.WaitAsync(persistCts.Token);
        try
        {
            await host.SetStateJsonAsync(json, persistCts.Token);
        }
        finally
        {
            persistWrites.Release();
        }
    }

    private static OutputProtocolDescriptor PixelDescriptor() =>
        new()
        {
            Id = PixelProtocolId,
            DisplayName = "LIFX Pixel (Tube / Beam / strip)",
            PortType = PortType,
            PortTypeDisplayName = "LIFX",
            MaxUpdatesPerSecond = LifxConstants.MaxUpdatesPerSecond,
            SupportsDestinationDiscovery = true,
            MappingFields =
            [
                new PluginSettingDescriptor
                {
                    Key = LifxPixelProtocol.PixelsOptionKey,
                    Label = "Pixels",
                    Type = PluginSettingType.Integer,
                    Description = "Pixel count of the device; filled automatically when a discovered device is picked",
                },
                new PluginSettingDescriptor
                {
                    Key = LifxPixelProtocol.SixteenBitOptionKey,
                    Label = "16-bit",
                    Type = PluginSettingType.Boolean,
                    Description = "Six channels per pixel (red, red fine, green, green fine, blue, blue fine) instead of three",
                },
            ],
        };

    /// <summary>
    /// Dropdown name of a color protocol. The two original modes keep the
    /// names they shipped with; the rest spell out white / CT / bit depth.
    /// </summary>
    private static string ColorDisplayName(LifxColorMode mode)
    {
        if (mode == LifxColorMode.Rgb)
        {
            return "LIFX Color (single zone)";
        }

        string name = mode.HasWhite ? "LIFX Color RGBW" : "LIFX Color";
        if (mode.HasColorTemperature)
        {
            name += " + CT";
        }

        if (mode.SixteenBit)
        {
            name += " 16-bit";
        }

        return name;
    }

    private static OutputProtocolDescriptor Descriptor(string id, string displayName, string personality) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            PortType = PortType,
            PortTypeDisplayName = "LIFX",
            MaxUpdatesPerSecond = LifxConstants.MaxUpdatesPerSecond,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = ColorProfileCode,
            SuggestedPersonality = personality,
        };
}
