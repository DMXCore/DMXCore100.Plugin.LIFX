using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DMXCore100.LIFX;

/// <summary>
/// LIFX LAN UDP client: broadcast discovery, SET_COLOR / SET_POWER, and
/// multizone packets for SuperColour / strip / tile fixtures.
/// </summary>
public sealed class LifxLanClient : ILifxLanClient
{
    private readonly UdpClient udp;
    private readonly LifxPackets packets;
    private readonly Dictionary<string, LifxLight> lights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly CancellationTokenSource listenCts = new();
    private readonly Task listenTask;
    private readonly uint source;
    private byte sequence;
    private bool disposed;

    public LifxLanClient(string bindIp = "0.0.0.0")
    {
        this.source = (uint)Random.Shared.Next(2, int.MaxValue);
        this.sequence = (byte)Random.Shared.Next(0, 256);
        this.packets = new LifxPackets(this.source, NextSequence);

        IPAddress address = string.IsNullOrWhiteSpace(bindIp) || bindIp == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(bindIp);

        this.udp = new UdpClient(new IPEndPoint(address, 0));
        this.udp.EnableBroadcast = true;
        this.udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        this.udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        this.listenTask = ListenAsync(this.listenCts.Token);
    }

    public IReadOnlyList<LifxLight> GetLights()
    {
        lock (this.gate)
        {
            return this.lights.Values.Where(static light => light.IsLight).ToArray();
        }
    }

    public async Task<IReadOnlyList<LifxLight>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Send(this.packets.GetService(), IPAddress.Broadcast);
        await Task.Delay(timeout, cancellationToken);

        LifxLight[] snapshot;
        lock (this.gate)
        {
            snapshot = [.. this.lights.Values];
        }

        foreach (LifxLight light in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(this.packets.GetLabel(light.Target), IPAddress.Parse(light.Ip));
            await Task.Delay(50, cancellationToken);
            Send(this.packets.GetVersion(light.Target), IPAddress.Parse(light.Ip));
            await Task.Delay(50, cancellationToken);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);

        LifxLight[] zoned;
        lock (this.gate)
        {
            zoned = this.lights.Values.Where(static light => light.ZoneCapable).ToArray();
        }

        foreach (LifxLight light in zoned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestZoneGeometry(light);
            await Task.Delay(50, cancellationToken);
        }

        if (zoned.Length > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return GetLights();
    }

    public async Task ProbeAsync(string ip, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Send(this.packets.GetService(), IPAddress.Parse(ip));
        await Task.Delay(timeout, cancellationToken);

        LifxLight? found;
        lock (this.gate)
        {
            found = this.lights.Values.FirstOrDefault(light => light.Ip == ip);
        }

        if (found == null)
        {
            return;
        }

        Send(this.packets.GetLabel(found.Target), IPAddress.Parse(found.Ip));
        await Task.Delay(50, cancellationToken);
        Send(this.packets.GetVersion(found.Target), IPAddress.Parse(found.Ip));
        await Task.Delay(200, cancellationToken);
        if (found.ZoneCapable)
        {
            RequestZoneGeometry(found);
            await Task.Delay(300, cancellationToken);
        }
    }

    public void SetRgb(LifxLight light, double r, double g, double b, int kelvin, int durationMs, double brightness)
    {
        EnsurePowered(light);
        Hsbk color = LifxColor.ScaleBrightness(LifxColor.RgbToHsbk(r, g, b, kelvin, light.CurrentHue), brightness);
        Send(this.packets.SetColor(light.Target, color, durationMs), IPAddress.Parse(light.Ip));
        WriteLocalColor(light, color);
    }

    public void SetZones(LifxLight light, IReadOnlyList<Rgb01> zones, int kelvin, int durationMs, double brightness)
    {
        EnsurePowered(light);
        Hsbk[] colors = zones
            .Select(zone => LifxColor.ScaleBrightness(LifxColor.RgbToHsbk(zone.R, zone.G, zone.B, kelvin, light.CurrentHue), brightness))
            .ToArray();
        foreach (byte[] packet in this.packets.ZonePackets(light, colors, durationMs))
        {
            Send(packet, IPAddress.Parse(light.Ip));
        }

        if (colors.Length > 0)
        {
            WriteLocalColor(light, colors[0]);
        }
    }

