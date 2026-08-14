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
        Assert.IsTrue(host.OutputProtocols.ContainsKey(LifxPlugin.PixelProtocolId));
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(LifxPlugin.ColorProfileCode));
        Assert.AreEqual(true, host.ConnectionState);
        Assert.AreEqual("LIFX output ready", host.ConnectionDetail);

        OutputProtocolDescriptor color = host.OutputProtocols[LifxPlugin.ColorProtocolId].Descriptor;
        Assert.AreEqual(LifxPlugin.PortType, color.PortType);
        Assert.AreEqual(LifxConstants.MaxUpdatesPerSecond, color.MaxUpdatesPerSecond);
        Assert.IsTrue(color.SupportsDestinationDiscovery);
        Assert.AreEqual(LifxPlugin.ColorProfileCode, color.SuggestedProfileCode);
        Assert.AreEqual("RGB", color.SuggestedPersonality);
        Assert.AreEqual("RGB+CT", host.OutputProtocols[LifxPlugin.ColorCtProtocolId].Descriptor.SuggestedPersonality);

        OutputProtocolDescriptor pixel = host.OutputProtocols[LifxPlugin.PixelProtocolId].Descriptor;
        Assert.AreEqual(LifxPlugin.PortType, pixel.PortType);
        Assert.IsTrue(pixel.SupportsDestinationDiscovery);
        Assert.IsTrue(string.IsNullOrEmpty(pixel.SuggestedProfileCode));

        PluginFixtureProfileDescriptor profile = host.FixtureProfiles[LifxPlugin.ColorProfileCode];
        Assert.AreEqual("LIFX", profile.Manufacturer);
        Assert.AreEqual("RGB", profile.Personalities[0].Name);
        Assert.AreEqual("RGB+CT", profile.Personalities[1].Name);
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
    }

    [TestMethod]
    public async Task GetChannelCount_MatchesPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.10");

        Assert.AreEqual(3, Protocol(host, LifxPlugin.ColorProtocolId).GetChannelCount(config));
        Assert.AreEqual(4, Protocol(host, LifxPlugin.ColorCtProtocolId).GetChannelCount(config));
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
