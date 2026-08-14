namespace DMXCore100.LIFX;

public interface ILifxLanClient : IAsyncDisposable
{
    IReadOnlyList<LifxLight> GetLights();

    Task<IReadOnlyList<LifxLight>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task ProbeAsync(string ip, TimeSpan timeout, CancellationToken cancellationToken);

    void SetRgb(LifxLight light, double r, double g, double b, int kelvin, int durationMs, double brightness);

    void SetZones(LifxLight light, IReadOnlyList<Rgb01> zones, int kelvin, int durationMs, double brightness);

    void SetPower(LifxLight light, bool on);
}
