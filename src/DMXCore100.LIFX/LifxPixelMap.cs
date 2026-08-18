namespace DMXCore100.LIFX;

/// <summary>
/// Maps the pixels a mapping exposes onto the zone indexes the device
/// actually lights. Some LIFX matrix products report a padded tile whose
/// grid has more entries than physical zones (the LAN docs describe the
/// Candle as a 5×6 tile with 26 zones); the SuperColour Tube reports 5×11 =
/// 55 but has 52 zones. Everywhere else the map is the identity.
/// </summary>
internal static class LifxPixelMap
{
    /// <summary>
    /// SuperColour Tube (products 217/218), as measured on hardware
    /// (2026-08-18): device indexes 0-1 are the two zones on top, 2-4 are
    /// dead, and 5-54 run from the top of the side down to the base.
    /// </summary>
    private static readonly int[] TubeMap = [.. new[] { 0, 1 }, .. Enumerable.Range(5, 50)];

    private const int TubeReportedZones = 55;

    /// <summary>
    /// The number of addressable pixels: the device's zone count minus any
    /// dead indexes.
    /// </summary>
    public static int PixelCount(LifxLight light) =>
        DeviceIndexes(light)?.Length ?? Math.Max(1, light.ZoneCount);

    /// <summary>
    /// Device zone index for each exposed pixel, or null when every reported
    /// zone is a real pixel (identity).
    /// </summary>
    public static int[]? DeviceIndexes(LifxLight light)
    {
        if (LifxProducts.IsSuperColourTube((int)light.Product) && light.ZoneCount == TubeReportedZones)
        {
            return TubeMap;
        }

        return null;
    }

    /// <summary>
    /// Spread pixel colors onto the device's full zone array; dead zones
    /// stay black. With an identity map this is the input padded or
    /// truncated to the zone count.
    /// </summary>
    public static Hsbk[] ToDeviceZones(LifxLight light, IReadOnlyList<Hsbk> pixels)
    {
        var zones = new Hsbk[Math.Max(1, light.ZoneCount)];
        int[]? map = DeviceIndexes(light);
        if (map == null)
        {
            for (int i = 0; i < zones.Length && i < pixels.Count; i++)
            {
                zones[i] = pixels[i];
            }

            return zones;
        }

        for (int i = 0; i < map.Length && i < pixels.Count; i++)
        {
            zones[map[i]] = pixels[i];
        }

        return zones;
    }
}
