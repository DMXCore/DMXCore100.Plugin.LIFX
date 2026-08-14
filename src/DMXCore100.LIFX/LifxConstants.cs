namespace DMXCore100.LIFX;

internal static class LifxConstants
{
    public const int Port = 56700;
    public const int Protocol = 1024;
    public const int HeaderSize = 36;
    public const int DefaultKelvin = 3500;
    public const int KelvinMin = 2500;
    public const int KelvinMax = 9000;
    /// <summary>
    /// SetColor duration at <see cref="MaxUpdatesPerSecond"/>: ~1.5× the send
    /// interval so consecutive updates fade into each other over UDP.
    /// </summary>
    public const int StreamDurationMs = 75;
    public const int MaxUpdatesPerSecond = 20;
    public const int DiscoveryTimeoutMs = 2000;

    public const ushort GetService = 2;
    public const ushort StateService = 3;
    public const ushort SetPower = 21;
    public const ushort GetLabel = 23;
    public const ushort StateLabel = 25;
    public const ushort GetVersion = 32;
    public const ushort StateVersion = 33;
    public const ushort SetColor = 102;
    public const ushort SetExtendedColorZones = 510;
    public const ushort GetExtendedColorZones = 511;
    public const ushort StateExtendedColorZones = 512;
    public const ushort GetDeviceChain = 701;
    public const ushort StateDeviceChain = 702;
    public const ushort Set64 = 715;

    public const byte MultiZoneApply = 1;
    public const byte MultiZoneNoApply = 0;
    public const int ExtendedMzColorsPerPacket = 82;
    public const int Set64ColorsPerPacket = 64;
    public const int TileDeviceSize = 55;
}
