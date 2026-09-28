namespace DMXCore100.LIFX;

/// <summary>
/// Picks the transition (fade) time for the next frame. Small frame-to-frame
/// steps keep <see cref="LifxConstants.StreamDurationMs"/> so consecutive
/// updates blend into a smooth fade; a big jump (a flash, bump, or snap) goes
/// out with no transition, because the firmware eases even a 75 ms fade into
/// something that reads as a soft half-second change. Hardware-checked on a
/// SuperColour Tube: 0 ms snaps, 75 ms visibly fades on a 24 % to 100 % step.
/// </summary>
internal static class LifxTransition
{
    /// <summary>Brightness change (fraction of full) that counts as a jump.</summary>
    public const double JumpBrightness = 0.15;

    /// <summary>Hue change (degrees, shortest way round) that counts as a jump.</summary>
    public const double JumpHueDegrees = 30.0;

    /// <summary>Saturation change (fraction of full) that counts as a jump.</summary>
    public const double JumpSaturation = 0.25;

    /// <summary>White temperature change (kelvin) that counts as a jump.</summary>
    public const int JumpKelvin = 1000;

    /// <summary>
    /// Below this brightness the color is too dark for hue, saturation, or
    /// white temperature to be visible, so only brightness can make a jump.
    /// </summary>
    public const double VisibleBrightness = 0.05;

    /// <summary>
    /// Below this saturation hue is meaningless (near-white), and at or above
    /// <see cref="WhiteSaturation"/> kelvin is (the color is not a white).
    /// </summary>
    public const double HueSaturation = 0.20;

    public const double WhiteSaturation = 0.50;

    /// <summary>
    /// Transition for a single-zone frame. With no previous frame (a new
    /// session) it keeps the smooth default.
    /// </summary>
    public static int DurationMs(Hsbk? previous, Hsbk next)
    {
        return previous is { } last && IsJump(last, next) ? 0 : LifxConstants.StreamDurationMs;
    }

    /// <summary>
    /// Transition for a multi-zone frame: one packet carries every zone, so a
    /// jump in any zone snaps the whole frame. A missing or differently sized
    /// previous frame keeps the smooth default.
    /// </summary>
    public static int DurationMs(IReadOnlyList<Hsbk>? previous, IReadOnlyList<Hsbk> next)
    {
        if (previous == null || previous.Count != next.Count)
        {
            return LifxConstants.StreamDurationMs;
        }

        for (int i = 0; i < next.Count; i++)
        {
            if (IsJump(previous[i], next[i]))
            {
                return 0;
            }
        }

        return LifxConstants.StreamDurationMs;
    }

    public static bool IsJump(Hsbk from, Hsbk to)
    {
        double fromBrightness = from.Brightness / 65535.0;
        double toBrightness = to.Brightness / 65535.0;
        if (Math.Abs(toBrightness - fromBrightness) >= JumpBrightness)
        {
            return true;
        }

        // Hue, saturation, and white temperature only show on a lit zone
        if (Math.Min(fromBrightness, toBrightness) < VisibleBrightness)
        {
            return false;
        }

        double fromSaturation = from.Saturation / 65535.0;
        double toSaturation = to.Saturation / 65535.0;
        if (Math.Abs(toSaturation - fromSaturation) >= JumpSaturation)
        {
            return true;
        }

        if (Math.Min(fromSaturation, toSaturation) >= HueSaturation && HueDegrees(from.Hue, to.Hue) >= JumpHueDegrees)
        {
            return true;
        }

        return Math.Max(fromSaturation, toSaturation) < WhiteSaturation
            && Math.Abs(to.Kelvin - from.Kelvin) >= JumpKelvin;
    }

    /// <summary>
    /// Shortest distance between two LIFX hues in degrees (0-180), so 350 to
    /// 10 degrees is a 20 degree step.
    /// </summary>
    internal static double HueDegrees(ushort from, ushort to)
    {
        int difference = Math.Abs(to - from);
        int shortest = Math.Min(difference, 65536 - difference);

        return shortest * 360.0 / 65536.0;
    }
}
