using DMXCore.PluginSdk;

namespace DMXCore100.LIFX;

/// <summary>
/// Channel layout of one single-zone LIFX color protocol: RGB, optionally a
/// White channel (mixed additively into the color), optionally a
/// ColorTemperature channel (kelvin of the white portion), each 8- or 16-bit.
/// In 16-bit layouts every function takes two adjacent channels, coarse then
/// fine, in the same order as the 8-bit layout.
/// </summary>
internal sealed record LifxColorMode(string ProtocolId, string Personality, bool HasWhite, bool HasColorTemperature, bool SixteenBit)
{
    public static readonly LifxColorMode Rgb = new(LifxPlugin.ColorProtocolId, "RGB", false, false, false);
    public static readonly LifxColorMode RgbCt = new(LifxPlugin.ColorCtProtocolId, "RGB+CT", false, true, false);
    public static readonly LifxColorMode Rgbw = new(LifxPlugin.ColorRgbwProtocolId, "RGBW", true, false, false);
    public static readonly LifxColorMode RgbwCt = new(LifxPlugin.ColorRgbwCtProtocolId, "RGBW+CT", true, true, false);
    public static readonly LifxColorMode Rgb16 = new(LifxPlugin.Color16ProtocolId, "RGB 16-bit", false, false, true);
    public static readonly LifxColorMode RgbCt16 = new(LifxPlugin.ColorCt16ProtocolId, "RGB+CT 16-bit", false, true, true);
    public static readonly LifxColorMode Rgbw16 = new(LifxPlugin.ColorRgbw16ProtocolId, "RGBW 16-bit", true, false, true);
    public static readonly LifxColorMode RgbwCt16 = new(LifxPlugin.ColorRgbwCt16ProtocolId, "RGBW+CT 16-bit", true, true, true);

    /// <summary>
    /// Every mode, in the order the protocols are registered and the profile
    /// personalities are listed.
    /// </summary>
    public static readonly IReadOnlyList<LifxColorMode> All =
    [
        Rgb,
        RgbCt,
        Rgbw,
        RgbwCt,
        Rgb16,
        RgbCt16,
        Rgbw16,
        RgbwCt16,
    ];

    /// <summary>
    /// Bytes per function: 1 (8-bit) or 2 (16-bit, coarse then fine).
    /// </summary>
    public int BytesPerFunction => this.SixteenBit ? 2 : 1;

    public int FunctionCount => 3 + (this.HasWhite ? 1 : 0) + (this.HasColorTemperature ? 1 : 0);

    public int ChannelCount => this.FunctionCount * this.BytesPerFunction;

    /// <summary>
    /// The mode's DMX personality, function by function; each 16-bit function
    /// contributes its coarse function followed by its Fine counterpart.
    /// </summary>
    public IReadOnlyList<PluginFixtureFunction> ProfileChannels()
    {
        var channels = new List<PluginFixtureFunction>(this.ChannelCount);
        Add(PluginFixtureFunction.Red, PluginFixtureFunction.RedFine);
        Add(PluginFixtureFunction.Green, PluginFixtureFunction.GreenFine);
        Add(PluginFixtureFunction.Blue, PluginFixtureFunction.BlueFine);
        if (this.HasWhite)
        {
            Add(PluginFixtureFunction.White, PluginFixtureFunction.WhiteFine);
        }

        if (this.HasColorTemperature)
        {
            Add(PluginFixtureFunction.ColorTemperature, PluginFixtureFunction.ColorTemperatureFine);
        }

        return channels;

        void Add(PluginFixtureFunction coarse, PluginFixtureFunction fine)
        {
            channels.Add(coarse);
            if (this.SixteenBit)
            {
                channels.Add(fine);
            }
        }
    }

    /// <summary>
    /// Read function <paramref name="index"/> from a channel slice as 0..1,
    /// honoring the mode's byte width. Channels past the end read as 0.
    /// </summary>
    public double Read(ReadOnlySpan<byte> channels, int index)
    {
        int offset = index * this.BytesPerFunction;
        if (this.SixteenBit)
        {
            int coarse = offset < channels.Length ? channels[offset] : 0;
            int fine = offset + 1 < channels.Length ? channels[offset + 1] : 0;
            return ((coarse << 8) | fine) / 65535.0;
        }

        return (offset < channels.Length ? channels[offset] : 0) / 255.0;
    }

    /// <summary>
    /// Convert one channel slice to the HSBK LIFX SetColor wants: additive
    /// RGB+W mixing, kelvin from the CT channel or the LIFX default.
    /// </summary>
    public Hsbk ToHsbk(ReadOnlySpan<byte> channels)
    {
        double red = this.Read(channels, 0);
        double green = this.Read(channels, 1);
        double blue = this.Read(channels, 2);
        int next = 3;
        double white = this.HasWhite ? this.Read(channels, next++) : 0.0;
        int kelvin = this.HasColorTemperature
            ? LifxColor.KelvinFromUnit(this.Read(channels, next))
            : LifxConstants.DefaultKelvin;

        return LifxColor.RgbwToHsbk(red, green, blue, white, kelvin);
    }
}
