using System.Text.Json;

namespace DMXCore100.LIFX;

/// <summary>
/// JSON shape of one discovered light in the plugin's persistent state blob,
/// so targets and pixel geometry survive restarts instead of living only in
/// the RAM discovery cache.
/// </summary>
internal sealed class LifxLightState
{
    public string Target { get; set; } = "";

    public string Ip { get; set; } = "";

    public string Label { get; set; } = "";

    public uint Vendor { get; set; }

    public uint Product { get; set; }

    public string ModelName { get; set; } = "";

    public string Layout { get; set; } = nameof(LifxLayout.Single);

    public int ZoneCount { get; set; } = 1;

    public int MatrixWidth { get; set; } = 1;

    public int MatrixHeight { get; set; } = 1;

    public int TileCount { get; set; } = 1;

    public static string Serialize(IReadOnlyList<LifxLight> lights)
    {
        var states = lights.Select(static light => new LifxLightState
        {
            Target = Convert.ToHexString(light.Target),
            Ip = light.Ip,
            Label = light.Label,
            Vendor = light.Vendor,
            Product = light.Product,
            ModelName = light.ModelName,
            Layout = light.Layout.ToString(),
            ZoneCount = light.ZoneCount,
            MatrixWidth = light.MatrixWidth,
            MatrixHeight = light.MatrixHeight,
            TileCount = light.TileCount,
        }).ToArray();

        return JsonSerializer.Serialize(new LifxPluginState { Lights = states });
    }

    public static IReadOnlyList<LifxLight> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        LifxPluginState? state;
        try
        {
            state = JsonSerializer.Deserialize<LifxPluginState>(json);
        }
        catch (JsonException)
        {
            return [];
        }

        var lights = new List<LifxLight>();
        foreach (LifxLightState item in state?.Lights ?? [])
        {
            byte[] target;
            try
            {
                target = Convert.FromHexString(item.Target);
            }
            catch (FormatException)
            {
                continue;
            }

            if (target.Length != 8 || string.IsNullOrWhiteSpace(item.Ip))
            {
                continue;
            }

            lights.Add(new LifxLight(target, item.Ip, item.Label)
            {
                Vendor = item.Vendor,
                Product = item.Product,
                ModelName = item.ModelName,
                Layout = Enum.TryParse(item.Layout, out LifxLayout layout) ? layout : LifxLayout.Single,
                ZoneCount = Math.Max(1, item.ZoneCount),
                MatrixWidth = Math.Max(1, item.MatrixWidth),
                MatrixHeight = Math.Max(1, item.MatrixHeight),
                TileCount = Math.Max(1, item.TileCount),
            });
        }

        return lights;
    }
}

internal sealed class LifxPluginState
{
    public LifxLightState[] Lights { get; set; } = [];
}
