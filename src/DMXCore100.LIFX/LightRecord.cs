using System.Text.Json;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.LIFX;

/// <summary>
/// Last-discovered lights, persisted in the plugin state blob so a restart
/// can probe known IPs before a full broadcast.
/// </summary>
public sealed class LightRecord
{
    public List<Entry> Lights { get; set; } = [];

    public sealed class Entry
    {
        public string Id { get; set; } = "";

        public string Ip { get; set; } = "";

        public string Label { get; set; } = "";

        public int Product { get; set; }
    }

    public static async Task<LightRecord> Load(IPluginHost host, CancellationToken cancellationToken)
    {
        try
        {
            string? json = await host.GetStateJsonAsync(cancellationToken);
            if (!string.IsNullOrEmpty(json))
            {
                return JsonSerializer.Deserialize<LightRecord>(json) ?? new LightRecord();
            }
        }
        catch (JsonException ex)
        {
            host.Logger.LogWarning(ex, "Discarding corrupt LIFX light record");
        }

        return new LightRecord();
    }

    public static Task Save(IPluginHost host, IReadOnlyList<LifxLight> lights, CancellationToken cancellationToken)
    {
        var record = new LightRecord
        {
            Lights = lights
                .Select(light => new Entry
                {
                    Id = light.Id,
                    Ip = light.Ip,
                    Label = light.Label,
                    Product = (int)light.Product,
                })
                .OrderBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList(),
        };

        return host.SetStateJsonAsync(JsonSerializer.Serialize(record), cancellationToken);
    }
}
