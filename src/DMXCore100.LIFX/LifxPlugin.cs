using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.LIFX;

/// <summary>
/// Discovers LIFX fixtures on the LAN and runs colour / effect commands.
/// Commands arrive on MQTT (same broker as the rest of the device), matching
/// the Home Assistant plugin's topic layout.
/// </summary>
/// <remarks>
/// Topic layout (serial is the device hardware id):
/// <code>
/// dmxcore/{serial}/lifx/command          text or JSON commands
/// dmxcore/{serial}/lifx/lights           retained discovered-light list
/// dmxcore/{serial}/lifx/{lightId}/set    per-light colour / power / effect
/// dmxcore/{serial}/lifx/{lightId}/state  retained colour + effect
/// </code>
/// All MQTT handlers run on the plugin's serial dispatch queue. The LAN
/// client and effect runner have their own locks for UDP / tick work.
/// </remarks>
public class LifxPlugin : IPlugin
{
    internal const string BindIpKey = "bind-ip";
    internal const string AutoDiscoverKey = "auto-discover";
    internal const string DiscoveryTimeoutKey = "discovery-timeout";
    internal const string FadeMsKey = "fade-ms";
    internal const string DefaultBrightnessKey = "default-brightness";
    internal const string DiscoveredTrigger = "LIFX-DISCOVERED";

    private readonly Func<string, ILifxLanClient> clientFactory;
    private readonly List<IDisposable> subscriptions = [];
    private IPluginHost host = null!;
    private ILifxLanClient? client;
    private LifxEffectRunner? effects;
    private CancellationTokenSource? discoveryCts;
    private string boundIp = "0.0.0.0";

    public LifxPlugin()
        : this(bindIp => new LifxLanClient(bindIp))
    {
    }

    internal LifxPlugin(Func<string, ILifxLanClient> clientFactory)
    {
        this.clientFactory = clientFactory;
    }

    public PluginInfo Info { get; } = new()
    {
        Id = "lifx",
        Name = "LIFX",
        Version = "1.0.0",
        Description = "Discovers LIFX lights on the LAN and runs colour and effect commands over the LIFX local protocol.",
        Settings =
        [
            new()
            {
                Key = BindIpKey,
                Label = "Bind IP",
                Type = PluginSettingType.String,
                DefaultValue = "0.0.0.0",
                Description = "Local interface for LIFX UDP (0.0.0.0 = all interfaces)",
            },
            new()
            {
                Key = AutoDiscoverKey,
                Label = "Discover on startup",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = DiscoveryTimeoutKey,
                Label = "Discovery timeout (seconds)",
                Type = PluginSettingType.Integer,
                DefaultValue = "5",
            },
            new()
            {
                Key = FadeMsKey,
                Label = "Default fade (ms)",
                Type = PluginSettingType.Integer,
                DefaultValue = "45",
                Description = "LIFX interpolation time for colour commands",
            },
            new()
            {
                Key = DefaultBrightnessKey,
                Label = "Default brightness (%)",
                Type = PluginSettingType.Integer,
                DefaultValue = "100",
            },
        ],
        Triggers =
        [
            new()
            {
                Code = DiscoveredTrigger,
                Label = "Lights discovered",
                Description = "Fired after a discovery pass that found at least one light",
            },
        ],
    };

