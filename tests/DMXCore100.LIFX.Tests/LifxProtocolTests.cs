using System.Buffers.Binary;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxProtocolTests
{
    private static LifxPackets Packets() => new(1, () => 1);

    [TestMethod]
    public void SuperColourProducts_AreMatrix()
    {
        Assert.AreEqual("LIFX SuperColour Tube", LifxProducts.ModelName(1, 218));
        Assert.AreEqual("LIFX SuperColour Luna", LifxProducts.ModelName(1, 219));
        Assert.AreEqual(LifxLayout.Matrix, LifxProducts.Layout(218));
        Assert.IsFalse(LifxProducts.IsSwitch(218, "LIFX SuperColour Tube"));
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
    public void Light_ZoneCapableFromProductWithoutLayout()
    {
        var light = new LifxLight(new byte[8], "192.168.1.122") { Product = 218 };
        Assert.AreEqual(LifxLayout.Single, light.Layout);
        Assert.AreEqual(LifxLayout.Matrix, light.EffectiveLayout);
        Assert.IsTrue(light.ZoneCapable);
    }

    [TestMethod]
    public void Set64_PacketSizeForTube()
    {
        Hsbk[] colors = Enumerable.Repeat(new Hsbk(100, 200, 300, 3500), 52).ToArray();
        IReadOnlyList<byte[]> packets = Packets().BuildSet64Packets(new byte[8], colors, 4, 13, 20);
        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(LifxConstants.HeaderSize + 522, packets[0].Length);
        Assert.AreEqual(LifxConstants.Set64, LifxPackets.ReadMessageType(packets[0]));
    }

    [TestMethod]
    public void ExtendedMultizone_PacketSize()
    {
        Hsbk[] colors = Enumerable.Repeat(new Hsbk(1, 2, 3, 3500), 10).ToArray();
        IReadOnlyList<byte[]> packets = Packets().BuildExtendedMzPackets(new byte[8], colors, 20);
        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(LifxConstants.HeaderSize + 664, packets[0].Length);
        Assert.AreEqual(LifxConstants.MultiZoneApply, packets[0][LifxConstants.HeaderSize + 4]);
    }

    [TestMethod]
    public void ParseDeviceChain_SetsZoneCount()
    {
        var light = new LifxLight(new byte[8], "192.168.1.10");
        byte[] tile = new byte[LifxConstants.TileDeviceSize];
        tile[16] = 4;
        tile[17] = 13;
        byte[] payload = new byte[1 + (16 * LifxConstants.TileDeviceSize) + 1];
        tile.CopyTo(payload, 1);
        payload[1 + (16 * LifxConstants.TileDeviceSize)] = 1;
        byte[] packet = new byte[LifxConstants.HeaderSize + payload.Length];
        payload.CopyTo(packet, LifxConstants.HeaderSize);

        LifxPackets.ParseStateDeviceChain(light, packet);

        Assert.AreEqual(LifxLayout.Matrix, light.Layout);
        Assert.AreEqual(4, light.MatrixWidth);
        Assert.AreEqual(13, light.MatrixHeight);
        Assert.AreEqual(52, light.ZoneCount);
    }

    [TestMethod]
    public void ParseLinearZoneCount_ExtendedUsesUInt16()
    {
        var light = new LifxLight(new byte[8], "192.168.1.11");
        byte[] packet = new byte[LifxConstants.HeaderSize + 5];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(LifxConstants.HeaderSize, 2), 300);

        LifxPackets.ParseLinearZoneCount(light, LifxConstants.StateExtendedColorZones, packet);

        Assert.AreEqual(LifxLayout.Linear, light.Layout);
        Assert.AreEqual(300, light.ZoneCount);
    }

    [TestMethod]
    public void LegacyLinear_EmitsSetColorZonesPerRun()
    {
        var light = new LifxLight(new byte[8], "192.168.1.16") { Layout = LifxLayout.Linear, Product = 31 };
        Hsbk[] colors = [new(1, 2, 3, 3500), new(1, 2, 3, 3500), new(4, 5, 6, 3500)];
        IReadOnlyList<byte[]> packets = Packets().ZonePackets(light, colors, 20);

        Assert.AreEqual(2, packets.Count);
        Assert.AreEqual(LifxConstants.SetColorZones, LifxPackets.ReadMessageType(packets[0]));
        Assert.AreEqual((0, 1), (packets[0][LifxConstants.HeaderSize], packets[0][LifxConstants.HeaderSize + 1]));
        Assert.AreEqual((2, 2), (packets[1][LifxConstants.HeaderSize], packets[1][LifxConstants.HeaderSize + 1]));
        Assert.AreEqual(LifxConstants.MultiZoneNoApply, packets[0][LifxConstants.HeaderSize + 14]);
        Assert.AreEqual(LifxConstants.MultiZoneApply, packets[1][LifxConstants.HeaderSize + 14]);
    }

    [TestMethod]
    public void ExtendedLinear_UsesMessage510()
    {
        var light = new LifxLight(new byte[8], "192.168.1.17") { Layout = LifxLayout.Linear, Product = 56 };
        IReadOnlyList<byte[]> packets = Packets().ZonePackets(light, [new Hsbk(1, 2, 3, 3500)], 20);
        Assert.AreEqual(LifxConstants.SetExtendedColorZones, LifxPackets.ReadMessageType(packets[0]));
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
}
