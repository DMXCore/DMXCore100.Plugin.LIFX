using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.LIFX;

// Interactive harness: F5 this project to talk to real LIFX lights on the
// LAN through the output protocol, without a DMX Core 100 device. Use `r`
// to recycle Initialize/Shutdown in-process — the host cannot unload plugin
// assemblies, so this is the practical restart.

LifxPlugin plugin = new();
var host = new TestPluginHost(plugin.Info);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, cts.Token);

PrintHelp();

try
{
    bool running = true;
    while (running && !cts.IsCancellationRequested)
    {
        Console.Write("> ");
        string? input;
        try
        {
            input = (await ReadLineAsync(cts.Token))?.Trim();
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (input == null || cts.IsCancellationRequested)
        {
            break;
        }

        try
        {
            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "discover":
                case "m":
                    IReadOnlyList<PluginOutputDestinationOption>? options =
                        await host.OutputProtocols[LifxPlugin.ColorProtocolId].Protocol
                            .GetDestinationOptionsAsync(refresh: true, cts.Token);
                    IReadOnlyList<PluginOutputDestinationOption>? pixels =
                        await host.OutputProtocols[LifxPlugin.PixelProtocolId].Protocol
                            .GetDestinationOptionsAsync(refresh: false, cts.Token);
                    if (options == null || options.Count == 0)
                    {
                        Console.WriteLine("  no lights found");
                        break;
                    }

                    Console.WriteLine("  all:");
                    foreach (PluginOutputDestinationOption option in options)
                    {
                        Console.WriteLine($"    {option.Value}  {option.Label}");
                    }

                    if (pixels is { Count: > 0 })
                    {
                        Console.WriteLine("  pixel:");
                        foreach (PluginOutputDestinationOption option in pixels)
                        {
                            Console.WriteLine($"    {option.Value}  {option.Label}");
                        }
                    }

                    break;

                case "send":
                case "color":
                case "colour":
                    if (parts.Length < 5
                        || !byte.TryParse(parts[2], out byte red)
                        || !byte.TryParse(parts[3], out byte green)
                        || !byte.TryParse(parts[4], out byte blue))
                    {
                        Console.WriteLine("usage: send <ip> <r> <g> <b>");
                        break;
                    }

                    bool rgbOk = await host.SimulateOutputDeliveryAsync(
                        LifxPlugin.ColorProtocolId,
                        Mapping(parts[1]),
                        [red, green, blue],
                        cts.Token);
                    Console.WriteLine(rgbOk ? "  sent" : "  send failed");
                    break;

                case "sendct":
                    if (parts.Length < 6
                        || !byte.TryParse(parts[2], out byte ctr)
                        || !byte.TryParse(parts[3], out byte ctg)
                        || !byte.TryParse(parts[4], out byte ctb)
                        || !byte.TryParse(parts[5], out byte ct))
                    {
                        Console.WriteLine("usage: sendct <ip> <r> <g> <b> <ct>");
                        break;
                    }

                    bool ctOk = await host.SimulateOutputDeliveryAsync(
                        LifxPlugin.ColorCtProtocolId,
                        Mapping(parts[1]),
                        [ctr, ctg, ctb, ct],
                        cts.Token);
                    Console.WriteLine(ctOk ? "  sent" : "  send failed");
                    break;

                case "sendpixel":
                    if (parts.Length < 5
                        || !byte.TryParse(parts[2], out byte pr)
                        || !byte.TryParse(parts[3], out byte pg)
                        || !byte.TryParse(parts[4], out byte pb))
                    {
                        Console.WriteLine("usage: sendpixel <ip> <r> <g> <b>");
                        break;
                    }

                    IPluginOutputProtocol pixelProtocol =
                        host.OutputProtocols[LifxPlugin.PixelProtocolId].Protocol;
                    _ = await pixelProtocol.GetDestinationOptionsAsync(refresh: true, cts.Token);
                    int channels = pixelProtocol.GetChannelCount(Mapping(parts[1]));
                    if (channels < 3)
                    {
                        Console.WriteLine("  not a pixel device (run discover, or check the IP)");
                        break;
                    }

                    byte[] dmx = new byte[channels];
                    for (int i = 0; i < channels; i += 3)
                    {
                        dmx[i] = pr;
                        dmx[i + 1] = pg;
                        dmx[i + 2] = pb;
                    }

                    bool pixelOk = await host.SimulateOutputDeliveryAsync(
                        LifxPlugin.PixelProtocolId,
                        Mapping(parts[1]),
                        dmx,
                        cts.Token);
                    Console.WriteLine(pixelOk ? $"  sent {channels / 3} px" : "  send failed");
                    break;

                case "r":
                {
                    LifxPlugin replacement = new();
                    await replacement.InitializeAsync(host, cts.Token);
                    using var reinitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    reinitCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await plugin.ShutdownAsync(reinitCts.Token);
                    plugin = replacement;
                    Console.WriteLine("  plugin re-initialized in-process (assemblies stay loaded)");
                    break;
                }

                case "i":
                    Console.WriteLine($"  device: {host.DeviceInfo.ProductName} '{host.DeviceInfo.DeviceName}'");
                    Console.WriteLine($"  serial: {host.DeviceInfo.Serial}");
                    Console.WriteLine($"  version: {host.DeviceInfo.SoftwareVersion}");
                    break;

                case "d":
                    Console.WriteLine($"  protocols: {string.Join(", ", host.OutputProtocols.Keys)}");
                    Console.WriteLine($"  profiles:  {string.Join(", ", host.FixtureProfiles.Keys)}");
                    Console.WriteLine($"  connected: {host.ConnectionState} {host.ConnectionDetail}");
                    break;

                case "q":
                    running = false;
                    break;

                case "?":
                case "help":
                    PrintHelp();
                    break;

                default:
                    Console.WriteLine("unknown command, ? for help");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
        }
    }
}
finally
{
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await plugin.ShutdownAsync(shutdownCts.Token);
    Console.WriteLine("shut down cleanly");
}

static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
{
    Task<string?> read = Task.Run(Console.ReadLine, cancellationToken);
    return await read.WaitAsync(cancellationToken);
}

static PluginOutputMappingConfig Mapping(string ip) =>
    new()
    {
        DestinationAddress = ip,
        ChannelOffset = 0,
        UniverseId = 1,
    };

static void PrintHelp()
{
    Console.WriteLine("""
        Commands (output protocol, same path as the Core's Outputs page):
          discover                 broadcast-discover LIFX lights (pixel devices listed separately)
          send <ip> r g b          LIFX_COLOR SetColor (0-255)
          sendct <ip> r g b ct     LIFX_COLOR_CT, ct 0=warm 255=cool
          sendpixel <ip> r g b     LIFX_PIXEL fill all zones (Tube / Beam / strip)
          r                        shutdown + initialize again (no assembly unload)
          i                        show device info
          d                        dump registered protocols / profiles
          q                        quit
        """);
}
