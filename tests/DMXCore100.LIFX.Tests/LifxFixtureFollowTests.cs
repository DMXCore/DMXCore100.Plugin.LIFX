using DMXCore.PluginSdk;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxFixtureFollowTests
{
    [TestMethod]
    public void Matches_BareCodeAndFixturePrefix()
    {
        Assert.IsTrue(LifxFixtureFollow.Matches("HOUSE", "HOUSE"));
        Assert.IsTrue(LifxFixtureFollow.Matches("HOUSE", "fixture.HOUSE"));
        Assert.IsTrue(LifxFixtureFollow.Matches("house", "fixture.House"));
        Assert.IsFalse(LifxFixtureFollow.Matches("HOUSE", "fixture.WASH"));
        Assert.IsFalse(LifxFixtureFollow.Matches("HOUSE", "system.masterdimmer"));
    }

    [TestMethod]
    public void TryParse_GetFixtureJson_UsesZeroToOneChannels()
    {
        Assert.IsTrue(LifxFixtureFollow.TryParse(
            """{"red":1,"green":0.55,"blue":0.16,"intensity":0.8}""",
            out LifxLook.Color color));

        Assert.AreEqual(255, color.R);
        Assert.AreEqual(140, color.G);
        Assert.AreEqual(41, color.B);
        Assert.AreEqual(0.8, color.Brightness, 1e-9);
    }

    [TestMethod]
    public void TryParse_ByteRgbAndPercentBrightness()
    {
        Assert.IsTrue(LifxFixtureFollow.TryParse(
            """{"r":255,"g":140,"b":40,"brightness":50}""",
            out LifxLook.Color color));

        Assert.AreEqual(255, color.R);
        Assert.AreEqual(140, color.G);
        Assert.AreEqual(40, color.B);
        Assert.AreEqual(0.5, color.Brightness, 1e-9);
    }

    [TestMethod]
    public void TryParse_CsvRgb()
    {
        Assert.IsTrue(LifxFixtureFollow.TryParse("10,20,30,50", out LifxLook.Color color));
        Assert.AreEqual(10, color.R);
        Assert.AreEqual(20, color.G);
        Assert.AreEqual(30, color.B);
        Assert.AreEqual(0.5, color.Brightness, 1e-9);
    }

    [TestMethod]
    public void TryParse_RejectsMalformedPayloads()
    {
        Assert.IsFalse(LifxFixtureFollow.TryParse("{not json", out _));
        Assert.IsFalse(LifxFixtureFollow.TryParse("10,20", out _));
        Assert.IsFalse(LifxFixtureFollow.TryParse("10,20,x,50", out _));
    }

    [TestMethod]
    public void TryRead_LevelOnly_IsWhite()
    {
        Assert.IsTrue(LifxFixtureFollow.TryRead(
            new PluginEntityState { Code = "fixture.HOUSE", Level = 0.4 },
            out LifxLook.Color color));

        Assert.AreEqual(255, color.R);
        Assert.AreEqual(255, color.G);
        Assert.AreEqual(255, color.B);
        Assert.AreEqual(0.4, color.Brightness, 1e-9);
    }

    [TestMethod]
    public void NormalizeCode_TrimsAndRejectsEmpty()
    {
        Assert.AreEqual("HOUSE", LifxFixtureFollow.NormalizeCode("  HOUSE  "));
        Assert.IsNull(LifxFixtureFollow.NormalizeCode(""));
        Assert.IsNull(LifxFixtureFollow.NormalizeCode("   "));
    }
}
