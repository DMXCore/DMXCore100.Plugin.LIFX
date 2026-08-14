using System.Text.Json;

namespace DMXCore100.LIFX;

public static class LifxStatus
{
    public static string LightsTopic(string serial) => $"dmxcore/{serial}/lifx/lights";

    public static string CommandTopic(string serial) => $"dmxcore/{serial}/lifx/command";

    public static string LightSetTopic(string serial, string lightId) => $"dmxcore/{serial}/lifx/{lightId}/set";

    public static string LightStateTopic(string serial, string lightId) => $"dmxcore/{serial}/lifx/{lightId}/state";

    public static string LightsPayload(IReadOnlyList<LifxLight> lights) =>
        JsonSerializer.Serialize(new
        {
            count = lights.Count,
            lights = lights.Select(light => new
            {
                id = light.Id,
                label = light.DisplayLabel,
                ip = light.Ip,
                model = light.ModelName,
                zones = light.ZoneCount,
                layout = light.EffectiveLayout.ToString().ToLowerInvariant(),
            }),
        });

    public static string LightStatePayload(LifxLight light, LifxEffectKind effect)
    {
        (int r, int g, int b) = light.CurrentRgb;
        return JsonSerializer.Serialize(new
        {
            id = light.Id,
            label = light.DisplayLabel,
            r,
            g,
            b,
            brightness = Math.Round(light.CurrentBrightness / 65535.0, 3),
            kelvin = light.CurrentKelvin,
            power = light.Power > 0,
            effect = EffectName(effect),
        });
    }

    public static string EffectName(LifxEffectKind kind) => kind switch
    {
        LifxEffectKind.None => "none",
        LifxEffectKind.Chase => "chase",
        LifxEffectKind.Sinewave => "sinewave",
        LifxEffectKind.Rainbow => "rainbow",
        LifxEffectKind.PixelChase => "pixel-chase",
        _ => throw Unexpected(kind),
    };

    private static InvalidOperationException Unexpected(LifxEffectKind kind)
    {
        throw new InvalidOperationException($"Unhandled effect {kind}");
    }
}
