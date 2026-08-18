using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DMXCore100.LIFX;

internal delegate Task<IReadOnlyList<LifxLight>> LifxDiscoverFunc(bool refresh, CancellationToken cancellationToken);

internal delegate ValueTask LifxDatagramSender(
    IPEndPoint endpoint,
    ReadOnlyMemory<byte> packet,
    CancellationToken cancellationToken);

/// <summary>
/// One-shot LIFX LAN discovery: broadcast GetService and collect StateService
/// replies, then fetch label/version so the Outputs Discover list can show
/// a name and product. Zone-capable devices (SuperColour, Beam, strips,
/// tiles) are then queried for pixel geometry. Destination value is the
/// device IP.
/// </summary>
internal sealed class LifxDiscovery
{
    private readonly LifxDiscoverFunc? discoverOverride;
    private readonly object gate = new();
    private IReadOnlyList<LifxLight>? cached;
    private Task<IReadOnlyList<LifxLight>>? inFlight;

    public LifxDiscovery(LifxDiscoverFunc? discoverOverride = null)
    {
        this.discoverOverride = discoverOverride;
    }

    /// <summary>
    /// Invoked whenever a scan completes with at least one light, so the
    /// plugin can persist the snapshot. The scan does not complete until
    /// this callback returns, which lets callers await persistence.
    /// </summary>
    public Func<IReadOnlyList<LifxLight>, Task>? ScanCompleted { get; set; }

    /// <summary>
    /// Pre-populate the cache from persisted state. Ignored once a live scan
    /// has run; a live snapshot always wins over a restored one.
    /// </summary>
    public void Seed(IReadOnlyList<LifxLight> lights)
    {
        if (lights.Count == 0)
        {
            return;
        }

        lock (this.gate)
        {
            this.cached ??= lights;
        }
    }

    public async Task<IReadOnlyList<LifxLight>> GetLightsAsync(bool refresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IReadOnlyList<LifxLight>>? owner = null;
        Task<IReadOnlyList<LifxLight>> pending;
        lock (this.gate)
        {
            if (!refresh && this.cached != null)
            {
                return this.cached;
            }

            if (this.inFlight != null)
            {
                pending = this.inFlight;
            }
            else
            {
                owner = new TaskCompletionSource<IReadOnlyList<LifxLight>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                this.inFlight = owner.Task;
                pending = owner.Task;
            }
        }

        if (owner != null)
        {
            _ = this.RunScanAsync(owner, refresh);
        }

        return await pending.WaitAsync(cancellationToken);
    }

    private async Task RunScanAsync(
        TaskCompletionSource<IReadOnlyList<LifxLight>> owner,
        bool refresh)
    {
        using var lifetime = new CancellationTokenSource();
        try
        {
            IReadOnlyList<LifxLight> lights = this.discoverOverride != null
                ? await this.discoverOverride(refresh, lifetime.Token)
                : await BroadcastGetServiceAsync(
                    TimeSpan.FromMilliseconds(LifxConstants.DiscoveryTimeoutMs),
                    lifetime.Token);
            lights = lights
                .Where(static light => light.IsLight && !LifxProducts.IsSwitch((int)light.Product, light.ModelName))
                .ToArray();
            lights = Merge(this.cached, lights);
            this.cached = lights;
            if (lights.Count > 0 && ScanCompleted is { } onCompleted)
            {
                try
                {
                    await onCompleted(lights);
                }
                catch
                {
                }
            }

            owner.TrySetResult(lights);
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
        finally
        {
            lock (this.gate)
            {
                this.inFlight = null;
            }
        }
    }

    /// <summary>
    /// Fold a scan into the previous snapshot. UDP discovery is lossy - a
    /// bulb that is busy being streamed to often misses one GetService or
    /// StateVersion - so a scan must never make a known device disappear or
    /// forget its product/geometry: devices not seen this time are kept, and
    /// a seen device missing product or geometry inherits them from its
    /// previous entry (same target). A cached device whose IP was taken by a
    /// different target is dropped (DHCP moved the address).
    /// </summary>
    internal static IReadOnlyList<LifxLight> Merge(IReadOnlyList<LifxLight>? previous, IReadOnlyList<LifxLight> scanned)
    {
        if (previous == null || previous.Count == 0)
        {
            return scanned;
        }

        var byTarget = previous.ToDictionary(static light => Key(light), StringComparer.Ordinal);
        var merged = new List<LifxLight>(previous.Count + scanned.Count);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LifxLight light in scanned)
        {
            if (byTarget.TryGetValue(Key(light), out LifxLight? known))
            {
                Backfill(light, known);
            }

            merged.Add(light);
            seenTargets.Add(Key(light));
            seenIps.Add(light.Ip);
        }

        foreach (LifxLight known in previous)
        {
            if (!seenTargets.Contains(Key(known)) && !seenIps.Contains(known.Ip))
            {
                merged.Add(known);
            }
        }

        return merged;

        static string Key(LifxLight light) => Convert.ToHexString(light.Target);
    }