    private string Serial => this.host.DeviceInfo.Serial.ToLowerInvariant();

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;
        this.subscriptions.Add(host.Mqtt.Subscribe(LifxStatus.CommandTopic(Serial), HandleCommand));
        this.subscriptions.Add(host.Mqtt.Subscribe($"dmxcore/{Serial}/lifx/+/set", HandleLightSet));
        this.subscriptions.Add(host.Settings.OnChanged(HandleSettingsChanged));
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(HandleConnectionChanged));

        try
        {
            await EnsureClient(cancellationToken);
        }
        catch (Exception ex)
        {
            host.Logger.LogError(ex, "Failed to start LIFX client");
            host.SetConnectionState(false, ex.Message);
            return;
        }

        if (host.Settings.GetBoolean(AutoDiscoverKey) != false)
        {
            StartBackgroundDiscovery();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        this.discoveryCts?.Cancel();
        this.effects?.Dispose();
        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        if (this.client != null)
        {
            await this.client.DisposeAsync();
        }
    }

    private async Task EnsureClient(CancellationToken cancellationToken)
    {
        string bindIp = (this.host.Settings.GetString(BindIpKey) ?? "0.0.0.0").Trim();
        if (this.client != null && string.Equals(this.boundIp, bindIp, StringComparison.Ordinal))
        {
            return;
        }

        this.effects?.Dispose();
        if (this.client != null)
        {
            await this.client.DisposeAsync();
        }

        this.client = this.clientFactory(bindIp);
        this.effects = new LifxEffectRunner(this.client);
        this.boundIp = bindIp;
        this.host.SetConnectionState(true, "LIFX client ready");
        this.host.Logger.LogInformation("LIFX client bound to {BindIp}", bindIp);
    }

    private void StartBackgroundDiscovery()
    {
        this.discoveryCts?.Cancel();
        this.discoveryCts?.Dispose();
        this.discoveryCts = new CancellationTokenSource();
        CancellationToken token = this.discoveryCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await DiscoverAndPublish(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.host.Logger.LogError(ex, "Background LIFX discovery failed");
            }
        }, token);
    }

    private async Task DiscoverAndPublish(CancellationToken cancellationToken)
    {
        if (this.client == null)
        {
            return;
        }

        LightRecord record = await LightRecord.Load(this.host, cancellationToken);
        foreach (LightRecord.Entry entry in record.Lights.Where(entry => !string.IsNullOrWhiteSpace(entry.Ip)))
        {
            try
            {
                await this.client.ProbeAsync(entry.Ip, TimeSpan.FromSeconds(1.5), cancellationToken);
            }
            catch (Exception ex)
            {
                this.host.Logger.LogDebug(ex, "Probe of {Ip} failed", entry.Ip);
            }
        }

        int timeout = Math.Clamp(this.host.Settings.GetInteger(DiscoveryTimeoutKey) ?? 5, 1, 30);
        IReadOnlyList<LifxLight> lights = await this.client.DiscoverAsync(TimeSpan.FromSeconds(timeout), cancellationToken);
        await PublishLights(lights, cancellationToken);
        await LightRecord.Save(this.host, lights, cancellationToken);

        this.host.Logger.LogInformation("Discovered {Count} LIFX light(s)", lights.Count);
        this.host.SetConnectionState(true, lights.Count == 1 ? "1 light" : $"{lights.Count} lights");

        if (lights.Count > 0)
        {
            await this.host.Triggers.FireAsync(DiscoveredTrigger, cancellationToken);
        }
    }

    private async Task HandleConnectionChanged(bool connected, CancellationToken cancellationToken)
    {
        if (connected && this.client != null)
        {
            await PublishLights(this.client.GetLights(), cancellationToken);
        }
    }

    private async Task HandleSettingsChanged(CancellationToken cancellationToken)
    {
        string bindIp = (this.host.Settings.GetString(BindIpKey) ?? "0.0.0.0").Trim();
        if (!string.Equals(this.boundIp, bindIp, StringComparison.Ordinal))
        {
            try
            {
                await EnsureClient(cancellationToken);
            }
            catch (Exception ex)
            {
                this.host.Logger.LogError(ex, "Failed to rebind LIFX client");
                this.host.SetConnectionState(false, ex.Message);
                return;
            }
        }

        if (this.host.Settings.GetBoolean(AutoDiscoverKey) != false)
        {
            StartBackgroundDiscovery();
        }
    }

    private async Task HandleCommand(MqttMessage message, CancellationToken cancellationToken)
    {
        if (!LifxCommandParser.TryParse(message.Payload, out LifxCommand? command, out string? error) || command == null)
        {
            this.host.Logger.LogWarning("Unparseable LIFX command '{Payload}': {Error}", message.Payload, error);
            return;
        }

        await Dispatch(command, cancellationToken);
    }

    private async Task HandleLightSet(MqttMessage message, CancellationToken cancellationToken)
    {
        string[] parts = message.Topic.Split('/');
        if (parts.Length != 5)
        {
            return;
        }

        string lightId = parts[3];
        string payload = message.Payload.Trim();
        if (payload.StartsWith('{') || payload.Contains(' ', StringComparison.Ordinal))
        {
            if (!LifxCommandParser.TryParse(payload, out LifxCommand? command, out string? error) || command == null)
            {
                this.host.Logger.LogWarning("Unparseable per-light command '{Payload}': {Error}", payload, error);
                return;
            }

            await Dispatch(WithTarget(command, lightId), cancellationToken);
            return;
        }

        if (string.Equals(payload, "ON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            await Dispatch(new LifxCommand.Power(lightId, payload.Equals("ON", StringComparison.OrdinalIgnoreCase)), cancellationToken);
            return;
        }

        try
        {
            await Dispatch(new LifxCommand.Effect(lightId, LifxCommandParser.ParseEffectName(payload), null), cancellationToken);
        }
        catch (FormatException)
        {
            this.host.Logger.LogWarning("Unparseable per-light payload '{Payload}' for {LightId}", payload, lightId);
        }
    }

    private async Task Dispatch(LifxCommand command, CancellationToken cancellationToken)
    {
        if (this.client == null || this.effects == null)
        {
            this.host.Logger.LogWarning("LIFX client is not ready");
            return;
        }

        switch (command)
        {
            case LifxCommand.Discover:
                await DiscoverAndPublish(cancellationToken);
                break;
            case LifxCommand.ListLights:
                await PublishLights(this.client.GetLights(), cancellationToken);
                break;
            case LifxCommand.Color color:
                ApplyColor(color);
                await PublishLights(this.client.GetLights(), cancellationToken);
                break;
            case LifxCommand.Power power:
                ApplyPower(power);
                await PublishLights(this.client.GetLights(), cancellationToken);
                break;
            case LifxCommand.Effect effect:
                ApplyEffect(effect);
                await PublishLights(this.client.GetLights(), cancellationToken);
                break;
            default:
            {
                LifxCommand unused = command;
                throw new InvalidOperationException($"Unhandled command {unused.GetType().Name}");
            }
        }
    }

    private void ApplyColor(LifxCommand.Color color)
    {
        IReadOnlyList<LifxLight> lights = Resolve(color.Target);
        this.effects!.Stop(lights);
        int fadeMs = color.FadeMs ?? this.host.Settings.GetInteger(FadeMsKey) ?? LifxConstants.DefaultFadeMs;
        double brightness = color.Brightness * DefaultBrightness();
        foreach (LifxLight light in lights)
        {
            this.client!.SetRgb(light, color.R / 255.0, color.G / 255.0, color.B / 255.0, LifxConstants.DefaultKelvin, fadeMs, brightness);
        }
    }

    private void ApplyPower(LifxCommand.Power power)
    {
        IReadOnlyList<LifxLight> lights = Resolve(power.Target);
        if (!power.On)
        {
            this.effects!.Stop(lights);
        }

        foreach (LifxLight light in lights)
        {
            this.client!.SetPower(light, power.On);
        }
    }

    private void ApplyEffect(LifxCommand.Effect effect)
    {
        IReadOnlyList<LifxLight> lights = Resolve(effect.Target);
        int speed = effect.SpeedMs ?? LifxConstants.DefaultEffectSpeedMs;
        int fadeMs = this.host.Settings.GetInteger(FadeMsKey) ?? LifxConstants.DefaultFadeMs;
        this.effects!.Start(lights, effect.Kind, speed, DefaultBrightness(), fadeMs);
    }

    private IReadOnlyList<LifxLight> Resolve(string target)
    {
        IReadOnlyList<LifxLight> matches = LifxTargets.Resolve(this.client!.GetLights(), target);
        if (matches.Count == 0)
        {
            this.host.Logger.LogWarning("No LIFX light matched target '{Target}'", target);
        }

        return matches;
    }

    private double DefaultBrightness()
    {
        int percent = this.host.Settings.GetInteger(DefaultBrightnessKey) ?? 100;
        return LifxColor.Clamp01(percent / 100.0);
    }

    private async Task PublishLights(IReadOnlyList<LifxLight> lights, CancellationToken cancellationToken)
    {
        if (!this.host.Mqtt.IsConnected)
        {
            return;
        }

        await this.host.Mqtt.PublishAsync(
            LifxStatus.LightsTopic(Serial),
            LifxStatus.LightsPayload(lights),
            retain: true,
            MqttQos.AtLeastOnce,
            cancellationToken);

        foreach (LifxLight light in lights)
        {
            LifxEffectKind effect = this.effects?.Current(light.Id) ?? LifxEffectKind.None;
            await this.host.Mqtt.PublishAsync(
                LifxStatus.LightStateTopic(Serial, light.Id),
                LifxStatus.LightStatePayload(light, effect),
                retain: true,
                MqttQos.AtLeastOnce,
                cancellationToken);
        }
    }

    private static LifxCommand WithTarget(LifxCommand command, string target) => command switch
    {
        LifxCommand.Discover discover => discover,
        LifxCommand.ListLights list => list,
        LifxCommand.Color color => color with { Target = target },
        LifxCommand.Power power => power with { Target = target },
        LifxCommand.Effect effect => effect with { Target = target },
        _ => throw new InvalidOperationException($"Unhandled command {command.GetType().Name}"),
    };
}
