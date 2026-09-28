using System.Buffers.Binary;
using System.Net;

namespace DMXCore100.LIFX.Tests;

/// <summary>
/// Adaptive transition: big jumps snap (0 ms), small frame-to-frame steps keep
/// the smooth StreamDurationMs blend.
/// </summary>
[TestClass]
public class LifxTransitionTests
{
    private const int Smooth = LifxConstants.StreamDurationMs;

    private static Hsbk Color(double hueDegrees, double saturation, double brightness, int kelvin = 3500) =>
        new(
            (ushort)Math.Round(hueDegrees / 360.0 * 65536),
            (ushort)Math.Round(saturation * 65535),
            (ushort)Math.Round(brightness * 65535),
            (ushort)kelvin);

    [TestMethod]
    public void NoPreviousFrame_KeepsTheSmoothDefault()
    {
        Assert.AreEqual(Smooth, LifxTransition.DurationMs((Hsbk?)null, Color(0, 1, 1)));
        Assert.AreEqual(Smooth, LifxTransition.DurationMs((IReadOnlyList<Hsbk>?)null, [Color(0, 1, 1)]));
    }

    [TestMethod]
    public void FlashBrightnessJump_Snaps()
    {
        // The measured case: a 24 % to 100 % flash and back
        Assert.AreEqual(0, LifxTransition.DurationMs(Color(52, 1, 0.24), Color(52, 1, 1.0)));
        Assert.AreEqual(0, LifxTransition.DurationMs(Color(52, 1, 1.0), Color(52, 1, 0.24)));
    }

    [TestMethod]
    public void SmallBrightnessStep_Blends()
    {
        // A fader move or slow fade: a few percent per frame
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(52, 1, 0.50), Color(52, 1, 0.55)));
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(52, 1, 0.20), Color(52, 1, 0.34)));
    }

    [TestMethod]
    public void BigHueChange_Snaps_SmallOneBlends()
    {
        Assert.AreEqual(0, LifxTransition.DurationMs(Color(0, 1, 1), Color(240, 1, 1)));
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(100, 1, 1), Color(110, 1, 1)));
    }

    [TestMethod]
    public void HueIsMeasuredTheShortWayRound()
    {
        Assert.AreEqual(20.0, LifxTransition.HueDegrees(Color(350, 1, 1).Hue, Color(10, 1, 1).Hue), 0.1);
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(350, 1, 1), Color(10, 1, 1)));
    }

    [TestMethod]
    public void HueChange_OnAWhiteOrDarkZone_DoesNotSnap()
    {
        // Hue carries no visible color at zero saturation or near-black
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(0, 0, 1), Color(200, 0, 1)));
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(0, 1, 0.02), Color(200, 1, 0.03)));
    }

    [TestMethod]
    public void SaturationJump_Snaps()
    {
        // White to full red at the same brightness
        Assert.AreEqual(0, LifxTransition.DurationMs(Color(0, 0, 1), Color(0, 1, 1)));
    }

    [TestMethod]
    public void WhiteTemperatureJump_Snaps_OnlyForWhites()
    {
        Assert.AreEqual(0, LifxTransition.DurationMs(Color(0, 0, 1, 2700), Color(0, 0, 1, 6500)));
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(0, 0, 1, 2700), Color(0, 0, 1, 3000)));
        // Kelvin is irrelevant on a saturated color
        Assert.AreEqual(Smooth, LifxTransition.DurationMs(Color(0, 1, 1, 2700), Color(0, 1, 1, 6500)));
    }

    [TestMethod]
    public void MultiZone_AnyZoneJumping_SnapsTheFrame()
    {
        Hsbk[] before = [Color(52, 1, 0.24), Color(52, 1, 0.24), Color(52, 1, 0.24)];
        Hsbk[] fadeStep = [Color(52, 1, 0.26), Color(52, 1, 0.26), Color(52, 1, 0.26)];
        Hsbk[] oneZoneFlash = [Color(52, 1, 0.24), Color(52, 1, 1.0), Color(52, 1, 0.24)];

        Assert.AreEqual(Smooth, LifxTransition.DurationMs(before, fadeStep));
        Assert.AreEqual(0, LifxTransition.DurationMs(before, oneZoneFlash));
    }

    [TestMethod]
    public void MultiZone_DifferentZoneCount_KeepsTheSmoothDefault()
    {
        Assert.AreEqual(Smooth, LifxTransition.DurationMs([Color(0, 1, 0)], [Color(0, 1, 1), Color(0, 1, 1)]));
    }

    [TestMethod]
    public async Task ColorSession_FlashSnaps_FaderStepBlends()
    {
        var durations = new List<uint>();
        LifxDatagramSender sender = (_, packet, _) =>
        {
            if (LifxPackets.ReadMessageType(packet.Span) == LifxConstants.SetColor)
            {
                durations.Add(BinaryPrimitives.ReadUInt32LittleEndian(packet.Span.Slice(LifxConstants.HeaderSize + 9, 4)));
            }

            return ValueTask.CompletedTask;
        };

        await using var session = new LifxColorSession(
            LifxColorMode.Rgb,
            new IPEndPoint(IPAddress.Loopback, LifxConstants.Port),
            [1, 2, 3, 4, 5, 6, 0, 0],
            sender);

        await session.SendAsync(new byte[] { 61, 61, 0 }, CancellationToken.None);   // 24 % yellow: first frame
        await session.SendAsync(new byte[] { 255, 255, 0 }, CancellationToken.None); // flash to 100 %
        await session.SendAsync(new byte[] { 61, 61, 0 }, CancellationToken.None);   // release
        await session.SendAsync(new byte[] { 66, 66, 0 }, CancellationToken.None);   // small fader step

        CollectionAssert.AreEqual(new uint[] { Smooth, 0, 0, Smooth }, durations);
    }
}
