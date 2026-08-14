namespace DMXCore100.LIFX.Tests;

internal sealed class FakeLifxClient : ILifxLanClient
{
    public List<LifxLight> Lights { get; } = [];

    public List<ColorCall> Colors { get; } = [];

    public List<ZoneCall> Zones { get; } = [];

    public List<(string Id, bool On)> Powers { get; } = [];

    public int DiscoverCalls { get; private set; }

    public List<string> Probes { get; } = [];

    public IReadOnlyList<LifxLight> GetLights() => Lights.ToArray();

    public Task<IReadOnlyList<LifxLight>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DiscoverCalls++;
        return Task.FromResult(GetLights());
    }

    public Task ProbeAsync(string ip, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Probes.Add(ip);
        return Task.CompletedTask;
    }

    public void SetRgb(LifxLight light, double r, double g, double b, int kelvin, int durationMs, double brightness)
    {
        Hsbk color = LifxColor.ScaleBrightness(LifxColor.RgbToHsbk(r, g, b, kelvin, light.CurrentHue), brightness);
        light.CurrentHue = color.Hue;
        light.CurrentSaturation = color.Saturation;
        light.CurrentBrightness = color.Brightness;
        light.CurrentKelvin = color.Kelvin;
        light.CurrentRgb = LifxColor.HsbkToRgb8(color);
        light.Power = 65535;
        Colors.Add(new ColorCall(light.Id, r, g, b, kelvin, durationMs, brightness));
    }

    public void SetZones(LifxLight light, IReadOnlyList<Rgb01> zones, int kelvin, int durationMs, double brightness)
    {
        Zones.Add(new ZoneCall(light.Id, zones.Count, kelvin, durationMs, brightness));
        if (zones.Count > 0)
        {
            SetRgb(light, zones[0].R, zones[0].G, zones[0].B, kelvin, durationMs, brightness);
        }
    }

    public void SetPower(LifxLight light, bool on)
    {
        light.Power = on ? 65535 : 0;
        Powers.Add((light.Id, on));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal readonly record struct ColorCall(string Id, double R, double G, double B, int Kelvin, int DurationMs, double Brightness);

    internal readonly record struct ZoneCall(string Id, int Count, int Kelvin, int DurationMs, double Brightness);
}
