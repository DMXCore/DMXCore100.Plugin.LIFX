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
