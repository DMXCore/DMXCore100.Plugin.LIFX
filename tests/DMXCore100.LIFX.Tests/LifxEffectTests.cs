namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxEffectTests
{
    [TestMethod]
    public void SinewaveRgb_IsInUnitRange()
    {
        double[] phases = [0, -1, -100, Math.PI, 2 * Math.PI, 1000, 1e6];
        foreach (double phase in phases)
        {
            (double r, double g, double b) = LifxEffectRunner.SinewaveRgb(phase);
            Assert.IsTrue(r is >= 0 and <= 1, $"r out of range at phase {phase}");
            Assert.IsTrue(g is >= 0 and <= 1, $"g out of range at phase {phase}");
            Assert.IsTrue(b is >= 0 and <= 1, $"b out of range at phase {phase}");
        }
    }

    [TestMethod]
    public void RainbowZones_OneHuePerPixel()
    {
        IReadOnlyList<Rgb01> zones = LifxEffectRunner.RainbowZones(4);
        Assert.AreEqual(4, zones.Count);
        Assert.AreEqual(1.0, zones[0].R, 1e-9);
        Assert.AreEqual(0.0, zones[0].G, 1e-9);
    }

    [TestMethod]
    public void PixelChaseZones_LightsOneCell()
    {
        IReadOnlyList<Rgb01> zones = LifxEffectRunner.PixelChaseZones(5, 3, 1, 0, 0);
        Assert.AreEqual(5, zones.Count);
        Assert.AreEqual(1.0, zones[3].R, 1e-9);
        Assert.AreEqual(0.0, zones[0].R, 1e-9);
    }
}
