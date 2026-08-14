namespace DMXCore100.LIFX.Tests;

internal sealed class FakeLifxClient : ILifxLanClient
{
    private readonly Lock gate = new();
    private readonly List<LifxLight> lights = [];
    private readonly List<ColorCall> colors = [];
    private readonly List<ZoneCall> zones = [];
    private readonly List<(string Id, bool On)> powers = [];
    private readonly List<string> probes = [];
    private int discoverCalls;

    public IReadOnlyList<LifxLight> Lights
    {
        get
        {
            lock (this.gate)
            {
                return this.lights.ToArray();
            }
        }
    }

    public IReadOnlyList<ColorCall> Colors
    {
        get
        {
            lock (this.gate)
            {
                return this.colors.ToArray();
            }
        }
    }

    public IReadOnlyList<ZoneCall> Zones
    {
        get
        {
            lock (this.gate)
            {
                return this.zones.ToArray();
            }
        }
    }

    public IReadOnlyList<(string Id, bool On)> Powers
    {
        get
        {
            lock (this.gate)
            {
                return this.powers.ToArray();
            }
        }
    }

    public int DiscoverCalls
    {
        get
        {
            lock (this.gate)
            {
                return this.discoverCalls;
            }
        }
    }

    public IReadOnlyList<string> Probes
    {
        get
        {
            lock (this.gate)
            {
                return this.probes.ToArray();
            }
        }
    }

    public void AddLight(LifxLight light)
    {
        lock (this.gate)
        {
            this.lights.Add(light);
        }
    }

    public void Reset()
    {
        lock (this.gate)
        {
            this.colors.Clear();
            this.zones.Clear();
            this.powers.Clear();
            this.probes.Clear();
            this.discoverCalls = 0;
        }
    }

    public IReadOnlyList<LifxLight> GetLights()
    {
        lock (this.gate)
        {
            return this.lights.ToArray();
        }
    }

    public Task<IReadOnlyList<LifxLight>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.discoverCalls++;
            return Task.FromResult<IReadOnlyList<LifxLight>>(this.lights.ToArray());
        }
    }

    public Task ProbeAsync(string ip, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.probes.Add(ip);
        }

        return Task.CompletedTask;
    }

    public void SetRgb(LifxLight light, double r, double g, double b, int kelvin, int durationMs, double brightness)
    {
        lock (this.gate)
        {
            ApplyColor(light, r, g, b, kelvin, brightness);
            this.colors.Add(new ColorCall(light.Id, r, g, b, kelvin, durationMs, brightness));
        }
    }

    public void SetZones(LifxLight light, IReadOnlyList<Rgb01> zones, int kelvin, int durationMs, double brightness)
    {
        lock (this.gate)
        {
            this.zones.Add(new ZoneCall(light.Id, zones.Count, kelvin, durationMs, brightness));
            if (zones.Count > 0)
            {
                ApplyColor(light, zones[0].R, zones[0].G, zones[0].B, kelvin, brightness);
            }
        }
    }

    public void SetPower(LifxLight light, bool on)
    {
        lock (this.gate)
        {
            light.Power = on ? 65535 : 0;
            this.powers.Add((light.Id, on));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void ApplyColor(LifxLight light, double r, double g, double b, int kelvin, double brightness)
    {
        Hsbk color = LifxColor.ScaleBrightness(LifxColor.RgbToHsbk(r, g, b, kelvin, light.CurrentHue), brightness);
        light.CurrentHue = color.Hue;
        light.CurrentSaturation = color.Saturation;
        light.CurrentBrightness = color.Brightness;
        light.CurrentKelvin = color.Kelvin;
        light.CurrentRgb = LifxColor.HsbkToRgb8(color);
        light.Power = 65535;
    }

    internal readonly record struct ColorCall(string Id, double R, double G, double B, int Kelvin, int DurationMs, double Brightness);

    internal readonly record struct ZoneCall(string Id, int Count, int Kelvin, int DurationMs, double Brightness);
}
