namespace DMXCore100.LIFX;

public readonly record struct Hsbk(ushort Hue, ushort Saturation, ushort Brightness, ushort Kelvin);

public readonly record struct Rgb01(double R, double G, double B);

/// <summary>
/// RGB ↔ HSBK conversion matching the working DMX2LIFX LAN client
/// (Python colorsys, including hold-hue so dim chases do not flash red).
/// </summary>
public static class LifxColor
{
    public static double Clamp01(double value)
    {
        if (value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }

    public static Hsbk RgbToHsbk(double r, double g, double b, int kelvin = LifxConstants.DefaultKelvin, ushort? holdHue = null)
    {
        r = Clamp01(r);
        g = Clamp01(g);
        b = Clamp01(b);

        RgbToHsv(r, g, b, out double h, out double s, out double v);

        if (holdHue.HasValue && (s < 0.05 || v < 0.02))
        {
            h = (holdHue.Value & 0xFFFF) / 65535.0;
        }

        return new Hsbk(
            (ushort)((int)(h * 65535) & 0xFFFF),
            (ushort)((int)(s * 65535) & 0xFFFF),
            (ushort)((int)(v * 65535) & 0xFFFF),
            (ushort)Math.Clamp(kelvin, LifxConstants.KelvinMin, LifxConstants.KelvinMax));
    }

    public static double NormaliseBrightness(double value)
    {
        if (value > 1.0)
        {
            value /= 100.0;
        }

        return Clamp01(value);
    }

    public static Hsbk ScaleBrightness(Hsbk color, double brightness)
    {
        int scaled = Math.Clamp((int)(color.Brightness * Clamp01(brightness)), 0, 65535);
        return color with { Brightness = (ushort)scaled };
    }

    public static (int R, int G, int B) HsbkToRgb8(Hsbk color)
    {
        HsvToRgb(color.Hue / 65535.0, color.Saturation / 65535.0, color.Brightness / 65535.0, out double r, out double g, out double b);
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        if (s == 0.0)
        {
            r = g = b = v;
            return;
        }

        int i = (int)(h * 6.0);
        double f = (h * 6.0) - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - s * f);
        double t = v * (1.0 - s * (1.0 - f));
        switch (i % 6)
        {
            case 0:
                r = v; g = t; b = p;
                break;
            case 1:
                r = q; g = v; b = p;
                break;
            case 2:
                r = p; g = v; b = t;
                break;
            case 3:
                r = p; g = q; b = v;
                break;
            case 4:
                r = t; g = p; b = v;
                break;
            case 5:
                r = v; g = p; b = q;
                break;
            default:
                throw new InvalidOperationException($"Unhandled HSV sector {i}");
        }
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
