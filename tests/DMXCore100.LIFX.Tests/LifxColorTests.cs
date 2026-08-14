namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxColorTests
{
    [TestMethod]
    public void RgbToHsbk_PureColors()
    {
        Hsbk red = LifxColor.RgbToHsbk(1, 0, 0);
        Assert.AreEqual(0, red.Hue);
        Assert.AreEqual(65535, red.Saturation);
        Assert.AreEqual(65535, red.Brightness);

        Hsbk green = LifxColor.RgbToHsbk(0, 1, 0);
        Assert.IsTrue(green.Hue is > 20000 and < 23000);
        Assert.AreEqual(65535, green.Saturation);

        Hsbk blue = LifxColor.RgbToHsbk(0, 0, 1);
        Assert.IsTrue(blue.Hue is > 40000 and < 46000);
        Assert.AreEqual(65535, blue.Saturation);
    }

    [TestMethod]
    public void RgbToHsbk_WhiteAndBlack()
    {
        Hsbk white = LifxColor.RgbToHsbk(1, 1, 1);
        Assert.AreEqual(0, white.Saturation);
        Assert.AreEqual(65535, white.Brightness);

        Hsbk black = LifxColor.RgbToHsbk(0, 0, 0);
        Assert.AreEqual(0, black.Brightness);
        Assert.AreEqual(LifxConstants.DefaultKelvin, black.Kelvin);
    }

    [TestMethod]
    public void RgbToHsbk_ClampsAndCustomKelvin()
    {
        Hsbk high = LifxColor.RgbToHsbk(2, 2, 2);
        Assert.AreEqual(65535, high.Brightness);

        Hsbk low = LifxColor.RgbToHsbk(-1, -1, -1);
        Assert.AreEqual(0, low.Brightness);

        Hsbk custom = LifxColor.RgbToHsbk(0.5, 0.5, 0.5, kelvin: 5000);
        Assert.AreEqual(5000, custom.Kelvin);
        Assert.AreEqual(LifxConstants.KelvinMin, LifxColor.RgbToHsbk(1, 1, 1, kelvin: 1).Kelvin);
        Assert.AreEqual(LifxConstants.KelvinMax, LifxColor.RgbToHsbk(1, 1, 1, kelvin: 20000).Kelvin);
        Assert.AreEqual(0, custom.Saturation);
        Assert.IsTrue(custom.Brightness is > 30000 and < 35000);
    }
}
