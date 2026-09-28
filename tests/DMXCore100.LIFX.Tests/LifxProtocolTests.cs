using System.Buffers.Binary;
using System.Net;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxProtocolTests
{
    private static LifxPackets Packets() => new(1, () => 1);

    [TestMethod]
    public void ProductNames_MatchRegistry()
    {
        Assert.AreEqual("LIFX A19", LifxProducts.ModelName(1, 72));
        Assert.AreEqual("LIFX SuperColour Tube", LifxProducts.ModelName(1, 218));
        Assert.AreEqual("LIFX SuperColour Luna", LifxProducts.ModelName(1, 219));
    }

    [TestMethod]
    public void SwitchProducts_AreNotLights()
    {
        Assert.AreEqual("LIFX Switch", LifxProducts.ModelName(1, 70));
        Assert.IsTrue(LifxProducts.IsSwitch(70, "LIFX Switch"));
        Assert.IsTrue(LifxProducts.IsSwitch(226, "LIFX Dimmer Switch"));
        Assert.IsFalse(LifxProducts.IsSwitch(68, "LIFX Candle C"));
    }

    [TestMethod]
    public void UnknownProduct_KeepsId()
    {
        Assert.AreEqual("Unknown (product=99999)", LifxProducts.ModelName(1, 99999));
        Assert.AreEqual("Unknown (vendor=2)", LifxProducts.ModelName(2, 1));
    }

    [TestMethod]
    public void GetService_IsTaggedBroadcastHeader()
    {
        byte[] packet = Packets().GetService();
        Assert.AreEqual(LifxConstants.HeaderSize, packet.Length);
        Assert.AreEqual(LifxConstants.GetService, LifxPackets.ReadMessageType(packet));
        ushort frameBits = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        Assert.AreEqual(1, (frameBits >> 13) & 1);
    }

    [TestMethod]
    public void SetColor_WritesHsbkAndDuration()
    {
        var color = new Hsbk(100, 200, 300, 3500);
        byte[] packet = Packets().SetColor(new byte[8], color, LifxConstants.StreamDurationMs);
        Assert.AreEqual(LifxConstants.SetColor, LifxPackets.ReadMessageType(packet));
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual(100, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 1, 2)));
        Assert.AreEqual(200, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 3, 2)));
        Assert.AreEqual(300, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 5, 2)));
        Assert.AreEqual(3500, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(o + 7, 2)));
        Assert.AreEqual((uint)LifxConstants.StreamDurationMs, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(o + 9, 4)));
    }

    [TestMethod]
    public void StreamedMessages_AskForNoReply_ProbeMessagesDo()
    {
        byte[] target = [1, 2, 3, 4, 5, 6, 0, 0];
        LifxPackets packets = Packets();

        Assert.AreEqual(0, packets.SetColor(target, new Hsbk(0, 0, 0, 3500), 75)[22]);
        Assert.AreEqual(0, packets.SetPower(target, true)[22]);
        Assert.AreEqual(2, packets.SetPower(target, true, ackRequired: true)[22]);
        Assert.AreEqual(1, packets.GetLight(target)[22]);
        Assert.AreEqual(1, packets.GetMultiZoneEffect(target)[22]);
        Assert.AreEqual(LifxConstants.GetMultiZoneEffect, LifxPackets.ReadMessageType(packets.GetMultiZoneEffect(target)));
    }

    [TestMethod]
    public void DescribeReply_ReadsLightStateAndEffect()
    {
        byte[] state = new byte[LifxConstants.HeaderSize + 52];
        int o = LifxConstants.HeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 2, 2), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 4, 2), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 6, 2), 3500);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 10, 2), 65535);
        "Beam3"u8.CopyTo(state.AsSpan(o + 12));

        string light = LifxPackets.DescribeReply(LifxConstants.LightState, state);
        StringAssert.Contains(light, "'Beam3'");
        StringAssert.Contains(light, "power ON");
        StringAssert.Contains(light, "saturation 100%");
        StringAssert.Contains(light, "3500 K");

        byte[] effect = new byte[LifxConstants.HeaderSize + 59];
        effect[LifxConstants.HeaderSize + 4] = 1;
        StringAssert.Contains(LifxPackets.DescribeReply(LifxConstants.StateMultiZoneEffect, effect), "MOVE");

        StringAssert.Contains(LifxPackets.DescribeReply(LifxConstants.Acknowledgement, new byte[LifxConstants.HeaderSize]), "acknowledged");
    }

    [TestMethod]
    public void DescribeReply_CountsLitExtendedZones()
    {
        byte[] state = new byte[LifxConstants.HeaderSize + 5 + (82 * 8)];
        int o = LifxConstants.HeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o, 2), 31);
        state[o + 4] = 31;
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 5 + 4, 2), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(o + 5 + 8 + 4, 2), 1000);

        string zones = LifxPackets.DescribeReply(LifxConstants.StateExtendedColorZones, state);
        StringAssert.Contains(zones, "zones 0-30 of 31");
        StringAssert.Contains(zones, "2 non-black");
    }

    [TestMethod]
    public async Task IdleFrame_IsResentWithPowerOnAndFreshSequence()
    {
        var sent = new List<byte[]>();
        LifxDatagramSender sender = (_, packet, _) =>
        {
            lock (sent)
            {
                sent.Add(packet.ToArray());
            }

            return ValueTask.CompletedTask;
        };

        byte[] target = [1, 2, 3, 4, 5, 6, 0, 0];
        await using var io = new LifxSessionIo(
            new IPEndPoint(IPAddress.Loopback, LifxConstants.Port),
            sender,
            resendInterval: TimeSpan.FromMilliseconds(40));
        byte[] frame = io.Packets.SetColor(target, new Hsbk(100, 200, 300, 3500), 75);
        byte firstSequence = frame[23];
        await io.SendFrameAsync(target, [frame], multizone: false, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (sent)
            {
                if (sent.Count >= 4)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        byte[][] snapshot;
        lock (sent)
        {
            snapshot = [.. sent];
        }

        // Power-on + frame, then at least one idle resend of power-on + frame
        Assert.IsTrue(snapshot.Length >= 4, $"expected a resend, got {snapshot.Length} packet(s)");
        Assert.AreEqual(LifxConstants.SetPower, LifxPackets.ReadMessageType(snapshot[0]));
        Assert.AreEqual(LifxConstants.SetColor, LifxPackets.ReadMessageType(snapshot[1]));
        Assert.AreEqual(LifxConstants.SetPower, LifxPackets.ReadMessageType(snapshot[2]));
        Assert.AreEqual(LifxConstants.SetColor, LifxPackets.ReadMessageType(snapshot[3]));
        Assert.AreEqual(firstSequence, snapshot[1][23]);
        Assert.AreNotEqual(firstSequence, snapshot[3][23]);
        CollectionAssert.AreEqual(snapshot[1][24..], snapshot[3][24..]);
    }

    [TestMethod]
    public async Task InjectedSender_DoesNotResendByDefault()
    {
        int count = 0;
        LifxDatagramSender sender = (_, _, _) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        };

        byte[] target = [1, 2, 3, 4, 5, 6, 0, 0];
        await using var io = new LifxSessionIo(new IPEndPoint(IPAddress.Loopback, LifxConstants.Port), sender);
        await io.SendFrameAsync(target, [io.Packets.SetColor(target, new Hsbk(0, 0, 0, 3500), 75)], false, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(2, Volatile.Read(ref count));
    }

    [TestMethod]
    public void DescribeTarget_FlagsUntargetedSessions()
    {
        Assert.AreEqual("d073d5000001", LifxMapping.DescribeTarget([0xd0, 0x73, 0xd5, 0, 0, 1, 0, 0]));
        StringAssert.Contains(LifxMapping.DescribeTarget(new byte[8]), "untargeted");
    }

    [TestMethod]
    public void SuperColourAndStrips_HavePixelLayouts_A19DoesNot()
    {
        Assert.AreEqual(LifxLayout.Matrix, LifxProducts.Layout(218));
        Assert.AreEqual(LifxLayout.Matrix, LifxProducts.Layout(219));
        Assert.AreEqual(LifxLayout.Linear, LifxProducts.Layout(38));
        Assert.AreEqual(LifxLayout.Linear, LifxProducts.Layout(31));
        Assert.AreEqual(LifxLayout.Single, LifxProducts.Layout(72));

        var a19 = new LifxLight(new byte[8], "10.0.0.1") { Product = 72 };
        Assert.IsFalse(a19.ZoneCapable);
        Assert.IsNull(Packets().GeometryRequest(a19));

        var tube = new LifxLight(new byte[8], "10.0.0.2") { Product = 218 };
        Assert.IsTrue(tube.ZoneCapable);
        Assert.AreEqual(LifxConstants.GetDeviceChain, LifxPackets.ReadMessageType(Packets().GeometryRequest(tube)!));

        var beam = new LifxLight(new byte[8], "10.0.0.3") { Product = 38 };
        Assert.IsTrue(beam.ZoneCapable);
        Assert.AreEqual(
            LifxConstants.GetExtendedColorZones,
            LifxPackets.ReadMessageType(Packets().GeometryRequest(beam)!));
    }

    [TestMethod]
    public void ApplyMessage_StateVersion_SetsLayoutFromProductWithoutOverwritingGeometry()
    {
        byte[] version = VersionPacket(vendor: 1, product: 218);
        var light = new LifxLight(new byte[8], "10.0.0.2");
        LifxDiscovery.ApplyMessage(light, LifxConstants.StateVersion, version);
        Assert.AreEqual(218u, light.Product);
        Assert.AreEqual(LifxLayout.Matrix, light.Layout);
        Assert.AreEqual("LIFX SuperColour Tube", light.ModelName);

        light.Layout = LifxLayout.Linear;
        light.ZoneCount = 8;
        LifxDiscovery.ApplyMessage(light, LifxConstants.StateVersion, version);
        Assert.AreEqual(LifxLayout.Linear, light.Layout);
        Assert.AreEqual(8, light.ZoneCount);
    }

    [TestMethod]
    public void ParseStateDeviceChain_ReadsFirstTileGeometry()
    {
        byte[] packet = new byte[LifxConstants.HeaderSize + 1 + (16 * LifxConstants.TileDeviceSize) + 1];
        int payload = LifxConstants.HeaderSize;
        packet[payload + 1 + 16] = 4;
        packet[payload + 1 + 17] = 13;
        packet[payload + 1 + (16 * LifxConstants.TileDeviceSize)] = 1;

        var light = new LifxLight(new byte[8], "10.0.0.2");
        LifxPackets.ParseStateDeviceChain(light, packet);

        Assert.AreEqual(LifxLayout.Matrix, light.Layout);
        Assert.AreEqual(4, light.MatrixWidth);
        Assert.AreEqual(13, light.MatrixHeight);
        Assert.AreEqual(1, light.TileCount);
        Assert.AreEqual(52, light.ZoneCount);
    }

    [TestMethod]
    public void ParseLinearZoneCount_ReadsExtendedTotal()
    {
        byte[] extended = new byte[LifxConstants.HeaderSize + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(LifxConstants.HeaderSize, 2), 10);
        var strip = new LifxLight(new byte[8], "10.0.0.3");
        LifxPackets.ParseLinearZoneCount(strip, LifxConstants.StateExtendedColorZones, extended);
        Assert.AreEqual(LifxLayout.Linear, strip.Layout);
        Assert.AreEqual(10, strip.ZoneCount);
    }

    [TestMethod]
    public void ZonePackets_SuperColour_EmitsSet64()
    {
        var light = new LifxLight(new byte[8], "10.0.0.2")
        {
            Product = 218,
            Layout = LifxLayout.Matrix,
            MatrixWidth = 4,
            MatrixHeight = 13,
            TileCount = 1,
            ZoneCount = 52,
        };
        Hsbk[] colors = Enumerable.Repeat(new Hsbk(0, 65535, 65535, 3500), 52).ToArray();
        IReadOnlyList<byte[]> packets = Packets().ZonePackets(light, colors, LifxConstants.StreamDurationMs);

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(LifxConstants.Set64, LifxPackets.ReadMessageType(packets[0]));
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual(4, packets[0][o + 5]);
    }

    [TestMethod]
    public void ZonePackets_Linear_EmitsExtendedMultizone()
    {
        var light = new LifxLight(new byte[8], "10.0.0.3")
        {
            Product = 38,
            Layout = LifxLayout.Linear,
            ZoneCount = 8,
        };
        Hsbk[] colors = Enumerable.Repeat(new Hsbk(0, 65535, 65535, 3500), 8).ToArray();
        IReadOnlyList<byte[]> packets = Packets().ZonePackets(light, colors, LifxConstants.StreamDurationMs);

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(LifxConstants.SetExtendedColorZones, LifxPackets.ReadMessageType(packets[0]));
        int o = LifxConstants.HeaderSize;
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(packets[0].AsSpan(o + 5, 2)));
        Assert.AreEqual(8, packets[0][o + 7]);
    }

    [TestMethod]
    public void DirectedBroadcast_ORsHostBits()
    {
        IPAddress broadcast = LifxDiscovery.DirectedBroadcast(
            IPAddress.Parse("192.168.1.10"),
            IPAddress.Parse("255.255.255.0"));
        Assert.AreEqual("192.168.1.255", broadcast.ToString());
    }

    private static byte[] VersionPacket(uint vendor, uint product)
    {
        byte[] packet = new byte[LifxConstants.HeaderSize + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(LifxConstants.HeaderSize, 4), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(LifxConstants.HeaderSize + 4, 4), product);
        return packet;
    }
}
