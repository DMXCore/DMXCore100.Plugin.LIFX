namespace DMXCore100.LIFX;

public sealed class LifxLight
{
    public LifxLight(byte[] target, string ip, string label = "")
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length != 8)
        {
            throw new ArgumentException("LIFX target must be 8 bytes.", nameof(target));
        }

        this.target = [.. target];
        Ip = ip;
        Label = label;
    }

    private readonly byte[] target;

    public byte[] Target => [.. this.target];

    public string Id => Convert.ToHexString(this.target).ToLowerInvariant();

    public string Ip { get; set; }

    public string Label { get; set; }

    public int Power { get; set; }

    public uint Vendor { get; set; }

    public uint Product { get; set; }

    public string ModelName { get; set; } = "Discovering...";

    public bool IsLight { get; set; } = true;

    public LifxLayout Layout { get; set; } = LifxLayout.Single;

    public int ZoneCount { get; set; } = 1;

    public int MatrixWidth { get; set; } = 1;

    public int MatrixHeight { get; set; } = 1;

    public int TileCount { get; set; } = 1;

    public ushort CurrentHue { get; set; }

    public ushort CurrentSaturation { get; set; }

    public ushort CurrentBrightness { get; set; }

    public ushort CurrentKelvin { get; set; } = LifxConstants.DefaultKelvin;

    public (int R, int G, int B) CurrentRgb { get; set; }

    public LifxLayout EffectiveLayout =>
        Layout is LifxLayout.Linear or LifxLayout.Matrix
            ? Layout
            : LifxProducts.Layout((int)Product);

    public bool ZoneCapable => EffectiveLayout is LifxLayout.Linear or LifxLayout.Matrix;

    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label : $"Light {Ip}";
}
