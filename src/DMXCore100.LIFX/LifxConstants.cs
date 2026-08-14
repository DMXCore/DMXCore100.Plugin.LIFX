namespace DMXCore100.LIFX;

internal static class LifxConstants
{
    public const int Port = 56700;
    public const int Protocol = 1024;
    public const int HeaderSize = 36;
    public const int DefaultKelvin = 3500;
    public const int KelvinMin = 2500;
    public const int KelvinMax = 9000;
    public const int DefaultFadeMs = 45;
    public const int DefaultEffectSpeedMs = 400;

    public const ushort GetService = 2;
    public const ushort StateService = 3;
    public const ushort GetPower = 20;
    public const ushort SetPower = 21;
    public const ushort StatePower = 22;
    public const ushort GetLabel = 23;
    public const ushort StateLabel = 25;
    public const ushort GetVersion = 32;
    public const ushort StateVersion = 33;
    public const ushort GetLightState = 101;
    public const ushort SetColor = 102;
    public const ushort StateLight = 107;
    public const ushort SetColorZones = 501;
    public const ushort GetColorZones = 502;
    public const ushort StateZone = 503;
    public const ushort StateMultizone = 506;
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
