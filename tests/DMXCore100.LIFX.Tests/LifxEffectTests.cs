namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxEffectTests
{
    [TestMethod]
    public void SinewaveRgb_IsInUnitRange()
    {
        (double r, double g, double b) = LifxEffectRunner.SinewaveRgb(0);
        Assert.IsTrue(r is >= 0 and <= 1);
        Assert.IsTrue(g is >= 0 and <= 1);
        Assert.IsTrue(b is >= 0 and <= 1);
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
        Assert.AreEqual(1.0, zones[3].R);
        Assert.AreEqual(0.0, zones[0].R);
    }
}
