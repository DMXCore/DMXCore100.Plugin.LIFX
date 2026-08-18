using System.Buffers.Binary;
using System.Net;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxPluginTests
{
    private readonly List<LifxPlugin> plugins = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (LifxPlugin plugin in this.plugins)
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }

        this.plugins.Clear();
    }

    private async Task<(LifxPlugin Plugin, TestPluginHost Host, List<(IPEndPoint Endpoint, byte[] Packet)> Sent)> CreateInitializedAsync(
        IReadOnlyList<LifxLight>? discovered = null)
    {
        var sent = new List<(IPEndPoint, byte[])>();
        LifxLight[] lights = discovered?.ToArray() ??
        [
            new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
            {
                ModelName = "LIFX A19",
                Product = 72,
            },
        ];

        var plugin = new LifxPlugin(
            (_, _) => Task.FromResult<IReadOnlyList<LifxLight>>(lights),
            (endpoint, packet, _) =>
            {
                sent.Add((endpoint, packet.ToArray()));
                return ValueTask.CompletedTask;
            });
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, sent);
    }

    [TestMethod]
    public async Task Initialize_RegistersColorProtocolsAndProfile()
    {
        var (_, host, _) = await CreateInitializedAsync();

        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorCtProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorRgbwProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorRgbwCtProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.Color16ProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorCt16ProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorRgbw16ProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.ColorRgbwCt16ProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.PixelProtocolId));
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(LifxPlugin.ColorProfileCode));
        Assert.AreEqual(true, host.ConnectionState);
        Assert.AreEqual("LIFX output ready", host.ConnectionDetail);

        OutputProtocolDescriptor color = host.OutputProtocols[LifxPlugin.ColorProtocolId].Descriptor;
        Assert.AreEqual(LifxPlugin.PortType, color.PortType);
        Assert.AreEqual(LifxConstants.MaxUpdatesPerSecond, color.MaxUpdatesPerSecond);
        Assert.IsTrue(color.SupportsDestinationDiscovery);
        Assert.AreEqual(LifxPlugin.ColorProfileCode, color.SuggestedProfileCode);
        Assert.AreEqual("LIFX Color (single zone)", color.DisplayName);
        Assert.AreEqual("RGB", color.SuggestedPersonality);
        Assert.AreEqual("RGB+CT", host.OutputProtocols[LifxPlugin.ColorCtProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("LIFX Color + CT", host.OutputProtocols[LifxPlugin.ColorCtProtocolId].Descriptor.DisplayName);
        Assert.AreEqual("RGBW", host.OutputProtocols[LifxPlugin.ColorRgbwProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("LIFX Color RGBW", host.OutputProtocols[LifxPlugin.ColorRgbwProtocolId].Descriptor.DisplayName);
        Assert.AreEqual("RGBW+CT 16-bit", host.OutputProtocols[LifxPlugin.ColorRgbwCt16ProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("LIFX Color RGBW + CT 16-bit", host.OutputProtocols[LifxPlugin.ColorRgbwCt16ProtocolId].Descriptor.DisplayName);

        OutputProtocolDescriptor pixel = host.OutputProtocols[LifxPlugin.PixelProtocolId].Descriptor;
        Assert.AreEqual(LifxPlugin.PortType, pixel.PortType);
        Assert.IsTrue(pixel.SupportsDestinationDiscovery);
        Assert.IsTrue(string.IsNullOrEmpty(pixel.SuggestedProfileCode));
        CollectionAssert.AreEqual(
            new[] { LifxPixelProtocol.PixelsOptionKey, LifxPixelProtocol.SixteenBitOptionKey },
            pixel.MappingFields!.Select(field => field.Key).ToArray());
        Assert.AreEqual(PluginSettingType.Boolean, pixel.MappingFields![1].Type);

        PluginFixtureProfileDescriptor profile = host.FixtureProfiles[LifxPlugin.ColorProfileCode];
        Assert.AreEqual("LIFX", profile.Manufacturer);
        CollectionAssert.AreEqual(
            new[] { "RGB", "RGB+CT", "RGBW", "RGBW+CT", "RGB 16-bit", "RGB+CT 16-bit", "RGBW 16-bit", "RGBW+CT 16-bit" },
            profile.Personalities.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue },
            profile.Personalities[0].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PluginFixtureFunction.Red,
                PluginFixtureFunction.Green,
                PluginFixtureFunction.Blue,
                PluginFixtureFunction.ColorTemperature,
            },
            profile.Personalities[1].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PluginFixtureFunction.Red,
                PluginFixtureFunction.Green,
                PluginFixtureFunction.Blue,
                PluginFixtureFunction.White,
            },
            profile.Personalities[2].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PluginFixtureFunction.Red,
                PluginFixtureFunction.RedFine,
                PluginFixtureFunction.Green,
                PluginFixtureFunction.GreenFine,
                PluginFixtureFunction.Blue,
                PluginFixtureFunction.BlueFine,
                PluginFixtureFunction.White,
                PluginFixtureFunction.WhiteFine,
                PluginFixtureFunction.ColorTemperature,
                PluginFixtureFunction.ColorTemperatureFine,
            },
            profile.Personalities[7].Channels.ToArray());
    }

    [TestMethod]
    public async Task Initialize_EveryColorProtocolHasAMatchingPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginFixtureProfileDescriptor profile = host.FixtureProfiles[LifxPlugin.ColorProfileCode];
        PluginOutputMappingConfig config = Mapping("192.168.1.10");

        foreach (LifxColorMode mode in LifxColorMode.All)
        {
            OutputProtocolDescriptor descriptor = host.OutputProtocols[mode.ProtocolId].Descriptor;
            Assert.AreEqual(LifxPlugin.ColorProfileCode, descriptor.SuggestedProfileCode);
            PluginFixturePersonality? personality = profile.Personalities
                .SingleOrDefault(p => p.Name == descriptor.SuggestedPersonality);
            Assert.IsNotNull(personality, mode.ProtocolId);

            // The personality footprint and the protocol channel count must
            // agree or the patch and the mapping drift apart
            Assert.AreEqual(
                personality.Channels.Count,
                host.OutputProtocols[mode.ProtocolId].Protocol.GetChannelCount(config),
                mode.ProtocolId);
        }
    }

    [TestMethod]
    public async Task GetChannelCount_MatchesPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.10");

        Assert.AreEqual(3, Protocol(host, LifxPlugin.ColorProtocolId).GetChannelCount(config));
        Assert.AreEqual(4, Protocol(host, LifxPlugin.ColorCtProtocolId).GetChannelCount(config));
        Assert.AreEqual(4, Protocol(host, LifxPlugin.ColorRgbwProtocolId).GetChannelCount(config));
        Assert.AreEqual(5, Protocol(host, LifxPlugin.ColorRgbwCtProtocolId).GetChannelCount(config));
        Assert.AreEqual(6, Protocol(host, LifxPlugin.Color16ProtocolId).GetChannelCount(config));
        Assert.AreEqual(8, Protocol(host, LifxPlugin.ColorCt16ProtocolId).GetChannelCount(config));
        Assert.AreEqual(8, Protocol(host, LifxPlugin.ColorRgbw16ProtocolId).GetChannelCount(config));
        Assert.AreEqual(10, Protocol(host, LifxPlugin.ColorRgbwCt16ProtocolId).GetChannelCount(config));
    }

    [TestMethod]
    public async Task SendRgbw_WhiteAloneIsFullBrightnessDesaturatedAtDefaultKelvin()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorRgbwProtocolId,
            Mapping("192.168.1.10"),
            [0, 0, 0, 255]);

        Assert.IsTrue(ok);
        (_, ushort sat, ushort bri, ushort kelvin, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(0, sat);
        Assert.AreEqual(65535, bri);
        Assert.AreEqual(LifxConstants.DefaultKelvin, kelvin);
    }

    [TestMethod]
    public async Task SendRgbw_WhiteDesaturatesTheColor()
    {
        var (_, host, sent) = await CreateInitializedAsync();
        Hsbk expected = LifxColor.RgbToHsbk(1.0, 0.5, 0.5);

        // Half red plus half white = additive (1, 0.5, 0.5): a pastel red
        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorRgbwProtocolId,
            Mapping("192.168.1.10"),
            [128, 0, 0, 128]);

        Assert.IsTrue(ok);
        (ushort hue, ushort sat, ushort bri, _, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(expected.Hue, hue);
        Assert.AreEqual(expected.Saturation, sat, 200);
        Assert.AreEqual(expected.Brightness, bri, 200);
    }

    [TestMethod]
    public async Task SendRgbwCt_WhiteFollowsColorTemperature()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorRgbwCtProtocolId,
            Mapping("192.168.1.10"),
            [0, 0, 0, 255, 0]);

        Assert.IsTrue(ok);
        (_, ushort sat, ushort bri, ushort kelvin, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(0, sat);
        Assert.AreEqual(65535, bri);
        Assert.AreEqual(LifxConstants.KelvinMin, kelvin);
    }

    [TestMethod]
    public async Task SendRgb16_UsesFineChannelsForBrightness()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        // Red coarse 0x12, fine 0x34 → 0x1234 / 65535 → LIFX brightness 0x1234
        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.Color16ProtocolId,
            Mapping("192.168.1.10"),
            [0x12, 0x34, 0, 0, 0, 0]);

        Assert.IsTrue(ok);
        (ushort hue, ushort sat, ushort bri, _, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(0, hue);
        Assert.AreEqual(65535, sat);
        Assert.AreEqual(0x1234, bri);
    }

    [TestMethod]
    public async Task SendRgb16_ResolvesBelowOneEightBitStep()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        // Coarse 0 with a non-zero fine byte must still light the bulb: that
        // is the whole point of the 16-bit mode
        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.Color16ProtocolId,
            Mapping("192.168.1.10"),
            [0, 0x80, 0, 0, 0, 0]);

        Assert.IsTrue(ok);
        (_, _, ushort bri, _, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(0x80, bri);
    }

    [TestMethod]
    public async Task SendRgbwCt16_ReadsEveryFunctionAsCoarseFine()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        // Only white (0xFFFF) and CT (0xFFFF): a cool full white
        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorRgbwCt16ProtocolId,
            Mapping("192.168.1.10"),
            [0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.IsTrue(ok);
        (_, ushort sat, ushort bri, ushort kelvin, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(0, sat);
        Assert.AreEqual(65535, bri);
        Assert.AreEqual(LifxConstants.KelvinMax, kelvin);
    }

    [TestMethod]
    public async Task Send_RejectsShortSliceForEveryMode()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        foreach (LifxColorMode mode in LifxColorMode.All)
        {
            bool ok = await host.SimulateOutputDeliveryAsync(
                mode.ProtocolId,
                Mapping("192.168.1.10"),
                new byte[mode.ChannelCount - 1]);
            Assert.IsFalse(ok, mode.ProtocolId);
        }

        Assert.AreEqual(0, sent.Count);
    }

    [TestMethod]
    public async Task SendRgb_WritesSetColorHsbkAndDuration()
    {
        var (_, host, sent) = await CreateInitializedAsync();
        Hsbk expected = LifxColor.RgbToHsbk(1, 0, 0, LifxConstants.DefaultKelvin);

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorProtocolId,
            Mapping("192.168.1.10"),
            [255, 0, 0]);

        Assert.IsTrue(ok);
        byte[] color = AssertSetColor(sent);
        (ushort hue, ushort sat, ushort bri, ushort kelvin, uint duration) = ReadSetColor(color);
        Assert.AreEqual(expected.Hue, hue);
        Assert.AreEqual(expected.Saturation, sat);
        Assert.AreEqual(expected.Brightness, bri);
        Assert.AreEqual(expected.Kelvin, kelvin);
        Assert.AreEqual((uint)LifxConstants.StreamDurationMs, duration);
        Assert.AreEqual(LifxConstants.Port, sent[0].Endpoint.Port);
        Assert.AreEqual("192.168.1.10", sent[0].Endpoint.Address.ToString());
    }

    [TestMethod]
    public async Task SendRgbCt_MapsColorTemperatureToKelvin()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.ColorCtProtocolId,
            Mapping("192.168.1.10"),
            [0, 0, 0, 255]);

        Assert.IsTrue(ok);
        (_, _, _, ushort kelvin, _) = ReadSetColor(AssertSetColor(sent));
        Assert.AreEqual(LifxConstants.KelvinMax, kelvin);
    }

    [TestMethod]
    public async Task Session_PowersOnOnceThenStreamsColor()
    {
        var (_, host, sent) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.ColorProtocolId);
        await using IPluginOutputSession session = await protocol.OpenSessionAsync(
            Mapping("192.168.1.10"),
            CancellationToken.None);

        Assert.IsTrue(await session.SendAsync(new byte[] { 255, 0, 0 }, CancellationToken.None));
        Assert.IsTrue(await session.SendAsync(new byte[] { 0, 255, 0 }, CancellationToken.None));

        Assert.AreEqual(3, sent.Count);
        Assert.AreEqual(LifxConstants.SetPower, LifxPackets.ReadMessageType(sent[0].Packet));
        Assert.AreEqual(LifxConstants.SetColor, LifxPackets.ReadMessageType(sent[1].Packet));
        Assert.AreEqual(LifxConstants.SetColor, LifxPackets.ReadMessageType(sent[2].Packet));
    }

    [TestMethod]
    public async Task Discover_ReturnsIpDestinationsAndSkipsSwitches()
    {
        LifxLight[] lights =
        [
            new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
            {
                ModelName = "LIFX A19",
                Product = 72,
            },
            new LifxLight([8, 7, 6, 5, 4, 3, 2, 1], "192.168.1.20", "Hall")
            {
                ModelName = "LIFX Switch",
                Product = 70,
                IsLight = false,
            },
        ];
        var (_, host, _) = await CreateInitializedAsync(lights);

        IReadOnlyList<PluginOutputDestinationOption>? options =
            await Protocol(host, LifxPlugin.ColorProtocolId)
                .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("192.168.1.10", options[0].Value);
        Assert.AreEqual("Kitchen (192.168.1.10, LIFX A19)", options[0].Label);
    }

    [TestMethod]
    public async Task Discover_UsesCacheUntilRefresh()
    {
        int calls = 0;
        LifxLight kitchen = new([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
        {
            ModelName = "LIFX A19",
            Product = 72,
        };
        var plugin = new LifxPlugin(
            (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<LifxLight>>([kitchen]);
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.ColorProtocolId);

        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Discover_ConcurrentRefresh_SharesInFlightScan()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LifxLight kitchen = new([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
        {
            ModelName = "LIFX A19",
            Product = 72,
        };
        var plugin = new LifxPlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return (IReadOnlyList<LifxLight>)[kitchen];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.ColorProtocolId);

        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        release.SetResult();

        IReadOnlyList<PluginOutputDestinationOption>?[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, results[0]!.Count);
        Assert.AreEqual(1, results[1]!.Count);
        Assert.AreEqual("192.168.1.10", results[0]![0].Value);
    }

    [TestMethod]
    public async Task Discover_InitiatorCancel_DoesNotFailOtherRefresh()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken scanToken = CancellationToken.None;
        LifxLight kitchen = new([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
        {
            ModelName = "LIFX A19",
            Product = 72,
        };
        var plugin = new LifxPlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                scanToken = cancellationToken;
                started.TrySetResult();
                await release.Task;
                return (IReadOnlyList<LifxLight>)[kitchen];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.ColorProtocolId);

        using var firstCts = new CancellationTokenSource();
        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, firstCts.Token);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        firstCts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);
        Assert.IsFalse(scanToken.IsCancellationRequested);

        release.SetResult();
        IReadOnlyList<PluginOutputDestinationOption>? options = await second;

        Assert.AreEqual(1, calls);
        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("192.168.1.10", options[0].Value);
    }

    [TestMethod]
    public async Task PixelDiscover_ListsOnlyZoneCapableDevicesWithPixelCount()
    {
        LifxLight[] lights =
        [
            new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
            {
                ModelName = "LIFX A19",
                Product = 72,
            },
            SuperColourTube(),
        ];
        var (_, host, _) = await CreateInitializedAsync(lights);

        IReadOnlyList<PluginOutputDestinationOption>? color =
            await Protocol(host, LifxPlugin.ColorProtocolId)
                .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        IReadOnlyList<PluginOutputDestinationOption>? pixel =
            await Protocol(host, LifxPlugin.PixelProtocolId)
                .GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.IsNotNull(color);
        Assert.AreEqual(2, color.Count);
        Assert.IsNotNull(pixel);
        Assert.AreEqual(1, pixel.Count);
        Assert.AreEqual("192.168.1.30", pixel[0].Value);
        Assert.AreEqual("Bar (192.168.1.30, LIFX SuperColour Tube, 52 px)", pixel[0].Label);
    }

    [TestMethod]
    public async Task PixelGetChannelCount_IsThreeTimesDiscoveredZones()
    {
        var (_, host, _) = await CreateInitializedAsync([SuperColourTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.AreEqual(156, protocol.GetChannelCount(Mapping("192.168.1.30")));
        Assert.AreEqual(0, protocol.GetChannelCount(Mapping("192.168.1.10")));
    }

    [TestMethod]
    public async Task PixelGetChannelCount_HonorsStoredPixelsOptionWithoutDiscovery()
    {
        var (_, host, _) = await CreateInitializedAsync([SuperColourTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);

        // No discovery has run: the stored mapping option alone must supply
        // the channel count (the cold-start case)
        PluginOutputMappingConfig config = Mapping("192.168.1.30") with
        {
            Options = new Dictionary<string, string> { [LifxPixelProtocol.PixelsOptionKey] = "52" },
        };

        Assert.AreEqual(156, protocol.GetChannelCount(config));
        Assert.AreEqual(0, protocol.GetChannelCount(Mapping("192.168.1.30")));
    }

    [TestMethod]
    public async Task PixelGetChannelCount_SixteenBitDoublesTheFootprint()
    {
        var (_, host, _) = await CreateInitializedAsync([SuperColourTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        PluginOutputMappingConfig fromDiscovery = Mapping("192.168.1.30") with
        {
            Options = new Dictionary<string, string> { [LifxPixelProtocol.SixteenBitOptionKey] = "true" },
        };
        PluginOutputMappingConfig fromStoredPixels = Mapping("192.168.1.30") with
        {
            Options = new Dictionary<string, string>
            {
                [LifxPixelProtocol.PixelsOptionKey] = "52",
                [LifxPixelProtocol.SixteenBitOptionKey] = "true",
            },
        };
        PluginOutputMappingConfig eightBit = Mapping("192.168.1.30") with
        {
            Options = new Dictionary<string, string> { [LifxPixelProtocol.SixteenBitOptionKey] = "false" },
        };

        Assert.AreEqual(312, protocol.GetChannelCount(fromDiscovery));
        Assert.AreEqual(312, protocol.GetChannelCount(fromStoredPixels));
        Assert.AreEqual(156, protocol.GetChannelCount(eightBit));
    }

    [TestMethod]
    public async Task SendPixel16_DecodesCoarseFinePerComponent()
    {
        var (_, host, sent) = await CreateInitializedAsync([LinearBeam()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        PluginOutputMappingConfig config = Mapping("192.168.1.40") with
        {
            Options = new Dictionary<string, string> { [LifxPixelProtocol.SixteenBitOptionKey] = "true" },
        };

        // Zone 0: red 0x1234; zone 1: fine-only red 0x0080; others black
        byte[] channels = new byte[48];
        channels[0] = 0x12;
        channels[1] = 0x34;
        channels[7] = 0x80;

        bool ok = await host.SimulateOutputDeliveryAsync(LifxPlugin.PixelProtocolId, config, channels);

        Assert.IsTrue(ok);
        byte[] mz = sent.Select(item => item.Packet)
            .First(item => LifxPackets.ReadMessageType(item) == LifxConstants.SetExtendedColorZones);
        int colors = LifxConstants.HeaderSize + 8;
        Assert.AreEqual(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(mz.AsSpan(colors + 4, 2)), "zone 0 brightness");
        Assert.AreEqual(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(mz.AsSpan(colors + 8 + 4, 2)), "zone 1 brightness");
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(mz.AsSpan(colors + 16 + 4, 2)), "zone 2 brightness");
    }

    [TestMethod]
    public void PixelMap_TubeExposes52OfItsReported55Zones()
    {
        LifxLight tube = RealTube();

        Assert.AreEqual(52, LifxPixelMap.PixelCount(tube));
        int[]? map = LifxPixelMap.DeviceIndexes(tube);
        Assert.IsNotNull(map);
        Assert.AreEqual(52, map.Length);
        Assert.AreEqual(0, map[0]);
        Assert.AreEqual(1, map[1]);
        Assert.AreEqual(5, map[2]);
        Assert.AreEqual(54, map[51]);
        CollectionAssert.DoesNotContain(map, 2);
        CollectionAssert.DoesNotContain(map, 3);
        CollectionAssert.DoesNotContain(map, 4);
    }

    [TestMethod]
    public void PixelMap_IsIdentityForOtherDevices()
    {
        Assert.IsNull(LifxPixelMap.DeviceIndexes(LinearBeam()));
        Assert.AreEqual(8, LifxPixelMap.PixelCount(LinearBeam()));

        // A Tube whose firmware reports something other than 5×11 is left
        // alone rather than remapped on a guess
        LifxLight oddTube = RealTube();
        oddTube.ZoneCount = 52;
        Assert.IsNull(LifxPixelMap.DeviceIndexes(oddTube));
        Assert.AreEqual(52, LifxPixelMap.PixelCount(oddTube));
    }

    [TestMethod]
    public async Task Tube_DiscoverAndChannelCountUse52Pixels()
    {
        var (_, host, _) = await CreateInitializedAsync([RealTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);

        IReadOnlyList<PluginOutputDestinationOption>? options =
            await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(options);
        Assert.AreEqual("Tube (192.168.1.31, LIFX SuperColour Tube, 52 px)", options[0].Label);
        Assert.AreEqual("52", options[0].Options?[LifxPixelProtocol.PixelsOptionKey]);
        Assert.AreEqual(156, protocol.GetChannelCount(Mapping("192.168.1.31")));
    }

    [TestMethod]
    public async Task SendPixel_TubeSkipsDeadZones()
    {
        var (_, host, sent) = await CreateInitializedAsync([RealTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        // Distinct brightness per DMX pixel so the placement is verifiable:
        // pixel p → red at (p + 1) * 4 (never 0)
        byte[] channels = new byte[156];
        for (int p = 0; p < 52; p++)
        {
            channels[p * 3] = (byte)((p + 1) * 4);
        }

        bool ok = await host.SimulateOutputDeliveryAsync(LifxPlugin.PixelProtocolId, Mapping("192.168.1.31"), channels);

        Assert.IsTrue(ok);
        byte[] set64 = sent.Select(item => item.Packet)
            .First(item => LifxPackets.ReadMessageType(item) == LifxConstants.Set64);
        // First Set64 packet: x=0, y=0, width 5, rows 0..11 → zones 0..54
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual(5, set64[o + 5], "width");
        ushort Brightness(int zone) => BinaryPrimitives.ReadUInt16LittleEndian(set64.AsSpan(o + 10 + (zone * 8) + 4, 2));
        ushort Expected(int pixel) => LifxColor.RgbToHsbk((pixel + 1) * 4 / 255.0, 0, 0).Brightness;

        Assert.AreEqual(Expected(0), Brightness(0), "pixel 0 → zone 0");
        Assert.AreEqual(Expected(1), Brightness(1), "pixel 1 → zone 1");
        Assert.AreEqual(0, Brightness(2), "zone 2 dead");
        Assert.AreEqual(0, Brightness(3), "zone 3 dead");
        Assert.AreEqual(0, Brightness(4), "zone 4 dead");
        Assert.AreEqual(Expected(2), Brightness(5), "pixel 2 → zone 5");
        Assert.AreEqual(Expected(51), Brightness(54), "pixel 51 → zone 54");
    }

    [TestMethod]
    public async Task PixelGetChannelCount_IgnoresPixelsOptionThatWouldOverflow()
    {
        var (_, host, _) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);

        PluginOutputMappingConfig config = Mapping("192.168.1.30") with
        {
            Options = new Dictionary<string, string>
            {
                [LifxPixelProtocol.PixelsOptionKey] = (int.MaxValue / 3 + 1).ToString(),
            },
        };

        Assert.AreEqual(0, protocol.GetChannelCount(config));
    }

    [TestMethod]
    public async Task PixelDiscover_StampsPixelsOptionOnDestinations()
    {
        var (_, host, _) = await CreateInitializedAsync([SuperColourTube()]);

        IReadOnlyList<PluginOutputDestinationOption>? options =
            await Protocol(host, LifxPlugin.PixelProtocolId)
                .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(options);
        Assert.AreEqual("52", options[0].Options?[LifxPixelProtocol.PixelsOptionKey]);
    }

    [TestMethod]
    public async Task Discovery_PersistsToStateJsonAndSeedsNextInstance()
    {
        var (_, host, _) = await CreateInitializedAsync([SuperColourTube()]);
        _ = await Protocol(host, LifxPlugin.PixelProtocolId)
            .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.IsNotNull(host.StateJson);
        StringAssert.Contains(host.StateJson, "192.168.1.30");

        // A fresh plugin instance on a host with that state - and a scanner
        // that finds nothing - must still know the device from the seed
        var plugin = new LifxPlugin(
            (_, _) => Task.FromResult<IReadOnlyList<LifxLight>>([]),
            null);
        this.plugins.Add(plugin);
        var restartedHost = new TestPluginHost(plugin.Info, logOutput: _ => { })
        {
            StateJson = host.StateJson,
        };
        await plugin.InitializeAsync(restartedHost, CancellationToken.None);

        IPluginOutputProtocol protocol = Protocol(restartedHost, LifxPlugin.PixelProtocolId);
        Assert.AreEqual(156, protocol.GetChannelCount(Mapping("192.168.1.30")));

        IReadOnlyList<PluginOutputDestinationOption>? destinations =
            await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        Assert.IsNotNull(destinations);
        Assert.AreEqual(1, destinations.Count);
        Assert.AreEqual("192.168.1.30", destinations[0].Value);
    }

    [TestMethod]
    public async Task ScanCompleted_IsAwaitedBeforeGetLightsAsyncReturns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new LifxDiscovery((_, _) =>
            Task.FromResult<IReadOnlyList<LifxLight>>([SuperColourTube()]));
        discovery.ScanCompleted = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        Task<IReadOnlyList<LifxLight>> get = discovery.GetLightsAsync(refresh: true, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(get.IsCompleted);

        release.SetResult();
        IReadOnlyList<LifxLight> lights = await get.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, lights.Count);
        Assert.AreEqual("192.168.1.30", lights[0].Ip);
    }

    [TestMethod]
    public void Header_SetsTaggedBitForZeroTarget()
    {
        var packets = new LifxPackets(12345, static () => 1);

        byte[] zeroTarget = packets.SetColor(new byte[8], new Hsbk(0, 0, 0, 3500), 75);
        byte[] realTarget = packets.SetColor([1, 2, 3, 4, 5, 6, 7, 8], new Hsbk(0, 0, 0, 3500), 75);

        ushort zeroFrame = BinaryPrimitives.ReadUInt16LittleEndian(zeroTarget.AsSpan(2, 2));
        ushort realFrame = BinaryPrimitives.ReadUInt16LittleEndian(realTarget.AsSpan(2, 2));
        Assert.AreEqual(1, (zeroFrame >> 13) & 1, "zero target must be tagged");
        Assert.AreEqual(0, (realFrame >> 13) & 1, "real target must stay untagged");
    }

    [TestMethod]
    public async Task SendPixel_WritesSet64ForSuperColourTube()
    {
        var (_, host, sent) = await CreateInitializedAsync([SuperColourTube()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        byte[] channels = new byte[156];
        for (int i = 0; i < 52; i++)
        {
            channels[i * 3] = 255;
        }

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.PixelProtocolId,
            Mapping("192.168.1.30"),
            channels);

        Assert.IsTrue(ok);
        Assert.AreEqual(LifxConstants.SetPower, LifxPackets.ReadMessageType(sent[0].Packet));
        byte[] set64 = sent.Select(item => item.Packet)
            .First(item => LifxPackets.ReadMessageType(item) == LifxConstants.Set64);
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual(0, set64[o]);
        Assert.AreEqual(0, set64[o + 3]);
        Assert.AreEqual(0, set64[o + 4]);
        Assert.AreEqual(4, set64[o + 5]);
        Assert.AreEqual((uint)LifxConstants.StreamDurationMs, BinaryPrimitives.ReadUInt32LittleEndian(set64.AsSpan(o + 6, 4)));
        Assert.AreEqual("192.168.1.30", sent[0].Endpoint.Address.ToString());
    }

    [TestMethod]
    public async Task SendPixel_WritesExtendedMzForLinearBeam()
    {
        var (_, host, sent) = await CreateInitializedAsync([LinearBeam()]);
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        byte[] channels = new byte[24];
        for (int i = 0; i < 8; i++)
        {
            channels[i * 3] = 255;
        }

        bool ok = await host.SimulateOutputDeliveryAsync(
            LifxPlugin.PixelProtocolId,
            Mapping("192.168.1.40"),
            channels);

        Assert.IsTrue(ok);
        Assert.AreEqual(LifxConstants.SetPower, LifxPackets.ReadMessageType(sent[0].Packet));
        byte[] mz = sent.Select(item => item.Packet)
            .First(item => LifxPackets.ReadMessageType(item) == LifxConstants.SetExtendedColorZones);
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual((uint)LifxConstants.StreamDurationMs, BinaryPrimitives.ReadUInt32LittleEndian(mz.AsSpan(o, 4)));
        Assert.AreEqual(LifxConstants.MultiZoneApply, mz[o + 4]);
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(mz.AsSpan(o + 5, 2)));
        Assert.AreEqual(8, mz[o + 7]);
        Assert.AreEqual("192.168.1.40", sent[0].Endpoint.Address.ToString());
    }

    [TestMethod]
    public async Task PixelOpenSession_RequiresZoneCapableDevice()
    {
        var (_, host, _) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.PixelProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping("192.168.1.10"), CancellationToken.None));
    }

    [TestMethod]
    public async Task OpenSession_RequiresValidIp()
    {
        var (_, host, _) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, LifxPlugin.ColorProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping(""), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping("not-an-ip"), CancellationToken.None));
    }

    [TestMethod]
    public void DestinationLabel_FallsBackWhenNameMissing()
    {
        var light = new LifxLight(new byte[8], "10.0.0.5");
        Assert.AreEqual("LIFX (10.0.0.5, LIFX)", LifxDiscovery.DestinationLabel(light));
    }

    [TestMethod]
    public void DestinationLabel_AppendsPixelCountForMultipixelDevices()
    {
        Assert.AreEqual(
            "Bar (192.168.1.30, LIFX SuperColour Tube, 52 px)",
            LifxDiscovery.DestinationLabel(SuperColourTube()));
    }

    [TestMethod]
    public void KelvinFromDmx_SpansLifxRange()
    {
        Assert.AreEqual(LifxConstants.KelvinMin, LifxColor.KelvinFromDmx(0));
        Assert.AreEqual(LifxConstants.KelvinMax, LifxColor.KelvinFromDmx(255));
    }

    private static LifxLight SuperColourTube() =>
        new([9, 8, 7, 6, 5, 4, 3, 2], "192.168.1.30", "Bar")
        {
            ModelName = "LIFX SuperColour Tube",
            Product = 218,
            Layout = LifxLayout.Matrix,
            MatrixWidth = 4,
            MatrixHeight = 13,
            TileCount = 1,
            ZoneCount = 52,
        };

    /// <summary>
    /// What a real SuperColour Tube reports: a 5×11 tile (55 zones) of
    /// which 52 light (indexes 2-4 are dead).
    /// </summary>
    private static LifxLight RealTube() =>
        new([9, 8, 7, 6, 5, 4, 3, 2], "192.168.1.31", "Tube")
        {
            ModelName = "LIFX SuperColour Tube",
            Product = 217,
            Layout = LifxLayout.Matrix,
            MatrixWidth = 5,
            MatrixHeight = 11,
            TileCount = 1,
            ZoneCount = 55,
        };

    private static LifxLight LinearBeam() =>
        new([2, 3, 4, 5, 6, 7, 8, 9], "192.168.1.40", "Beam")
        {
            ModelName = "LIFX Beam",
            Product = 38,
            Layout = LifxLayout.Linear,
            ZoneCount = 8,
        };

    private static IPluginOutputProtocol Protocol(TestPluginHost host, string id) =>
        host.OutputProtocols[id].Protocol;

    private static PluginOutputMappingConfig Mapping(string ip) =>
        new()
        {
            DestinationAddress = ip,
            ChannelOffset = 0,
            UniverseId = 1,
        };

    private static byte[] AssertSetColor(List<(IPEndPoint Endpoint, byte[] Packet)> sent)
    {
        byte[]? packet = sent.Select(item => item.Packet)
            .FirstOrDefault(item => LifxPackets.ReadMessageType(item) == LifxConstants.SetColor);
        Assert.IsNotNull(packet);
        return packet;
    }

    private static (ushort Hue, ushort Saturation, ushort Brightness, ushort Kelvin, uint Duration) ReadSetColor(
        byte[] packet)
    {
        int o = LifxConstants.HeaderSize;
        return (
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 1, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 3, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 5, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 7, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(o + 9, 4)));
    }
}
