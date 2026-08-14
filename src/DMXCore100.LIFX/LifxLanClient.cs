using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DMXCore100.LIFX;

/// <summary>
/// LIFX LAN UDP client: broadcast discovery, SET_COLOR / SET_POWER, and
/// multizone packets for SuperColour / strip / tile fixtures. A
/// non-recoverable listen <see cref="SocketException"/> recreates the
/// socket in-process with backoff — the host cannot unload plugin
/// assemblies, so recovery must not require a plugin restart.
/// </summary>
public sealed class LifxLanClient : ILifxLanClient
{
    private const int ListenBackoffMs = 200;
    private const int ListenBackoffMaxMs = 5000;

    private readonly IPAddress bindAddress;
    private readonly LifxPackets packets;
    private readonly Dictionary<string, LifxLight> lights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly CancellationTokenSource listenCts = new();
    private readonly Task listenTask;
    private readonly uint source;
    private readonly Lock sendGate = new();
    private readonly Action<Exception>? onListenFailure;
    private readonly Action? onListenRecovered;
    private UdpClient udp;
    private int sequence;
    private int disposed;
    private int listenFailed;

    public LifxLanClient(
        string bindIp = "0.0.0.0",
        Action<Exception>? onListenFailure = null,
        Action? onListenRecovered = null)
    {
        this.source = (uint)Random.Shared.Next(2, int.MaxValue);
        this.sequence = Random.Shared.Next(0, 256);
        this.packets = new LifxPackets(this.source, NextSequence);
        this.onListenFailure = onListenFailure;
        this.onListenRecovered = onListenRecovered;
        this.bindAddress = string.IsNullOrWhiteSpace(bindIp) || bindIp == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(bindIp);
        this.udp = CreateUdp();
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
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.listenCts.CancelAsync();
        lock (this.sendGate)
        {
            this.udp.Dispose();
        }

        try
        {
            await this.listenTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        lock (this.sendGate)
        {
            this.udp.Send(packet, packet.Length, new IPEndPoint(ip, LifxConstants.Port));
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        int backoffMs = ListenBackoffMs;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpClient socket;
            lock (this.sendGate)
            {
                socket = this.udp;
            }

            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                if (Volatile.Read(ref this.disposed) != 0 || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }
            catch (SocketException ex) when (IsRecoverable(ex.SocketErrorCode))
            {
                try
                {
                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }
            catch (SocketException ex)
            {
                Interlocked.Exchange(ref this.listenFailed, 1);
                NotifyListenFailure(ex);
                if (!TryRecreateSocket())
                {
                    break;
                }

                try
                {
                    await Task.Delay(backoffMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffMs = Math.Min(backoffMs * 2, ListenBackoffMaxMs);
                continue;
            }

            backoffMs = ListenBackoffMs;
            if (Interlocked.Exchange(ref this.listenFailed, 0) != 0)
            {
                NotifyListenRecovered();
            }

            HandlePacket(result.Buffer, result.RemoteEndPoint.Address.ToString());
        }
    }

    private UdpClient CreateUdp()
    {
        var endpoint = new IPEndPoint(this.bindAddress, 0);
        var client = new UdpClient(endpoint.AddressFamily);
        client.EnableBroadcast = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(endpoint);
        return client;
    }

    private bool TryRecreateSocket()
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return false;
        }

        try
        {
            UdpClient replacement = CreateUdp();
            lock (this.sendGate)
            {
                if (Volatile.Read(ref this.disposed) != 0)
                {
                    replacement.Dispose();
                    return false;
                }

                UdpClient previous = this.udp;
                this.udp = replacement;
                previous.Dispose();
            }

            return true;
        }
        catch (Exception ex)
        {
            NotifyListenFailure(ex);
            return Volatile.Read(ref this.disposed) == 0;
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
                int labelLength = Math.Min(32, data.Length - LifxConstants.HeaderSize);
                light.Label = Encoding.UTF8.GetString(data.AsSpan(LifxConstants.HeaderSize, labelLength)).TrimEnd('\0');
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
            case LifxConstants.StateLight when data.Length >= LifxConstants.HeaderSize + 8:
                light.CurrentHue = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LifxConstants.HeaderSize, 2));
                light.CurrentSaturation = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LifxConstants.HeaderSize + 2, 2));
                light.CurrentBrightness = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LifxConstants.HeaderSize + 4, 2));
                light.CurrentKelvin = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LifxConstants.HeaderSize + 6, 2));
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

    private void NotifyListenFailure(Exception ex)
    {
        try
        {
            this.onListenFailure?.Invoke(ex);
        }
        catch
        {
        }
    }

    private void NotifyListenRecovered()
    {
        try
        {
            this.onListenRecovered?.Invoke();
        }
        catch
        {
        }
    }

    private byte NextSequence() => (byte)Interlocked.Increment(ref this.sequence);

    private static bool IsRecoverable(SocketError error) => error is
        SocketError.ConnectionReset
        or SocketError.ConnectionAborted
        or SocketError.TimedOut
        or SocketError.Interrupted
        or SocketError.WouldBlock
        or SocketError.TryAgain
        or SocketError.NetworkReset
        or SocketError.MessageSize;

    private static InvalidOperationException Unexpected(LifxLayout layout) =>
        new($"Unhandled layout {layout}");
}
