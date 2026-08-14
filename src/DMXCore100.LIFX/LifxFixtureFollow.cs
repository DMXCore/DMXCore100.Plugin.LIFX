using System.Globalization;
using System.Text.Json;
using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// Resolves a Fixture Control fixture code to a LIFX look. Accepts the
/// <c>dmx.getFixture</c> JSON shape (<c>red</c>/<c>green</c>/<c>blue</c>/<c>intensity</c>
/// in 0–1, or 0–255 / 0–100 when the value is greater than 1) and the
/// <c>fixture.CODE</c> entity namespace.
/// </summary>
public static class LifxFixtureFollow
{
    public const string EntityPrefix = "fixture.";

    public static string? NormalizeCode(string? text)
    {
        string code = text?.Trim() ?? "";
        return code.Length == 0 ? null : code;
    }

    public static bool Matches(string fixtureCode, string entityCode)
    {
        if (string.Equals(fixtureCode, entityCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entityCode.StartsWith(EntityPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(fixtureCode, entityCode[EntityPrefix.Length..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(EntityPrefix + fixtureCode, entityCode, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> EntityCodes(string fixtureCode)
    {
        yield return EntityPrefix + fixtureCode;
        yield return fixtureCode;
    }

    public static bool TryRead(PluginEntityState state, out LifxLook.Color color)
    {
        if (!string.IsNullOrWhiteSpace(state.Text) && TryParse(state.Text, out color))
        {
            return true;
        }

        if (state.Level is double level)
        {
            color = new LifxLook.Color(255, 255, 255, LifxColor.Clamp01(level));
            return true;
        }

        color = new LifxLook.Color(0, 0, 0, 0);
        return false;
    }

    public static bool TryParse(string text, out LifxLook.Color color)
    {
        color = new LifxLook.Color(0, 0, 0, 0);
        string value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        return value.StartsWith('{')
            ? TryParseJson(value, out color)
            : TryParseRgb(value, out color);
    }

    private static bool TryParseRgb(string value, out LifxLook.Color color)
    {
        color = new LifxLook.Color(0, 0, 0, 0);
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
        {
            return false;
        }

        double brightness = 1.0;
        if (parts.Length == 4
            && !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out brightness))
        {
            return false;
        }

        if (brightness > 1.0)
        {
            brightness /= 100.0;
        }

        color = new LifxLook.Color(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255), LifxColor.Clamp01(brightness));
        return true;
    }

    private static bool TryParseJson(string json, out LifxLook.Color color)
    {
        color = new LifxLook.Color(0, 0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool hasRgb = TryChannel(root, out double red, "red", "r")
                | TryChannel(root, out double green, "green", "g")
                | TryChannel(root, out double blue, "blue", "b");
            bool hasWhite = TryChannel(root, out double white, "white", "w");
            if (!hasRgb && hasWhite)
            {
                red = green = blue = white;
                hasRgb = true;
            }

            if (!hasRgb)
            {
                if (!TryChannel(root, out _, "intensity", "brightness", "level"))
                {
                    return false;
                }

                red = green = blue = 1.0;
            }

            double intensity = 1.0;
            if (TryChannel(root, out double parsedIntensity, "intensity", "brightness", "level"))
            {
                intensity = parsedIntensity > 1.0 ? parsedIntensity / 100.0 : parsedIntensity;
            }

            color = new LifxLook.Color(
                ToByte(red),
                ToByte(green),
                ToByte(blue),
                LifxColor.Clamp01(intensity));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryChannel(JsonElement root, out double value, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement property)
                || property.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static int ToByte(double channel) =>
        channel > 1.0
            ? Math.Clamp((int)Math.Round(channel), 0, 255)
            : (int)Math.Round(LifxColor.Clamp01(channel) * 255.0);
}