    public void SetPower(LifxLight light, bool on)
    {
        Send(this.packets.SetPower(light.Target, on), IPAddress.Parse(light.Ip));
        light.Power = on ? 65535 : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        await this.listenCts.CancelAsync();
        this.udp.Dispose();
        try
        {
            await this.listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        this.listenCts.Dispose();
    }

    private void EnsurePowered(LifxLight light)
    {
        if (light.Power != 0)
        {
            return;
        }

        SetPower(light, true);
    }

    private void WriteLocalColor(LifxLight light, Hsbk color)
    {
        light.CurrentHue = color.Hue;
        light.CurrentSaturation = color.Saturation;
        light.CurrentBrightness = color.Brightness;
        light.CurrentKelvin = color.Kelvin;
        light.CurrentRgb = LifxColor.HsbkToRgb8(color);
    }

    private void RequestZoneGeometry(LifxLight light)
    {
        byte[] packet = light.EffectiveLayout switch
        {
            LifxLayout.Matrix => this.packets.GetDeviceChain(light.Target),
            LifxLayout.Linear when LifxProducts.UsesExtendedMultizone((int)light.Product)
                => this.packets.GetExtendedColorZones(light.Target),
            LifxLayout.Linear => this.packets.GetColorZones(light.Target),
            LifxLayout.Single => Array.Empty<byte>(),
            _ => throw Unexpected(light.EffectiveLayout),
        };

        if (packet.Length > 0)
        {
            Send(packet, IPAddress.Parse(light.Ip));
        }
    }

    private void Send(byte[] packet, IPAddress ip)
    {
        this.udp.Send(packet, packet.Length, new IPEndPoint(ip, LifxConstants.Port));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await this.udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            HandlePacket(result.Buffer, result.RemoteEndPoint.Address.ToString());
        }
    }

    private void HandlePacket(byte[] data, string ip)
    {
        if (!LifxPackets.TryReadHeader(data, out uint packetSource, out byte[] target, out ushort msgType))
        {
            return;
        }

        if (packetSource != this.source)
        {
            return;
        }

        string id = Convert.ToHexString(target).ToLowerInvariant();
        lock (this.gate)
        {
            if (!this.lights.TryGetValue(id, out LifxLight? light))
            {
                light = new LifxLight(target, ip);
                this.lights[id] = light;
            }

            light.Ip = ip;
            ApplyMessage(light, msgType, data);
            if (!light.IsLight || LifxProducts.IsSwitch((int)light.Product, light.ModelName))
            {
                this.lights.Remove(id);
            }
        }
    }

    private static void ApplyMessage(LifxLight light, ushort msgType, byte[] data)
    {
        switch (msgType)
        {
            case LifxConstants.StateService:
                break;
            case LifxConstants.StateLabel when data.Length >= LifxConstants.HeaderSize:
                light.Label = Encoding.UTF8.GetString(data.AsSpan(LifxConstants.HeaderSize)).TrimEnd('\0');
                break;
            case LifxConstants.StatePower when data.Length >= LifxConstants.HeaderSize + 2:
                light.Power = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LifxConstants.HeaderSize, 2));
                break;
            case LifxConstants.StateVersion when data.Length >= LifxConstants.HeaderSize + 12:
                light.Vendor = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(LifxConstants.HeaderSize, 4));
                light.Product = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(LifxConstants.HeaderSize + 4, 4));
                if (light.Layout is not (LifxLayout.Linear or LifxLayout.Matrix))
                {
                    light.Layout = LifxProducts.Layout((int)light.Product);
                }

                light.ModelName = LifxProducts.ModelName((int)light.Vendor, (int)light.Product);
                light.IsLight = !LifxProducts.IsSwitch((int)light.Product, light.ModelName);
                break;
            case LifxConstants.StateLight when data.Length >= 45:
                light.CurrentHue = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(37, 2));
                light.CurrentSaturation = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(39, 2));
                light.CurrentBrightness = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(41, 2));
                light.CurrentKelvin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(43, 2));
                light.CurrentRgb = LifxColor.HsbkToRgb8(new Hsbk(
                    light.CurrentHue, light.CurrentSaturation, light.CurrentBrightness, light.CurrentKelvin));
                break;
            case LifxConstants.StateDeviceChain:
                LifxPackets.ParseStateDeviceChain(light, data);
                break;
            case LifxConstants.StateExtendedColorZones:
            case LifxConstants.StateMultizone:
            case LifxConstants.StateZone:
                LifxPackets.ParseLinearZoneCount(light, msgType, data);
                break;
            default:
                break;
        }
    }

    private byte NextSequence()
    {
        this.sequence++;
        return this.sequence;
    }

    private static InvalidOperationException Unexpected(LifxLayout layout)
    {
        throw new InvalidOperationException($"Unhandled layout {layout}");
    }
}
