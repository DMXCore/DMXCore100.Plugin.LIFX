namespace DMXCore100.LIFX;

/// <summary>
/// In-process chase / sinewave / rainbow / pixel-chase, matching the
/// DMX2LIFX test-RGB animations.
/// </summary>
public sealed class LifxEffectRunner : IDisposable
{
    private static readonly (double R, double G, double B)[] ChaseColors =
    [
        (1.0, 0.0, 0.0),
        (0.0, 1.0, 0.0),
        (0.0, 0.0, 1.0),
    ];

    private readonly ILifxLanClient client;
    private readonly Action<Exception>? onTickFailure;
    private readonly Dictionary<string, EffectState> running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastTickFailure = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private CancellationTokenSource? loopCts;
    private Task? loopTask;
    private bool disposed;

    public LifxEffectRunner(ILifxLanClient client, Action<Exception>? onTickFailure = null)
    {
        this.client = client;
        this.onTickFailure = onTickFailure;
    }

    public LifxEffectKind Current(string lightId)
    {
        lock (this.gate)
        {
            return this.running.TryGetValue(lightId, out EffectState? state) ? state.Kind : LifxEffectKind.None;
        }
    }

    public void Start(IReadOnlyList<LifxLight> lights, LifxEffectKind kind, int speedMs, double brightness, int fadeMs)
    {
        if (kind == LifxEffectKind.None)
        {
            Stop(lights);
            return;
        }

        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            foreach (LifxLight light in lights)
            {
                this.running[light.Id] = new EffectState(light, kind, Math.Max(80, speedMs), brightness, fadeMs);
            }

            EnsureLoop();
        }
    }

    public void Stop(IReadOnlyList<LifxLight>? lights = null)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            if (lights == null)
            {
                this.running.Clear();
            }
            else
            {
                foreach (LifxLight light in lights)
                {
                    this.running.Remove(light.Id);
                }
            }

            if (this.running.Count == 0)
            {
                this.loopCts?.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.running.Clear();
            this.loopCts?.Cancel();
        }

        try
        {
            this.loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (this.gate)
        {
            this.loopCts?.Dispose();
            this.loopCts = null;
        }
    }

    internal static (double R, double G, double B) SinewaveRgb(double phase)
    {
        return (
            (Math.Sin(phase) + 1) / 2,
            (Math.Sin(phase + (2 * Math.PI / 3)) + 1) / 2,
            (Math.Sin(phase + (4 * Math.PI / 3)) + 1) / 2);
    }

    internal static IReadOnlyList<Rgb01> RainbowZones(int count)
    {
        int n = Math.Max(1, count);
        return Enumerable.Range(0, n)
            .Select(i =>
            {
                LifxColor.HsvToRgb(i / (double)n, 1.0, 1.0, out double r, out double g, out double b);
                return new Rgb01(r, g, b);
            })
            .ToArray();
    }

    internal static IReadOnlyList<Rgb01> PixelChaseZones(int count, int index, double r, double g, double b)
    {
        int n = Math.Max(1, count);
        var zones = new Rgb01[n];
        zones[PositiveMod(index, n)] = new Rgb01(r, g, b);
        return zones;
    }

    private void EnsureLoop()
    {
        if (this.disposed)
        {
            return;
        }

        if (this.loopTask is { IsCompleted: false } && this.loopCts is { IsCancellationRequested: false })
        {
            return;
        }

        this.loopCts?.Dispose();
        this.loopCts = new CancellationTokenSource();
        this.loopTask = Task.Run(() => RunLoop(this.loopCts.Token));
    }

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            EffectState[] snapshot;
            lock (this.gate)
            {
                if (this.running.Count == 0)
                {
                    return;
                }

                snapshot = [.. this.running.Values];
            }

            DateTime now = DateTime.UtcNow;
            foreach (EffectState state in snapshot)
            {
                try
                {
                    Tick(state, now);
                    lock (this.gate)
                    {
                        this.lastTickFailure.Remove(state.Light.Id);
                    }
                }
                catch (Exception ex)
                {
                    if (ShouldNotifyTickFailure(state.Light.Id, now))
                    {
                        this.onTickFailure?.Invoke(ex);
                    }
                }
            }

            try
            {
                await Task.Delay(80, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick(EffectState state, DateTime now)
    {
        switch (state.Kind)
        {
            case LifxEffectKind.None:
                return;
            case LifxEffectKind.Chase:
                if (now - state.LastSent < TimeSpan.FromMilliseconds(state.SpeedMs * 4))
                {
                    return;
                }

                (double cr, double cg, double cb) = ChaseColors[PositiveMod(state.Step, ChaseColors.Length)];
                this.client.SetRgb(state.Light, cr, cg, cb, LifxConstants.DefaultKelvin, 0, state.Brightness);
                state.Step++;
                state.LastSent = now;
                break;
            case LifxEffectKind.Sinewave:
                state.Phase = WrapPhase(state.Phase + (2 * Math.PI / state.SpeedMs));
                if (now - state.LastSent < TimeSpan.FromMilliseconds(state.SpeedMs))
                {
                    return;
                }

                (double sr, double sg, double sb) = SinewaveRgb(state.Phase);
                this.client.SetRgb(state.Light, sr, sg, sb, LifxConstants.DefaultKelvin, state.SpeedMs, state.Brightness);
                state.LastSent = now;
                break;
            case LifxEffectKind.Rainbow:
                if (now - state.LastSent < TimeSpan.FromMilliseconds(state.SpeedMs))
                {
                    return;
                }

                if (state.Light.ZoneCapable && state.Light.ZoneCount > 1)
                {
                    this.client.SetZones(state.Light, RainbowZones(state.Light.ZoneCount), LifxConstants.DefaultKelvin, state.FadeMs, state.Brightness);
                }
                else
                {
                    LifxColor.HsvToRgb(PositiveMod(state.Step, 360) / 360.0, 1.0, 1.0, out double rr, out double rg, out double rb);
                    this.client.SetRgb(state.Light, rr, rg, rb, LifxConstants.DefaultKelvin, state.FadeMs, state.Brightness);
                    state.Step += 8;
                }

                state.LastSent = now;
                break;
            case LifxEffectKind.PixelChase:
                if (now - state.LastSent < TimeSpan.FromMilliseconds(state.SpeedMs))
                {
                    return;
                }

                int zones = Math.Max(1, state.Light.ZoneCount);
                this.client.SetZones(
                    state.Light,
                    PixelChaseZones(zones, state.Step, 1.0, 1.0, 1.0),
                    LifxConstants.DefaultKelvin,
                    0,
                    state.Brightness);
                state.Step++;
                state.LastSent = now;
                break;
            default:
            {
                LifxEffectKind unused = state.Kind;
                throw new InvalidOperationException($"Unhandled effect {unused}");
            }
        }
    }

    private sealed class EffectState
    {
        public EffectState(LifxLight light, LifxEffectKind kind, int speedMs, double brightness, int fadeMs)
        {
            Light = light;
            Kind = kind;
            SpeedMs = speedMs;
            Brightness = brightness;
            FadeMs = fadeMs;
        }

        public LifxLight Light { get; }

        public LifxEffectKind Kind { get; }

        public int SpeedMs { get; }

        public double Brightness { get; }

        public int FadeMs { get; }

        public int Step { get; set; }

        public double Phase { get; set; }

        public DateTime LastSent { get; set; } = DateTime.MinValue;
    }

    private bool ShouldNotifyTickFailure(string lightId, DateTime now)
    {
        lock (this.gate)
        {
            if (this.lastTickFailure.TryGetValue(lightId, out DateTime last) && now - last < TimeSpan.FromSeconds(5))
            {
                return false;
            }

            this.lastTickFailure[lightId] = now;
            return true;
        }
    }

    private static int PositiveMod(int value, int modulus)
    {
        int remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static double WrapPhase(double phase)
    {
        double cycle = 2 * Math.PI;
        phase %= cycle;
        return phase < 0 ? phase + cycle : phase;
    }
}
