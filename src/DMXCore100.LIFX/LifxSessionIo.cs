using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.LIFX;

internal sealed class LifxSessionIo : IAsyncDisposable
{
    private readonly UdpClient? udp;
    private readonly IPEndPoint endpoint;
    private readonly ILogger? log;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan powerInterval;
    private TimeSpan nextSummary = TimeSpan.Zero;
    private TimeSpan nextProbe = TimeSpan.Zero;
    private TimeSpan? lastPower;
    private int updatesSinceSummary;
    private Task probe = Task.CompletedTask;

    /// <param name="powerInterval">
    /// How long a power-on stands before the next frame repeats it; defaults to
    /// <see cref="LifxConstants.RefreshIntervalMs"/>.
    /// </param>
    public LifxSessionIo(
        IPEndPoint endpoint,
        LifxDatagramSender? sender,
        ILogger? log = null,
        TimeSpan? powerInterval = null)
    {
        this.endpoint = endpoint;
        this.log = log;
        this.powerInterval = powerInterval ?? TimeSpan.FromMilliseconds(LifxConstants.RefreshIntervalMs);
        uint source = (uint)Random.Shared.Next(2, int.MaxValue);
        int sequence = Random.Shared.Next(0, 256);
        Packets = new LifxPackets(source, () => (byte)Interlocked.Increment(ref sequence));
        if (sender != null)
        {
            Send = sender;
        }
        else
        {
            this.udp = new UdpClient(endpoint.AddressFamily);
            UdpClient socket = this.udp;
            Send = async (ep, packet, ct) =>
            {
                await socket.SendAsync(packet, ep, ct);
            };
        }
    }

    public LifxPackets Packets { get; }

    public LifxDatagramSender Send { get; }

    private bool DebugEnabled => this.log?.IsEnabled(LogLevel.Debug) == true;

    /// <summary>
    /// Send one frame, preceded by a power-on on the session's first frame and
    /// again whenever the last power-on is older than the power interval. The
    /// host re-delivers unchanged values every
    /// <see cref="LifxConstants.RefreshIntervalMs"/> (the protocols' declared
    /// RefreshInterval), which restores a color changed behind our back; the
    /// repeated power-on restores a device switched off the same way.
    /// </summary>
    public async Task SendFrameAsync(
        byte[] target,
        IReadOnlyList<byte[]> packets,
        bool multizone,
        CancellationToken cancellationToken)
    {
        TimeSpan now = this.clock.Elapsed;
        if (this.lastPower is not { } lastPower || now - lastPower >= this.powerInterval)
        {
            await this.Send(this.endpoint, this.Packets.SetPower(target, true), cancellationToken);
            this.lastPower = now;
        }

        foreach (byte[] packet in packets)
        {
            await this.Send(this.endpoint, packet, cancellationToken);
        }

        this.ProbeIfDue(target, multizone);
    }

    /// <summary>
    /// Count one delivered update and, every few seconds, log what is being
    /// streamed. <paramref name="describe"/> only runs when a summary is due.
    /// </summary>
    public void Delivered(Func<string> describe)
    {
        if (!this.DebugEnabled)
        {
            return;
        }

        this.updatesSinceSummary++;
        TimeSpan now = this.clock.Elapsed;
        if (now < this.nextSummary)
        {
            return;
        }

        this.nextSummary = now + TimeSpan.FromMilliseconds(LifxConstants.SendSummaryIntervalMs);
        this.log!.LogDebug(
            "LIFX {Ip}: {Updates} update(s) since the last summary, latest {Latest}",
            this.endpoint.Address,
            this.updatesSinceSummary,
            describe());
        this.updatesSinceSummary = 0;
    }

    /// <summary>
    /// Streamed color messages are fire-and-forget, so every
    /// <see cref="LifxConstants.ProbeIntervalMs"/> ask the device, from this
    /// session's own socket, to acknowledge a SetPower and report its light
    /// (and multizone effect) state. The replies, or their absence, show
    /// whether the stream reaches the device and what it is actually showing.
    /// </summary>
    private void ProbeIfDue(byte[] target, bool multizone)
    {
        if (this.udp == null || !this.DebugEnabled || !this.probe.IsCompleted)
        {
            return;
        }

        TimeSpan now = this.clock.Elapsed;
        if (now < this.nextProbe)
        {
            return;
        }

        this.nextProbe = now + TimeSpan.FromMilliseconds(LifxConstants.ProbeIntervalMs);
        this.probe = Task.Run(() => this.ProbeAsync(this.udp, target, multizone, this.lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        this.udp?.Dispose();
        try
        {
            await this.probe;
        }
        catch (Exception)
        {
        }

        this.lifetime.Dispose();
    }

    private async Task ProbeAsync(UdpClient socket, byte[] target, bool multizone, CancellationToken cancellationToken)
    {
        var requests = new List<byte[]>
        {
            this.Packets.SetPower(target, true, ackRequired: true),
            this.Packets.GetLight(target),
        };

        if (multizone)
        {
            requests.Add(this.Packets.GetMultiZoneEffect(target));
            requests.Add(this.Packets.GetExtendedColorZones(target));
        }

        string local = socket.Client.LocalEndPoint?.ToString() ?? "unbound";
        int replies = 0;
        try
        {
            foreach (byte[] request in requests)
            {
                await socket.SendAsync(request, this.endpoint, cancellationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LifxConstants.ProbeReplyTimeoutMs);
            while (replies < requests.Count)
            {
                UdpReceiveResult result = await socket.ReceiveAsync(timeout.Token);
                if (!LifxPackets.TryReadHeader(result.Buffer, out uint source, out _, out ushort msgType)
                    || source != this.Packets.Source)
                {
                    continue;
                }

                replies++;
                this.log!.LogDebug(
                    "LIFX {Ip} probe reply from {Remote}: {Reply}",
                    this.endpoint.Address,
                    result.RemoteEndPoint,
                    LifxPackets.DescribeReply(msgType, result.Buffer));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (SocketException ex)
        {
            this.log!.LogDebug(
                "LIFX {Ip} probe from {Local} failed: {Message}",
                this.endpoint.Address,
                local,
                ex.Message);
            return;
        }

        if (replies < requests.Count)
        {
            this.log!.LogDebug(
                "LIFX {Ip} probe from {Local}: {Replies} of {Expected} replies within {TimeoutMs} ms (target {Target}); no reply means the datagrams are not reaching the device or its replies are not reaching this socket",
                this.endpoint.Address,
                local,
                replies,
                requests.Count,
                LifxConstants.ProbeReplyTimeoutMs,
                LifxMapping.DescribeTarget(target));
        }
    }
}

internal static class LifxMapping
{
    public static IPEndPoint RequireEndpoint(PluginOutputMappingConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
        {
            throw new InvalidOperationException("Destination address (the LIFX device IP) is required.");
        }

        if (!IPAddress.TryParse(config.DestinationAddress.Trim(), out IPAddress? ip))
        {
            throw new InvalidOperationException(
                $"Destination address '{config.DestinationAddress}' is not a valid IP address.");
        }

        return new IPEndPoint(ip, LifxConstants.Port);
    }

    /// <summary>
    /// The device serial a session addresses, or a note that it sends
    /// untargeted (tagged) frames because discovery never cached the device.
    /// </summary>
    public static string DescribeTarget(byte[] target) =>
        target.Any(static b => b != 0)
            ? Convert.ToHexString(target, 0, 6).ToLowerInvariant()
            : "none - untargeted, the device is not in the discovery cache";
}
