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
    public const string PixelProtocolId = "LIFX_PIXEL";
    public const string ColorProfileCode = "LIFX_COLOR";
    public const string PortType = "LIFX";

    private readonly List<IDisposable> registrations = [];
    private readonly LifxDiscoverFunc? discoverOverride;
    private readonly LifxDatagramSender? sendOverride;

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
            Id = "lifx",
            Name = "LIFX",
            Version = "0.2.0",
            Description = "Drives LIFX WiFi bulbs and SuperColour / pixel fixtures from DMX over the LAN protocol.",
        };
    }

    public PluginInfo Info { get; }

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var discovery = new LifxDiscovery(this.discoverOverride);

        this.registrations.Add(host.Outputs.RegisterFixtureProfile(new PluginFixtureProfileDescriptor
        {
            Code = ColorProfileCode,
            Name = "Color Bulb",
            Manufacturer = "LIFX",
            Personalities =
            [
                new PluginFixturePersonality
                {
                    Name = "RGB",
                    Channels =
                    [
                        PluginFixtureFunction.Red,
                        PluginFixtureFunction.Green,
                        PluginFixtureFunction.Blue,
                    ],
                },
                new PluginFixturePersonality
                {
                    Name = "RGB+CT",
                    Channels =
                    [
                        PluginFixtureFunction.Red,
                        PluginFixtureFunction.Green,
                        PluginFixtureFunction.Blue,
                        PluginFixtureFunction.ColorTemperature,
                    ],
                },
            ],
        }));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor(ColorProtocolId, "LIFX Color (single zone)", "RGB"),
            new LifxColorProtocol(LifxColorMode.Rgb, discovery, this.sendOverride)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            Descriptor(ColorCtProtocolId, "LIFX Color + CT", "RGB+CT"),
            new LifxColorProtocol(LifxColorMode.RgbCt, discovery, this.sendOverride)));

        this.registrations.Add(host.Outputs.RegisterOutputProtocol(
            PixelDescriptor(),
            new LifxPixelProtocol(discovery, this.sendOverride)));

        host.SetConnectionState(true, "LIFX output ready");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable registration in this.registrations)
        {
            registration.Dispose();
        }

        this.registrations.Clear();
        return Task.CompletedTask;
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
        };

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
