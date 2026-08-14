namespace DMXCore100.LIFX;

public abstract record LifxCommand
{
    public sealed record Discover : LifxCommand;

    public sealed record ListLights : LifxCommand;

    public sealed record Color(string Target, int R, int G, int B, double Brightness, int? FadeMs) : LifxCommand;

    public sealed record Power(string Target, bool On) : LifxCommand;

    public sealed record Effect(string Target, LifxEffectKind Kind, int? SpeedMs) : LifxCommand;
}
