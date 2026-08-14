namespace DMXCore100.LIFX;

public abstract record LifxLook
{
    public sealed record Color(int R, int G, int B, double Brightness) : LifxLook;

    public sealed record Power(bool On) : LifxLook;

    public sealed record Effect(LifxEffectKind Kind) : LifxLook;

    public sealed record Identify : LifxLook;
}