    private static void Backfill(LifxLight light, LifxLight known)
    {
        if (string.IsNullOrWhiteSpace(light.Label))
        {
            light.Label = known.Label;
        }

        if (light.Product == 0 && known.Product != 0)
        {
            light.Vendor = known.Vendor;
            light.Product = known.Product;
            light.ModelName = known.ModelName;
            light.IsLight = known.IsLight;
        }

        bool hasGeometry = light.Layout is LifxLayout.Linear or LifxLayout.Matrix && light.ZoneCount > 1;
        bool knownGeometry = known.Layout is LifxLayout.Linear or LifxLayout.Matrix && known.ZoneCount > 1;
        if (!hasGeometry && knownGeometry)
        {
            light.Layout = known.Layout;
            light.ZoneCount = known.ZoneCount;
            light.MatrixWidth = known.MatrixWidth;
            light.MatrixHeight = known.MatrixHeight;
            light.TileCount = known.TileCount;
        }
    }

    public LifxLight? LightFor(string ip)
    {
        return this.cached?.FirstOrDefault(light =>
            string.Equals(light.Ip, ip, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] TargetFor(string ip) => LightFor(ip)?.Target ?? new byte[8];

    internal static async Task<IReadOnlyList<LifxLight>> BroadcastGetServiceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        uint source = (uint)Random.Shared.Next(2, int.MaxValue);
        int sequence = Random.Shared.Next(0, 256);
        var packets = new LifxPackets(source, () => (byte)Interlocked.Increment(ref sequence));
        var lights = new Dictionary<string, LifxLight>(StringComparer.OrdinalIgnoreCase);
        using var udp = CreateUdp();
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task listen = ListenAsync(udp, source, lights, listenCts.Token);

        try
        {
            // Everything below is best-effort UDP to devices that drop
            // requests when busy (a bulb being streamed to at 20 pps often
            // ignores the first GetService or StateVersion), so each stage
            // re-asks the devices that have not answered yet
            byte[] getService = packets.GetService();
            IReadOnlyList<IPAddress> broadcasts = DiscoveryBroadcastAddresses();
            TimeSpan half = timeout / 2;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                foreach (IPAddress broadcast in broadcasts)
                {
                    Send(udp, getService, broadcast);
                }

                await Task.Delay(half, cancellationToken);
            }

            for (int attempt = 0; attempt < LifxConstants.DiscoveryQueryAttempts; attempt++)
            {
                LifxLight[] pending;
                lock (lights)
                {
                    pending = lights.Values
                        .Where(static light => light.Product == 0 || string.IsNullOrWhiteSpace(light.Label))
                        .ToArray();
                }

                if (pending.Length == 0)
                {
                    break;
                }

                foreach (LifxLight light in pending)
                {
                    IPAddress ip = IPAddress.Parse(light.Ip);
                    if (string.IsNullOrWhiteSpace(light.Label))
                    {
                        Send(udp, packets.GetLabel(light.Target), ip);
                    }

                    if (light.Product == 0)
                    {
                        Send(udp, packets.GetVersion(light.Target), ip);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }

            for (int attempt = 0; attempt < LifxConstants.DiscoveryQueryAttempts; attempt++)
            {
                LifxLight[] pending;
                lock (lights)
                {
                    pending = lights.Values
                        .Where(static light => light.ZoneCapable && light.ZoneCount <= 1)
                        .ToArray();
                }

                if (pending.Length == 0)
                {
                    break;
                }

                foreach (LifxLight light in pending)
                {
                    byte[]? request = packets.GeometryRequest(light);
                    if (request != null)
                    {
                        Send(udp, request, IPAddress.Parse(light.Ip));
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await StopListen(listenCts, listen);
            throw;
        }

        await StopListen(listenCts, listen);

        lock (lights)
        {
            return lights.Values
                .Where(static light => light.IsLight && !LifxProducts.IsSwitch((int)light.Product, light.ModelName))
                .ToArray();
        }
    }

    private static async Task ListenAsync(
        UdpClient udp,
        uint source,
        Dictionary<string, LifxLight> lights,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
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
                break;
            }

            HandlePacket(result.Buffer, result.RemoteEndPoint.Address.ToString(), source, lights);
        }
    }

    private static void HandlePacket(
        byte[] data,
        string ip,
        uint source,
        Dictionary<string, LifxLight> lights)
    {
        if (!LifxPackets.TryReadHeader(data, out uint packetSource, out byte[] target, out ushort msgType))
        {
            return;
        }

        if (packetSource != source)
        {
            return;
        }

        string id = Convert.ToHexString(target).ToLowerInvariant();
        lock (lights)
        {
            if (!lights.TryGetValue(id, out LifxLight? light))
            {
                light = new LifxLight(target, ip);
                lights[id] = light;
            }

            light.Ip = ip;
            ApplyMessage(light, msgType, data);
            if (!light.IsLight || LifxProducts.IsSwitch((int)light.Product, light.ModelName))
            {
                lights.Remove(id);
            }
        }
    }

    internal static void ApplyMessage(LifxLight light, ushort msgType, byte[] data)
    {
        switch (msgType)
        {
            case LifxConstants.StateService:
                break;
            case LifxConstants.StateLabel when data.Length > LifxConstants.HeaderSize:
                int labelLength = Math.Min(32, data.Length - LifxConstants.HeaderSize);
                string label = Encoding.UTF8.GetString(data.AsSpan(LifxConstants.HeaderSize, labelLength)).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(label))
                {
                    light.Label = label;
                }

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
            case LifxConstants.StateDeviceChain:
                LifxPackets.ParseStateDeviceChain(light, data);
                break;
            case LifxConstants.StateExtendedColorZones:
                LifxPackets.ParseLinearZoneCount(light, msgType, data);
                break;
            default:
                break;
        }
    }

    internal static string DestinationLabel(LifxLight light)
    {
        string name = string.IsNullOrWhiteSpace(light.Label) ? "LIFX" : light.Label;
        string product = string.IsNullOrWhiteSpace(light.ModelName) ? "LIFX" : light.ModelName;
        if (light.ZoneCapable && light.ZoneCount > 1)
        {
            return $"{name} ({light.Ip}, {product}, {LifxPixelMap.PixelCount(light)} px)";
        }

        return $"{name} ({light.Ip}, {product})";
    }

    internal static IReadOnlyList<IPAddress> DiscoveryBroadcastAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || unicast.IPv4Mask is not { } mask)
                {
                    continue;
                }

                addresses.Add(DirectedBroadcast(unicast.Address, mask));
            }
        }

        return addresses.Count > 0 ? addresses : [IPAddress.Broadcast];
    }

    internal static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes();
        byte[] netmask = mask.GetAddressBytes();
        if (ip.Length != 4 || netmask.Length != 4)
        {
            throw new ArgumentException("Directed broadcast requires IPv4 address and mask.");
        }

        byte[] broadcast = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ip[i] | (byte)~netmask[i]);
        }

        return new IPAddress(broadcast);
    }

    private static UdpClient CreateUdp()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        return client;
    }

    private static void Send(UdpClient udp, byte[] packet, IPAddress ip)
    {
        udp.Send(packet, packet.Length, new IPEndPoint(ip, LifxConstants.Port));
    }

    private static async Task StopListen(CancellationTokenSource listenCts, Task listen)
    {
        await listenCts.CancelAsync();
        try
        {
            await listen;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
