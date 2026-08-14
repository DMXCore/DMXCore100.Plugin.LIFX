using System.Globalization;
using System.Text.Json;

namespace DMXCore100.LIFX;

/// <summary>
/// Parses MQTT payloads into LIFX commands. Accepts JSON
/// (<c>{"cmd":"color","target":"all","r":255,"g":0,"b":0}</c>) or
/// space-separated text (<c>color all 255 0 0</c>).
/// </summary>
public static class LifxCommandParser
{
    public static bool TryParse(string payload, out LifxCommand? command, out string? error)
    {
        command = null;
        error = null;
        string text = payload.Trim();
        if (text.Length == 0)
        {
            error = "empty payload";
            return false;
        }

        if (text.StartsWith('{'))
        {
            return TryParseJson(text, out command, out error);
        }

        return TryParseText(text, out command, out error);
    }

    public static LifxEffectKind ParseEffectName(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "none" or "stop" => LifxEffectKind.None,
            "chase" => LifxEffectKind.Chase,
            "sine" or "sinewave" => LifxEffectKind.Sinewave,
            "rainbow" => LifxEffectKind.Rainbow,
            "pixel" or "pixel-chase" or "pixelchase" => LifxEffectKind.PixelChase,
            _ => throw new FormatException($"Unknown effect '{name}'"),
        };
    }

    private static bool TryParseJson(string text, out LifxCommand? command, out string? error)
    {
        command = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string cmd = ReadString(root, "cmd") ?? ReadString(root, "command") ?? "";
            string target = ReadString(root, "target") ?? "all";
            switch (cmd.ToLowerInvariant())
            {
                case "discover":
                    command = new LifxCommand.Discover();
                    return true;
                case "list":
                    command = new LifxCommand.ListLights();
                    return true;
                case "color":
                case "colour":
                    command = new LifxCommand.Color(
                        target,
                        ReadByte(root, "r"),
                        ReadByte(root, "g"),
                        ReadByte(root, "b"),
                        ReadBrightness(root),
                        ReadOptionalInt(root, "fade_ms"));
                    return true;
                case "power":
                    command = new LifxCommand.Power(target, ReadOn(root));
                    return true;
                case "effect":
                    string name = ReadString(root, "name") ?? ReadString(root, "effect") ?? "stop";
                    command = new LifxCommand.Effect(target, ParseEffectName(name), ReadOptionalInt(root, "speed_ms"));
                    return true;
                default:
                    error = $"unknown cmd '{cmd}'";
                    return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseText(string text, out LifxCommand? command, out string? error)
    {
        command = null;
        error = null;
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "discover":
                    command = new LifxCommand.Discover();
                    return true;
                case "list":
                    command = new LifxCommand.ListLights();
                    return true;
                case "color":
                case "colour":
                    if (parts.Length < 5)
                    {
                        error = "usage: color <target> <r> <g> <b> [brightness] [fade_ms]";
                        return false;
                    }

                    command = new LifxCommand.Color(
                        parts[1],
                        ParseByte(parts[2]),
                        ParseByte(parts[3]),
                        ParseByte(parts[4]),
                        parts.Length > 5 ? ParseBrightness(parts[5]) : 1.0,
                        parts.Length > 6 ? int.Parse(parts[6], CultureInfo.InvariantCulture) : null);
                    return true;
                case "power":
                    if (parts.Length < 3)
                    {
                        error = "usage: power <target> on|off";
                        return false;
                    }

                    command = new LifxCommand.Power(parts[1], ParseOn(parts[2]));
                    return true;
                case "effect":
                    if (parts.Length < 3)
                    {
                        error = "usage: effect <target> chase|sinewave|rainbow|pixel-chase|stop [speed_ms]";
                        return false;
                    }

                    command = new LifxCommand.Effect(
                        parts[1],
                        ParseEffectName(parts[2]),
                        parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : null);
                    return true;
                default:
                    error = $"unknown command '{parts[0]}'";
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadByte(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        int number = value.GetInt32();
        return Math.Clamp(number, 0, 255);
    }

    private static double ReadBrightness(JsonElement root)
    {
        if (!root.TryGetProperty("brightness", out JsonElement value))
        {
            return 1.0;
        }

        return ParseBrightness(value.GetDouble().ToString(CultureInfo.InvariantCulture));
    }

    private static int? ReadOptionalInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.GetInt32();
    }

    private static bool ReadOn(JsonElement root)
    {
        if (root.TryGetProperty("on", out JsonElement value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => ParseOn(value.GetString() ?? ""),
                _ => value.GetInt32() != 0,
            };
        }

        string? power = ReadString(root, "power");
        return power != null && ParseOn(power);
    }

    private static int ParseByte(string text)
    {
        int value = int.Parse(text, CultureInfo.InvariantCulture);
        return Math.Clamp(value, 0, 255);
    }

    private static double ParseBrightness(string text)
    {
        double value = double.Parse(text, CultureInfo.InvariantCulture);
        if (value > 1.0)
        {
            value /= 100.0;
        }

        return LifxColor.Clamp01(value);
    }

    private static bool ParseOn(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" => true,
        "off" or "0" or "false" => false,
        _ => throw new FormatException($"Expected on/off, got '{text}'"),
    };
}
