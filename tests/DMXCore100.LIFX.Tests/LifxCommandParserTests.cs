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
    public void Parse_IdentifyAndWhite()
    {
        Assert.IsTrue(LifxCommandParser.TryParse("identify", out LifxCommand? identify, out _));
        Assert.IsInstanceOfType<LifxCommand.Identify>(identify);

        Assert.IsTrue(LifxCommandParser.TryParse("white Kitchen 50", out LifxCommand? white, out _));
        var parsed = (LifxCommand.Color)white!;
        Assert.AreEqual("Kitchen", parsed.Target);
        Assert.AreEqual(255, parsed.R);
        Assert.AreEqual(255, parsed.G);
        Assert.AreEqual(255, parsed.B);
        Assert.AreEqual(0.5, parsed.Brightness, 1e-9);

        Assert.IsTrue(LifxCommandParser.TryParse("""{"cmd":"identify"}""", out LifxCommand? jsonIdentify, out _));
        Assert.IsInstanceOfType<LifxCommand.Identify>(jsonIdentify);
    }

    [TestMethod]
    public void Parse_JsonPower_RequiresOnOrPower()
    {
        Assert.IsFalse(LifxCommandParser.TryParse("""{"cmd":"power","target":"all"}""", out LifxCommand? missing, out string? error));
        Assert.IsNull(missing);
        Assert.IsTrue(error!.Contains("on or power", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(LifxCommandParser.TryParse("""{"cmd":"power","target":"all","on":true}""", out LifxCommand? on, out _));
        Assert.IsTrue(((LifxCommand.Power)on!).On);

        Assert.IsTrue(LifxCommandParser.TryParse("""{"cmd":"power","target":"all","power":"off"}""", out LifxCommand? power, out _));
        Assert.IsFalse(((LifxCommand.Power)power!).On);
    }

    [TestMethod]
    public void Parse_Unknown_ReturnsError()
    {
        Assert.IsFalse(LifxCommandParser.TryParse("nope", out _, out string? error));
        Assert.IsTrue(error!.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Parse_ColorAndColourAliases_AreEquivalent()
    {
        string[] payloads =
        [
            "color all 255 0 0",
            "colour all 255 0 0",
            "COLOR all 255 0 0",
            "COLOUR all 255 0 0",
            """{"cmd":"color","target":"all","r":255,"g":0,"b":0}""",
            """{"cmd":"colour","target":"all","r":255,"g":0,"b":0}""",
            """{"cmd":"Color","target":"all","r":255,"g":0,"b":0}""",
            """{"cmd":"Colour","target":"all","r":255,"g":0,"b":0}""",
        ];

        LifxCommand.Color? first = null;
        foreach (string payload in payloads)
        {
            Assert.IsTrue(LifxCommandParser.TryParse(payload, out LifxCommand? command, out string? error), error);
            var color = (LifxCommand.Color)command!;
            first ??= color;
            Assert.AreEqual(first.Target, color.Target);
            Assert.AreEqual(first.R, color.R);
            Assert.AreEqual(first.G, color.G);
            Assert.AreEqual(first.B, color.B);
        }
    }

    [TestMethod]
    public void Parse_NegativeFadeAndSpeed_AreClampedToZero()
    {
        Assert.IsTrue(LifxCommandParser.TryParse("color all 1 2 3 1 -40", out LifxCommand? textColor, out _));
        Assert.AreEqual(0, ((LifxCommand.Color)textColor!).FadeMs);

        Assert.IsTrue(LifxCommandParser.TryParse("effect all chase -10", out LifxCommand? textEffect, out _));
        Assert.AreEqual(0, ((LifxCommand.Effect)textEffect!).SpeedMs);

        Assert.IsTrue(LifxCommandParser.TryParse(
            """{"cmd":"color","target":"all","r":1,"g":2,"b":3,"fade_ms":-40}""",
            out LifxCommand? jsonColor,
            out _));
        Assert.AreEqual(0, ((LifxCommand.Color)jsonColor!).FadeMs);

        Assert.IsTrue(LifxCommandParser.TryParse(
            """{"cmd":"effect","target":"all","name":"chase","speed_ms":-10}""",
            out LifxCommand? jsonEffect,
            out _));
        Assert.AreEqual(0, ((LifxCommand.Effect)jsonEffect!).SpeedMs);
    }

    [TestMethod]
    public void Resolve_MatchesIdLabelIpAndAll()
    {
        var kitchen = new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen");
        var bar = new LifxLight([8, 7, 6, 5, 4, 3, 2, 1], "192.168.1.11", "Bar");
        LifxLight[] lights = [kitchen, bar];

        Assert.AreEqual(2, LifxTargets.Resolve(lights, "all").Count);
        Assert.AreEqual(2, LifxTargets.Resolve(lights, " all ").Count);
        Assert.AreEqual(0, LifxTargets.Resolve(lights, "   ").Count);
        Assert.AreEqual(kitchen.Id, LifxTargets.Resolve(lights, kitchen.Id).Single().Id);
        Assert.AreEqual(bar.Id, LifxTargets.Resolve(lights, "Bar").Single().Id);
        Assert.AreEqual(kitchen.Id, LifxTargets.Resolve(lights, "192.168.1.10").Single().Id);
    }
}
