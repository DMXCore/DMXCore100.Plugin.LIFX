using DMXCore.PluginSdk.Testing;
using DMXCore100.LIFX;

// Interactive harness: F5 this project to talk to real LIFX lights on the
// LAN through the plugin, without a DMX Core 100 device. MQTT commands are
// simulated in-process; UDP discovery and SET_COLOR go out on the network.

var plugin = new LifxPlugin();
var host = new TestPluginHost(plugin.Info);

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, CancellationToken.None);

PrintHelp();

bool running = true;
while (running)
{
    Console.Write("> ");
    string? input = Console.ReadLine()?.Trim();
    if (input == null)
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
                await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic(serial), input);
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

await plugin.ShutdownAsync(CancellationToken.None);
Console.WriteLine("shut down cleanly");

static void PrintHelp()
{
    Console.WriteLine("""
        Commands (sent as MQTT on dmxcore/{serial}/lifx/command):
          discover                 broadcast-discover LIFX lights
          list                     republish the discovered-light list
          color all 255 0 0        set RGB (0-255), optional brightness + fade_ms
          power all on|off
          effect all chase         chase | sinewave | rainbow | pixel-chase | stop
          m <payload>              raw command payload (text or JSON)
          s <key> <value>          change a setting (bind-ip, fade-ms, ...)
          x                        toggle MQTT connection
          d                        dump published messages / triggers
          q                        quit
        """);
}
