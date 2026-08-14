using System.Buffers.Binary;

namespace DMXCore100.LIFX;

/// <summary>
/// LIFX LAN frame builder/parser. Packet layouts match the working DMX2LIFX client.
/// </summary>
public sealed class LifxPackets
{
    private readonly uint source;
    private readonly Func<byte> nextSequence;

    public LifxPackets(uint source, Func<byte> nextSequence)
    {
        this.source = source;
        this.nextSequence = nextSequence;
    }

    public byte[] Header(ushort msgType, byte[]? target = null, bool tagged = false)
    {
        const int addressable = 1;
        const int origin = 0;
        int frameBits =
            (LifxConstants.Protocol & 0x0FFF)
            | (addressable << 12)
            | ((tagged ? 1 : 0) << 13)
            | (origin << 14);

        byte[] packet = new byte[LifxConstants.HeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), LifxConstants.HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), (ushort)frameBits);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), this.source);

        if (target is { Length: 8 })
        {
            target.CopyTo(packet, 8);
        }

        packet[23] = this.nextSequence();
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32, 2), msgType);
        return packet;
    }

    public static byte[] Finalise(byte[] packet)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        return packet;
    }

    public byte[] GetService() => Finalise(Header(LifxConstants.GetService, tagged: true));

    public byte[] GetLabel(byte[] target) => Finalise(Header(LifxConstants.GetLabel, target));

    public byte[] GetVersion(byte[] target) => Finalise(Header(LifxConstants.GetVersion, target));

    public byte[] GetLightState(byte[] target) => Finalise(Header(LifxConstants.GetLightState, target));

    public byte[] GetDeviceChain(byte[] target) => Finalise(Header(LifxConstants.GetDeviceChain, target));

    public byte[] GetExtendedColorZones(byte[] target) => Finalise(Header(LifxConstants.GetExtendedColorZones, target));

    public byte[] GetColorZones(byte[] target)
    {
        byte[] header = Header(LifxConstants.GetColorZones, target);
        byte[] packet = new byte[header.Length + 2];
        header.CopyTo(packet, 0);
        packet[header.Length] = 0;
        packet[header.Length + 1] = 255;
        return Finalise(packet);
    }

    public byte[] SetPower(byte[] target, bool on)
    {
        byte[] header = Header(LifxConstants.SetPower, target);
        byte[] packet = new byte[header.Length + 2];
        header.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header.Length, 2), (ushort)(on ? 65535 : 0));
        return Finalise(packet);
    }

    public byte[] SetColor(byte[] target, Hsbk color, int durationMs)
    {
        byte[] header = Header(LifxConstants.SetColor, target);
        byte[] packet = new byte[header.Length + 13];
        header.CopyTo(packet, 0);
        int o = header.Length;
        packet[o] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 1, 2), color.Hue);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 3, 2), color.Saturation);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 5, 2), color.Brightness);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 7, 2), color.Kelvin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(o + 9, 4), (uint)durationMs);
        return Finalise(packet);
    }

    public IReadOnlyList<byte[]> ZonePackets(LifxLight light, IReadOnlyList<Hsbk> colors, int durationMs)
    {
        if (light.EffectiveLayout == LifxLayout.Matrix)
        {
            return BuildMatrixPackets(light, colors, durationMs);
        }

        if (LifxProducts.UsesExtendedMultizone((int)light.Product))
        {
            return BuildExtendedMzPackets(light.Target, colors, durationMs);
        }

        return BuildLegacyMzPackets(light.Target, colors, durationMs);
    }

    public IReadOnlyList<byte[]> BuildSet64Packets(byte[] target, IReadOnlyList<Hsbk> colors, int width, int height, int durationMs, int tileIndex = 0)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int xStep = Math.Min(width, LifxConstants.Set64ColorsPerPacket);
        int rowsPerPacket = Math.Max(1, LifxConstants.Set64ColorsPerPacket / xStep);
        var packets = new List<byte[]>();
        for (int y = 0; y < height; y += rowsPerPacket)
        {
            for (int x = 0; x < width; x += xStep)
            {
                int cols = Math.Min(xStep, width - x);
                int rows = Math.Min(rowsPerPacket, height - y);
                var chunk = new List<Hsbk>(rows * cols);
                for (int row = 0; row < rows; row++)
                {
                    int start = ((y + row) * width) + x;
                    if (start >= colors.Count)
                    {
                        break;
                    }

                    int take = Math.Min(cols, colors.Count - start);
                    take = Math.Min(take, LifxConstants.Set64ColorsPerPacket - chunk.Count);
                    for (int i = 0; i < take; i++)
                    {
                        chunk.Add(colors[start + i]);
                    }
                }

                if (chunk.Count == 0)
                {
                    continue;
                }

                byte[] header = Header(LifxConstants.Set64, target);
                byte[] packet = new byte[header.Length + 10 + (LifxConstants.Set64ColorsPerPacket * 8)];
                header.CopyTo(packet, 0);
                int o = header.Length;
                packet[o] = (byte)tileIndex;
                packet[o + 1] = 1;
                packet[o + 2] = 0;
                packet[o + 3] = (byte)x;
                packet[o + 4] = (byte)y;
                packet[o + 5] = (byte)cols;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(o + 6, 4), (uint)durationMs);
                PackHsbk(packet.AsSpan(o + 10), chunk, LifxConstants.Set64ColorsPerPacket);
                packets.Add(Finalise(packet));
            }
        }

        return packets;
    }

    public IReadOnlyList<byte[]> BuildExtendedMzPackets(byte[] target, IReadOnlyList<Hsbk> colors, int durationMs)
    {
        var packets = new List<byte[]>();
        int total = colors.Count;
        for (int index = 0; index < total; index += LifxConstants.ExtendedMzColorsPerPacket)
        {
            Hsbk[] chunk = colors.Skip(index).Take(LifxConstants.ExtendedMzColorsPerPacket).ToArray();
            byte apply = index + LifxConstants.ExtendedMzColorsPerPacket >= total
                ? LifxConstants.MultiZoneApply
                : LifxConstants.MultiZoneNoApply;
            byte[] header = Header(LifxConstants.SetExtendedColorZones, target);
            byte[] packet = new byte[header.Length + 8 + (LifxConstants.ExtendedMzColorsPerPacket * 8)];
            header.CopyTo(packet, 0);
            int o = header.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(o, 4), (uint)durationMs);
            packet[o + 4] = apply;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 5, 2), (ushort)index);
            packet[o + 7] = (byte)chunk.Length;
            PackHsbk(packet.AsSpan(o + 8), chunk, LifxConstants.ExtendedMzColorsPerPacket);
            packets.Add(Finalise(packet));
        }

        return packets;
    }

    public IReadOnlyList<byte[]> BuildLegacyMzPackets(byte[] target, IReadOnlyList<Hsbk> colors, int durationMs)
    {
        if (colors.Count == 0)
        {
            return [];
        }

        var runs = new List<(int Start, int End, Hsbk Color)>();
        int start = 0;
        Hsbk current = colors[0];
        for (int i = 1; i < colors.Count; i++)
        {
            if (colors[i] != current)
            {
                runs.Add((start, i - 1, current));
                start = i;
                current = colors[i];
            }
        }

        runs.Add((start, colors.Count - 1, current));

        var packets = new List<byte[]>();
        for (int runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            (int runStart, int runEnd, Hsbk color) = runs[runIndex];
            byte apply = runIndex == runs.Count - 1 ? LifxConstants.MultiZoneApply : LifxConstants.MultiZoneNoApply;
            byte[] header = Header(LifxConstants.SetColorZones, target);
            byte[] packet = new byte[header.Length + 15];
            header.CopyTo(packet, 0);
            int o = header.Length;
            packet[o] = (byte)(runStart & 0xFF);
            packet[o + 1] = (byte)(runEnd & 0xFF);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 2, 2), color.Hue);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 4, 2), color.Saturation);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 6, 2), color.Brightness);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(o + 8, 2), color.Kelvin);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(o + 10, 4), (uint)durationMs);
            packet[o + 14] = apply;
            packets.Add(Finalise(packet));
        }

        return packets;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out uint source, out byte[] target, out ushort msgType)
    {
        source = 0;
        target = [];
        msgType = 0;
        if (data.Length < LifxConstants.HeaderSize)
        {
            return false;
        }

        source = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        target = data.Slice(8, 8).ToArray();
        msgType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(32, 2));
        return true;
    }

    public static void ParseStateDeviceChain(LifxLight light, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> payload = data[LifxConstants.HeaderSize..];
        int countOffset = 1 + (16 * LifxConstants.TileDeviceSize);
        if (payload.Length <= countOffset)
        {
            return;
        }

        int tileCount = payload[countOffset];
        if (tileCount < 1)
        {
            return;
        }

        ReadOnlySpan<byte> first = payload.Slice(1, LifxConstants.TileDeviceSize);
        int width = first[16];
        int height = first[17];
        if (width < 1 || height < 1)
        {
            return;
        }

        light.Layout = LifxLayout.Matrix;
        light.MatrixWidth = width;
        light.MatrixHeight = height;
        light.TileCount = tileCount;
        light.ZoneCount = width * height * tileCount;
    }

    public static void ParseLinearZoneCount(LifxLight light, ushort msgType, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> payload = data[LifxConstants.HeaderSize..];
        int zoneCount = 0;
        switch (msgType)
        {
            case LifxConstants.StateExtendedColorZones:
                if (payload.Length >= 2)
                {
                    zoneCount = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                }

                break;
            case LifxConstants.StateMultizone:
            case LifxConstants.StateZone:
                if (payload.Length >= 1)
                {
                    zoneCount = payload[0];
                }

                break;
            default:
                return;
        }

        if (zoneCount < 1)
        {
            return;
        }

        light.Layout = LifxLayout.Linear;
        light.ZoneCount = zoneCount;
    }

    public static ushort ReadMessageType(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 34)
        {
            throw new ArgumentException("LIFX packet is too short to contain a message type.", nameof(packet));
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(32, 2));
    }

    private IReadOnlyList<byte[]> BuildMatrixPackets(LifxLight light, IReadOnlyList<Hsbk> colors, int durationMs)
    {
        var packets = new List<byte[]>();
        int width = Math.Max(1, light.MatrixWidth);
        int height = Math.Max(1, light.MatrixHeight);
        int tileCount = Math.Max(1, light.TileCount);
        bool defaultGeometry = width == 1 && height == 1;
        int perTile;
        int sendWidth;
        int sendHeight;
        if (defaultGeometry)
        {
            perTile = Math.Max(1, light.ZoneCount / tileCount);
            sendWidth = Math.Min(perTile, LifxConstants.Set64ColorsPerPacket);
            sendHeight = Math.Max(1, (perTile + sendWidth - 1) / sendWidth);
        }
        else
        {
            perTile = width * height;
            sendWidth = width;
            sendHeight = height;
        }

        for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
        {
            int start = tileIndex * perTile;
            Hsbk[] chunk = colors.Skip(start).Take(perTile).ToArray();
            packets.AddRange(BuildSet64Packets(light.Target, chunk, sendWidth, sendHeight, durationMs, tileIndex));
        }

        return packets;
    }

    private static void PackHsbk(Span<byte> dest, IReadOnlyList<Hsbk> colors, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 8;
            if (i < colors.Count)
            {
                Hsbk color = colors[i];
                BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(o, 2), color.Hue);
                BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(o + 2, 2), color.Saturation);
                BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(o + 4, 2), color.Brightness);
                BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(o + 6, 2), color.Kelvin);
            }
            else
            {
                dest.Slice(o, 8).Clear();
            }
        }
    }
}
