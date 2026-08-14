namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxCommandParserTests
{
    [TestMethod]
    public void Parse_TextDiscoverAndColor()
    {
        Assert.IsTrue(LifxCommandParser.TryParse("discover", out LifxCommand? discover, out _));
        Assert.IsInstanceOfType<LifxCommand.Discover>(discover);

        Assert.IsTrue(LifxCommandParser.TryParse("color kitchen 255 0 128 50 200", out LifxCommand? color, out _));
        var parsed = (LifxCommand.Color)color!;
        Assert.AreEqual("kitchen", parsed.Target);
        Assert.AreEqual(255, parsed.R);
        Assert.AreEqual(0, parsed.G);
        Assert.AreEqual(128, parsed.B);
        Assert.AreEqual(0.5, parsed.Brightness, 1e-9);
        Assert.AreEqual(200, parsed.FadeMs);
    }

    [TestMethod]
    public void Parse_TextPowerAndEffect()
    {
        Assert.IsTrue(LifxCommandParser.TryParse("power all off", out LifxCommand? power, out _));
        Assert.IsFalse(((LifxCommand.Power)power!).On);

        Assert.IsTrue(LifxCommandParser.TryParse("effect all sinewave 300", out LifxCommand? effect, out _));
        var parsed = (LifxCommand.Effect)effect!;
        Assert.AreEqual(LifxEffectKind.Sinewave, parsed.Kind);
        Assert.AreEqual(300, parsed.SpeedMs);
    }

    [TestMethod]
    public void Parse_JsonColorAndEffect()
    {
        Assert.IsTrue(LifxCommandParser.TryParse(
            """{"cmd":"color","target":"all","r":10,"g":20,"b":30,"brightness":0.25,"fade_ms":90}""",
            out LifxCommand? color,
            out _));
        var parsed = (LifxCommand.Color)color!;
        Assert.AreEqual("all", parsed.Target);
        Assert.AreEqual(10, parsed.R);
        Assert.AreEqual(0.25, parsed.Brightness, 1e-9);
        Assert.AreEqual(90, parsed.FadeMs);

        Assert.IsTrue(LifxCommandParser.TryParse(
            """{"cmd":"effect","target":"tube","name":"pixel-chase"}""",
            out LifxCommand? effect,
            out _));
        Assert.AreEqual(LifxEffectKind.PixelChase, ((LifxCommand.Effect)effect!).Kind);
    }

    [TestMethod]
    public void Parse_Unknown_ReturnsError()
    {
        Assert.IsFalse(LifxCommandParser.TryParse("nope", out _, out string? error));
        Assert.IsTrue(error!.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Resolve_MatchesIdLabelIpAndAll()
    {
        var kitchen = new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen");
        var bar = new LifxLight([8, 7, 6, 5, 4, 3, 2, 1], "192.168.1.11", "Bar");
        LifxLight[] lights = [kitchen, bar];

        Assert.AreEqual(2, LifxTargets.Resolve(lights, "all").Count);
        Assert.AreEqual(kitchen.Id, LifxTargets.Resolve(lights, kitchen.Id).Single().Id);
        Assert.AreEqual(bar.Id, LifxTargets.Resolve(lights, "Bar").Single().Id);
        Assert.AreEqual(kitchen.Id, LifxTargets.Resolve(lights, "192.168.1.10").Single().Id);
    }
}
