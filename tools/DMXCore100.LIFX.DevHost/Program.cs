using System.Globalization;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.LIFX;

// Interactive harness: F5 this project to talk to real LIFX lights on the
// LAN through the plugin, without a DMX Core 100 device. MQTT commands are
// simulated in-process; UDP discovery and SET_COLOR go out on the network.
// Use `r` to recycle Initialize/Shutdown in-process — the host cannot unload
// plugin assemblies, so this is the practical restart.

LifxPlugin plugin = new();
var host = new TestPluginHost(plugin.Info);
host.EntityCatalog.Add(new PluginEntity
{
    Code = "system.masterdimmer",
    Name = "Master Dimmer",
    Kind = PluginEntityKind.Level,
});
host.EntityCatalog.Add(new PluginEntity
{
    Code = "fixture.HOUSE",
    Name = "House LIFX",
    Kind = PluginEntityKind.Level,
});
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, CancellationToken.None);

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
            string[] parts = input.Split(' ', 2);
            string serial = host.DeviceInfo.Serial.ToLowerInvariant();

            switch (parts[0].ToLowerInvariant())
            {
                case "discover":
                case "m":
                    await host.SimulateMqttMessageAsync(
                        LifxStatus.CommandTopic(serial),
                        parts.Length > 1 ? parts[1] : "discover");
                    break;

                case "color":
                case "colour":
                case "power":
                case "effect":
                case "list":
                case "identify":
                case "white":
                    await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic(serial), input);
                    break;

                case "fixture":
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: fixture <code> <r> <g> <b> [brightness]");
                        break;
                    }

                    string[] fixtureParts = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fixtureParts.Length < 4
                        || !int.TryParse(fixtureParts[1], out int fr)
                        || !int.TryParse(fixtureParts[2], out int fg)
                        || !int.TryParse(fixtureParts[3], out int fb))
                    {
                        Console.WriteLine("usage: fixture <code> <r> <g> <b> [brightness]");
                        break;
                    }

                    double fixtureBrightness = 1.0;
                    if (fixtureParts.Length > 4)
                    {
                        if (!double.TryParse(
                            fixtureParts[4],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out fixtureBrightness)
                            || !double.IsFinite(fixtureBrightness)
                            || fixtureBrightness is < 0 or > 100)
                        {
                            Console.WriteLine("usage: fixture <code> <r> <g> <b> [brightness]");
                            break;
                        }

                        if (fixtureBrightness > 1.0)
                        {
                            fixtureBrightness /= 100.0;
                        }
                    }

                    await host.SimulateEntityStateAsync(new DMXCore.PluginSdk.PluginEntityState
                    {
                        Code = "fixture." + fixtureParts[0],
                        Text = $$"""{"r":{{fr}},"g":{{fg}},"b":{{fb}},"brightness":{{fixtureBrightness.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""",
                    });
                    break;

                case "cueend":
                    await host.SimulateCueEndedAsync(parts.Length > 1 ? parts[1] : "TEST");
                    break;

                case "v":
                    double dimmer = 0.5;
                    if (parts.Length > 1)
                    {
                        if (!double.TryParse(
                            parts[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out dimmer)
                            || !double.IsFinite(dimmer)
                            || dimmer is < 0 or > 1)
                        {
                            Console.WriteLine("usage: v [level]");
                            break;
                        }
                    }

                    await host.SimulateEntityStateAsync(new PluginEntityState
                    {
                        Code = "system.masterdimmer",
                        Level = dimmer,
                    });
                    break;

                case "r":
                    LifxPlugin replacement = new();
                    await replacement.InitializeAsync(host, CancellationToken.None);
                    await plugin.ShutdownAsync(CancellationToken.None);
                    plugin = replacement;
                    Console.WriteLine("  plugin re-initialized in-process (assemblies stay loaded)");
                    break;

                case "i":
                    Console.WriteLine($"  device: {host.DeviceInfo.ProductName} '{host.DeviceInfo.DeviceName}'");
                    Console.WriteLine($"  serial: {host.DeviceInfo.Serial}");
                    Console.WriteLine($"  version: {host.DeviceInfo.SoftwareVersion}");
                    break;

                case "x":
                    await host.SimulateMqttConnectionChangedAsync(!host.MqttConnected);
                    Console.WriteLine($"  MQTT connected: {host.MqttConnected}");
                    break;

                case "s":
                    string[] setting = input.Split(' ', 3);
                    if (setting.Length < 3)
                    {
                        Console.WriteLine("usage: s <key> <value>");
                        break;
                    }

                    host.SetSetting(setting[1], setting[2]);
                    await host.TriggerSettingsChangedAsync();
                    break;

                case "d":
                    Console.WriteLine($"  published: {host.PublishedMessages.Count}");
                    Console.WriteLine($"  triggers:  {string.Join(", ", host.FiredTriggers.Distinct())}");
                    var lights = host.PublishedMessages.LastOrDefault(x => x.Topic == LifxStatus.LightsTopic(serial));
                    if (lights != default)
                    {
                        Console.WriteLine($"  lights:    {lights.Payload}");
                    }

                    break;

                case "q":
                    running = false;
                    break;

                case "?":
                case "help":
                    PrintHelp();
                    break;

                default:
                    if (input.Length > 0)
                    {
                        Console.WriteLine("unknown command, ? for help");
                    }

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
    await plugin.ShutdownAsync(CancellationToken.None);
    Console.WriteLine("shut down cleanly");
}

static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
{
    Task<string?> read = Task.Run(Console.ReadLine, cancellationToken);
    return await read.WaitAsync(cancellationToken);
}

static void PrintHelp()
{
    Console.WriteLine("""
        Commands (sent as MQTT on dmxcore/{serial}/lifx/command):
          discover                 broadcast-discover LIFX lights
          list                     republish the discovered-light list
          identify                 paint each light a different hue
          white all                set RGB white
          color all 255 0 0        set RGB (0-255), optional brightness + fade_ms
          power all on|off
          effect all chase         chase | sinewave | rainbow | pixel-chase | stop
          fixture <code> r g b [br]  simulate Fixture Control follow
          v [level]                simulate master dimmer (0-1, default 0.5)
          cueend [code]            simulate a cue-ended event
          m <payload>              raw command payload (text or JSON)
          s <key> <value>          change a setting (discover-now true, light-1 true)
          r                        shutdown + initialize again (no assembly unload)
          i                        show device info
          x                        toggle MQTT connection
          d                        dump published messages / triggers
          q                        quit
        """);
}
