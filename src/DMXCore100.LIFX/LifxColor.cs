namespace DMXCore100.LIFX;

internal readonly record struct Hsbk(ushort Hue, ushort Saturation, ushort Brightness, ushort Kelvin);

/// <summary>
/// RGB → HSBK conversion for LIFX SetColor payloads.
/// </summary>
internal static class LifxColor
{
    public static Hsbk RgbToHsbk(double r, double g, double b, int kelvin = LifxConstants.DefaultKelvin)
    {
        r = Clamp01(r);
        g = Clamp01(g);
        b = Clamp01(b);

        RgbToHsv(r, g, b, out double h, out double s, out double v);

        return new Hsbk(
            (ushort)((int)(h * 65535) & 0xFFFF),
            (ushort)((int)(s * 65535) & 0xFFFF),
            (ushort)((int)(v * 65535) & 0xFFFF),
            (ushort)Math.Clamp(kelvin, LifxConstants.KelvinMin, LifxConstants.KelvinMax));
    }

    /// <summary>
    /// RGB plus a White channel → HSBK. White is mixed additively (the way an
    /// RGBW LED fixture behaves): it lifts every component equally, so it
    /// desaturates the color and raises brightness, and the resulting white
    /// portion is rendered by the bulb at <paramref name="kelvin"/>.
    /// </summary>
    public static Hsbk RgbwToHsbk(double r, double g, double b, double w, int kelvin = LifxConstants.DefaultKelvin)
    {
        w = Clamp01(w);
        return RgbToHsbk(r + w, g + w, b + w, kelvin);
    }

    /// <summary>
    /// Map an 8-bit ColorTemperature channel onto LIFX kelvin (warm at 0,
    /// cool at 255).
    /// </summary>
    public static int KelvinFromDmx(byte value) => KelvinFromUnit(value / 255.0);

    /// <summary>
    /// Map a 0..1 ColorTemperature value onto LIFX kelvin (warm at 0, cool
    /// at 1).
    /// </summary>
    public static int KelvinFromUnit(double t)
    {
        t = Clamp01(t);
        return (int)Math.Round(LifxConstants.KelvinMin + (t * (LifxConstants.KelvinMax - LifxConstants.KelvinMin)));
    }

    private static double Clamp01(double value)
    {
        if (value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double maxc = Math.Max(r, Math.Max(g, b));
        double minc = Math.Min(r, Math.Min(g, b));
        v = maxc;
        if (minc == maxc)
        {
            h = 0.0;
            s = 0.0;
            return;
        }

        s = (maxc - minc) / maxc;
        double rc = (maxc - r) / (maxc - minc);
        double gc = (maxc - g) / (maxc - minc);
        double bc = (maxc - b) / (maxc - minc);
        if (r == maxc)
        {
            h = bc - gc;
        }
        else if (g == maxc)
        {
            h = 2.0 + rc - bc;
        }
        else
        {
            h = 4.0 + gc - rc;
        }

        h = (h / 6.0) % 1.0;
        if (h < 0.0)
        {
            h += 1.0;
        }
    }
}
