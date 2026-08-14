using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.LIFX;

/// <summary>
/// Discovers LIFX fixtures on the LAN and runs colour / effect commands.
/// Discovery, identify, and test toggles live on the plugin settings page.
/// Optional follow of a Fixture Control fixture (and the master dimmer)
/// keeps house LIFX in step with the show.
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
    internal const string AutoDiscoverKey = "auto-discover";
    internal const string DiscoverNowKey = "discover-now";
    internal const string DiscoveryTimeoutKey = "discovery-timeout";
    internal const string TestAllKey = "test-all";
    internal const string TestRedKey = "test-red";
    internal const string TestGreenKey = "test-green";
    internal const string TestBlueKey = "test-blue";
    internal const string IdentifyKey = "identify";
    internal const string ChaseKey = "chase";
    internal const string SinewaveKey = "sinewave";
    internal const string FollowMasterKey = "follow-master";
    internal const string BlackoutOnCueEndKey = "blackout-on-cue-end";
    internal const string FollowFixtureKey = "follow-fixture";
    internal const string FadeMsKey = "fade-ms";
    internal const string DefaultBrightnessKey = "default-brightness";
    internal const string DiscoveredTrigger = "LIFX-DISCOVERED";
    internal const string MasterDimmerCode = "system.masterdimmer";
    internal const int LightSlotCount = 12;
    internal const string BindAddress = "0.0.0.0";

    private readonly Func<string, ILifxLanClient>? clientFactory;
    private readonly string?[] slotLightIds = new string[LightSlotCount];
    private readonly bool[] lastLightSlots = new bool[LightSlotCount];
    private readonly List<IDisposable> subscriptions = [];
    private IPluginHost host = null!;
    private ILifxLanClient? client;
    private LifxEffectRunner? effects;
    private CancellationTokenSource? discoveryCts;
    private Task? discoveryTask;
    private bool lastDiscoverNow;
    private bool lastAutoDiscover;
    private bool lastTestAll;
    private bool lastIdentify;
    private bool lastChase;
    private bool lastSinewave;
    private bool lastFollowMaster;
    private double masterLevel = 1.0;
    private string lastTarget = "all";
    private LifxLook? lastLook;
    private string? lastFollowedFixture;
    private (int R, int G, int B, int Brightness)? lastFollowedColor;

    public LifxPlugin()
        : this(null)
    {
    }

    internal LifxPlugin(Func<string, ILifxLanClient>? clientFactory)
    {
        this.clientFactory = clientFactory;

        Info = new()
        {
            Id = "lifx",
            Name = "LIFX",
            Version = "0.0.3",
            Description = "Discovers LIFX lights on the LAN and runs colour and effect commands over the LIFX local protocol.",
            Settings = BuildSettings(),
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
    }

    public PluginInfo Info { get; }

    internal static string LightSlotKey(int index) => $"light-{index}";

    private string Serial => this.host.DeviceInfo.Serial.ToLowerInvariant();

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;
        this.lastDiscoverNow = host.Settings.GetBoolean(DiscoverNowKey) == true;
        this.lastAutoDiscover = host.Settings.GetBoolean(AutoDiscoverKey) != false;
        this.lastTestAll = host.Settings.GetBoolean(TestAllKey) == true;
        this.lastIdentify = host.Settings.GetBoolean(IdentifyKey) == true;
        this.lastChase = host.Settings.GetBoolean(ChaseKey) == true;
        this.lastSinewave = host.Settings.GetBoolean(SinewaveKey) == true;
        this.lastFollowMaster = host.Settings.GetBoolean(FollowMasterKey) == true;
        this.lastFollowedFixture = LifxFixtureFollow.NormalizeCode(host.Settings.GetString(FollowFixtureKey));
        PluginEntityState? dimmer = await host.Entities.GetStateAsync(MasterDimmerCode, cancellationToken);
        if (dimmer?.Level is double level)
        {
            this.masterLevel = LifxColor.Clamp01(level);
        }

        for (int i = 0; i < LightSlotCount; i++)
        {
            this.lastLightSlots[i] = host.Settings.GetBoolean(LightSlotKey(i + 1)) == true;
        }

        this.subscriptions.Add(host.Mqtt.Subscribe(LifxStatus.CommandTopic(Serial), HandleCommand));
        this.subscriptions.Add(host.Mqtt.Subscribe($"dmxcore/{Serial}/lifx/+/set", HandleLightSet));
        this.subscriptions.Add(host.Settings.OnChanged(HandleSettingsChanged));
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(HandleConnectionChanged));
        this.subscriptions.Add(host.Playback.OnCueEnded(HandleCueEnded));
        this.subscriptions.Add(host.Entities.OnStateChanged(HandleEntityState));

        try
        {
            EnsureClient();
        }
        catch (Exception ex)
        {
            host.Logger.LogError(ex, "Failed to start LIFX client");
            host.SetConnectionState(false, ex.Message);
            return;
        }

        await ApplyFollowedFixtureAsync(cancellationToken);

        if (this.lastAutoDiscover)
        {
            _ = StartBackgroundDiscovery();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (this.discoveryCts != null)
        {
            await this.discoveryCts.CancelAsync();
        }

        if (this.discoveryTask != null)
        {
            try
            {
                await this.discoveryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.discoveryCts?.Dispose();
        this.discoveryCts = null;
        this.discoveryTask = null;

        this.effects?.Dispose();
        this.effects = null;

        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        if (this.client != null)
        {
            await this.client.DisposeAsync();
            this.client = null;
        }
    }

    private IReadOnlyList<PluginSettingDescriptor> BuildSettings() =>
    [
        new()
        {
            Key = DiscoverNowKey,
            Label = "Discover now",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "Toggle on to scan the LAN. Names show on the plugin status. Light 1 is the first found, Light 2 the second, and so on.",
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
            Key = IdentifyKey,
            Label = "Identify lights",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "Paints each discovered light a different colour so you can match Light 1–12.",
        },
        new()
        {
            Key = TestAllKey,
            Label = "Test all lights",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "Toggle on to send the test colour to every discovered light; off turns them off.",
        },
        new()
        {
            Key = ChaseKey,
            Label = "Chase",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "RGB chase on every discovered light.",
        },
        new()
        {
            Key = SinewaveKey,
            Label = "Sinewave",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "Hue sinewave on every discovered light.",
        },
        ..Enumerable.Range(1, LightSlotCount).Select(index => new PluginSettingDescriptor
        {
            Key = LightSlotKey(index),
            Label = $"Light {index}",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = $"Toggle on to send the test colour to the {Ordinal(index)} light from the last scan; off turns it off.",
        }),
        new()
        {
            Key = TestRedKey,
            Label = "Test red",
            Type = PluginSettingType.Integer,
            DefaultValue = "255",
        },
        new()
        {
            Key = TestGreenKey,
            Label = "Test green",
            Type = PluginSettingType.Integer,
            DefaultValue = "80",
        },
        new()
        {
            Key = TestBlueKey,
            Label = "Test blue",
            Type = PluginSettingType.Integer,
            DefaultValue = "0",
        },
        new()
        {
            Key = FollowMasterKey,
            Label = "Follow master dimmer",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
            Description = "Scale LIFX brightness with the DMX Core master dimmer.",
        },
        new()
        {
            Key = BlackoutOnCueEndKey,
            Label = "Blackout when a cue ends",
            Type = PluginSettingType.Boolean,
            DefaultValue = "false",
        },
        new()
        {
            Key = FollowFixtureKey,
            Label = "Follow fixture",
            Type = PluginSettingType.String,
            DefaultValue = "",
            Description = "Fixture Control code to track (RGB + intensity). Requires the host to expose that fixture as an entity (fixture.CODE). Leave empty to drive LIFX from MQTT / test toggles only.",
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
    ];

    private void EnsureClient()
    {
        if (this.client != null)
        {
            return;
        }

        this.client = this.clientFactory?.Invoke(BindAddress)
            ?? new LifxLanClient(BindAddress, OnListenFailure);
        this.effects = new LifxEffectRunner(
            this.client,
            ex => this.host.Logger.LogWarning(ex, "LIFX effect tick failed"));
        this.host.SetConnectionState(true, "LIFX client ready");
        this.host.Logger.LogInformation("LIFX client listening on all interfaces");
    }

    private async Task StartBackgroundDiscovery()
    {
        CancellationTokenSource? previousCts = this.discoveryCts;
        Task? previous = this.discoveryTask;
        if (previousCts != null)
        {
            await previousCts.CancelAsync();
        }

        if (previous != null)
        {
            try
            {
                await previous;
            }
            catch (OperationCanceledException)
            {
            }
        }

        previousCts?.Dispose();
        this.discoveryCts = new CancellationTokenSource();
        CancellationToken token = this.discoveryCts.Token;
        this.discoveryTask = Task.Run(async () =>
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

    private void OnListenFailure(Exception ex)
    {
        this.host.Logger.LogError(ex, "LIFX listen failed");
        this.host.SetConnectionState(false, ex.Message);
    }

    private async Task DiscoverAndPublish(CancellationToken cancellationToken)
    {
        if (this.client == null)
        {
            return;
        }

        LightRecord record = await LightRecord.Load(this.host, cancellationToken);
        await Task.WhenAll(record.Lights
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Ip))
            .Select(entry => ProbePersisted(entry.Ip, cancellationToken)));

        int timeout = Math.Clamp(this.host.Settings.GetInteger(DiscoveryTimeoutKey) ?? 5, 1, 30);
        IReadOnlyList<LifxLight> lights = await this.client.DiscoverAsync(TimeSpan.FromSeconds(timeout), cancellationToken);
        ApplyDiscoveredLights(lights);
        await PublishLights(lights, cancellationToken);
        await LightRecord.Save(this.host, lights, cancellationToken);

        this.host.Logger.LogInformation("Discovered {Count} LIFX light(s)", lights.Count);
        this.host.SetConnectionState(true, ConnectionSummary(lights));

        if (lights.Count > 0)
        {
            await this.host.Triggers.FireAsync(DiscoveredTrigger, cancellationToken);
        }
    }

    private async Task ProbePersisted(string ip, CancellationToken cancellationToken)
    {
        try
        {
            await this.client!.ProbeAsync(ip, TimeSpan.FromSeconds(1.5), cancellationToken);
        }
        catch (Exception ex)
        {
            this.host.Logger.LogDebug(ex, "Probe of {Ip} failed", ip);
        }
    }

    internal void ApplyDiscoveredLights(IReadOnlyList<LifxLight> lights)
    {
        for (int i = 0; i < LightSlotCount; i++)
        {
            this.slotLightIds[i] = i < lights.Count ? lights[i].Id : null;
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
        bool autoDiscover = this.host.Settings.GetBoolean(AutoDiscoverKey) != false;
        bool discoverNow = this.host.Settings.GetBoolean(DiscoverNowKey) == true;
        bool testAll = this.host.Settings.GetBoolean(TestAllKey) == true;
        bool identify = this.host.Settings.GetBoolean(IdentifyKey) == true;
        bool chase = this.host.Settings.GetBoolean(ChaseKey) == true;
        bool sinewave = this.host.Settings.GetBoolean(SinewaveKey) == true;
        bool followMaster = this.host.Settings.GetBoolean(FollowMasterKey) == true;
        bool wasOverride = ManualOverrideActive();
        bool dirty = false;

        if (discoverNow && !this.lastDiscoverNow)
        {
            await StartBackgroundDiscovery();
        }
        else if (autoDiscover && !this.lastAutoDiscover)
        {
            await StartBackgroundDiscovery();
        }

        this.lastDiscoverNow = discoverNow;
        this.lastAutoDiscover = autoDiscover;

        if (identify && !this.lastIdentify)
        {
            IdentifyLights();
            dirty = true;
        }
        else if (!identify && this.lastIdentify)
        {
            ApplyPower(new LifxCommand.Power("all", false));
            dirty = true;
        }

        if (testAll && !this.lastTestAll)
        {
            ApplyColor(new LifxCommand.Color("all", TestRed(), TestGreen(), TestBlue(), 1.0, null));
            dirty = true;
        }
        else if (!testAll && this.lastTestAll)
        {
            ApplyPower(new LifxCommand.Power("all", false));
            dirty = true;
        }

        if (chase && !this.lastChase)
        {
            ApplyEffect(new LifxCommand.Effect("all", LifxEffectKind.Chase, null));
            dirty = true;
        }
        else if (!chase && this.lastChase)
        {
            ApplyEffect(new LifxCommand.Effect(
                "all",
                sinewave ? LifxEffectKind.Sinewave : LifxEffectKind.None,
                null));
            dirty = true;
        }

        if (sinewave && !this.lastSinewave)
        {
            ApplyEffect(new LifxCommand.Effect("all", LifxEffectKind.Sinewave, null));
            dirty = true;
        }
        else if (!sinewave && this.lastSinewave)
        {
            ApplyEffect(new LifxCommand.Effect(
                "all",
                chase ? LifxEffectKind.Chase : LifxEffectKind.None,
                null));
            dirty = true;
        }

        string? followFixture = LifxFixtureFollow.NormalizeCode(this.host.Settings.GetString(FollowFixtureKey));
        if (followFixture != this.lastFollowedFixture)
        {
            this.lastFollowedFixture = followFixture;
            this.lastFollowedColor = null;
            if (await ApplyFollowedFixtureAsync(cancellationToken))
            {
                dirty = true;
            }
        }

        if (followMaster != this.lastFollowMaster)
        {
            ResendCurrentLook();
            dirty = true;
        }

        this.lastIdentify = identify;
        this.lastTestAll = testAll;
        this.lastChase = chase;
        this.lastSinewave = sinewave;
        this.lastFollowMaster = followMaster;

        for (int i = 0; i < LightSlotCount; i++)
        {
            bool on = this.host.Settings.GetBoolean(LightSlotKey(i + 1)) == true;
            if (on == this.lastLightSlots[i])
            {
                continue;
            }

            this.lastLightSlots[i] = on;
            string? id = this.slotLightIds[i];
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (on)
            {
                ApplyColor(new LifxCommand.Color(id, TestRed(), TestGreen(), TestBlue(), 1.0, null));
            }
            else
            {
                ApplyPower(new LifxCommand.Power(id, false));
            }

            dirty = true;
        }

        if (wasOverride && !ManualOverrideActive())
        {
            this.lastFollowedColor = null;
            if (await ApplyFollowedFixtureAsync(cancellationToken))
            {
                dirty = true;
            }
        }

        if (dirty)
        {
            await PublishLights(this.client?.GetLights() ?? [], cancellationToken);
        }
    }

    private async Task HandleCueEnded(CuePlaybackEvent playback, CancellationToken cancellationToken)
    {
        if (this.host.Settings.GetBoolean(BlackoutOnCueEndKey) != true)
        {
            return;
        }

        ApplyPower(new LifxCommand.Power("all", false));
        await PublishLights(this.client?.GetLights() ?? [], cancellationToken);
    }

    private async Task HandleEntityState(PluginEntityState state, CancellationToken cancellationToken)
    {
        bool dirty = false;
        if (string.Equals(state.Code, MasterDimmerCode, StringComparison.OrdinalIgnoreCase)
            && state.Level is double level)
        {
            this.masterLevel = LifxColor.Clamp01(level);
            if (this.host.Settings.GetBoolean(FollowMasterKey) == true && this.lastLook != null)
            {
                ResendCurrentLook();
                dirty = true;
            }
        }

        if (ApplyFollowedFixture(state))
        {
            dirty = true;
        }

        if (dirty)
        {
            await PublishLights(this.client?.GetLights() ?? [], cancellationToken);
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
            case LifxCommand.Identify:
                IdentifyLights();
                await PublishLights(this.client.GetLights(), cancellationToken);
                break;
            default:
            {
                LifxCommand unused = command;
                throw new InvalidOperationException($"Unhandled command {unused.GetType().Name}");
            }
        }
    }

    private async Task<bool> ApplyFollowedFixtureAsync(CancellationToken cancellationToken)
    {
        string? code = LifxFixtureFollow.NormalizeCode(this.host.Settings.GetString(FollowFixtureKey));
        this.lastFollowedFixture = code;
        if (code == null || ManualOverrideActive())
        {
            return false;
        }

        foreach (string entityCode in LifxFixtureFollow.EntityCodes(code))
        {
            PluginEntityState? state = await this.host.Entities.GetStateAsync(entityCode, cancellationToken);
            if (state != null && ApplyFollowedFixture(state))
            {
                return true;
            }
        }

        this.host.Logger.LogWarning(
            "Follow fixture '{Code}' has no entity state (tried {Tried}). Fixture Control does not publish fixtures to plugins yet — only entities such as system.masterdimmer.",
            code,
            string.Join(", ", LifxFixtureFollow.EntityCodes(code)));
        return false;
    }

    private bool ApplyFollowedFixture(PluginEntityState state)
    {
        string? code = this.lastFollowedFixture
            ?? LifxFixtureFollow.NormalizeCode(this.host.Settings.GetString(FollowFixtureKey));
        if (this.client == null
            || this.effects == null
            || code == null
            || !LifxFixtureFollow.Matches(code, state.Code)
            || ManualOverrideActive())
        {
            return false;
        }

        if (!LifxFixtureFollow.TryRead(state, out LifxLook.Color color))
        {
            return false;
        }

        var quantized = (color.R, color.G, color.B, (int)Math.Round(color.Brightness * 1000.0));
        if (this.lastFollowedColor == quantized)
        {
            return false;
        }

        this.lastFollowedColor = quantized;
        if (color.Brightness <= 0.0001)
        {
            ApplyPower(new LifxCommand.Power("all", false));
        }
        else
        {
            ApplyColor(new LifxCommand.Color("all", color.R, color.G, color.B, color.Brightness, null));
        }

        return true;
    }

    private bool ManualOverrideActive() =>
        this.lastIdentify
        || this.lastTestAll
        || this.lastChase
        || this.lastSinewave
        || this.lastLightSlots.Any(on => on);

    private void ApplyLook(LifxLook look, string target)
    {
        switch (look)
        {
            case LifxLook.Color color:
                ApplyColor(new LifxCommand.Color(target, color.R, color.G, color.B, color.Brightness, null));
                break;
            case LifxLook.Power power:
                ApplyPower(new LifxCommand.Power(target, power.On));
                break;
            case LifxLook.Effect effect:
                ApplyEffect(new LifxCommand.Effect(target, effect.Kind, null));
                break;
            case LifxLook.Identify:
                IdentifyLights();
                break;
            default:
            {
                LifxLook unused = look;
                throw new InvalidOperationException($"Unhandled look {unused.GetType().Name}");
            }
        }
    }

    private void ResendCurrentLook()
    {
        if (this.lastLook == null)
        {
            return;
        }

        ApplyLook(this.lastLook, this.lastTarget);
    }

    private void ApplyColor(LifxCommand.Color color)
    {
        if (this.client == null || this.effects == null)
        {
            return;
        }

        this.lastTarget = color.Target;
        this.lastLook = new LifxLook.Color(color.R, color.G, color.B, color.Brightness);
        IReadOnlyList<LifxLight> lights = Resolve(color.Target);
        this.effects.Stop(lights);
        int fadeMs = color.FadeMs ?? this.host.Settings.GetInteger(FadeMsKey) ?? LifxConstants.DefaultFadeMs;
        double brightness = color.Brightness * DefaultBrightness() * MasterScale();
        foreach (LifxLight light in lights)
        {
            this.client!.SetRgb(light, color.R / 255.0, color.G / 255.0, color.B / 255.0, LifxConstants.DefaultKelvin, fadeMs, brightness);
        }
    }

    private void ApplyPower(LifxCommand.Power power)
    {
        if (this.client == null || this.effects == null)
        {
            return;
        }

        this.lastTarget = power.Target;
        this.lastLook = new LifxLook.Power(power.On);
        IReadOnlyList<LifxLight> lights = Resolve(power.Target);
        if (!power.On)
        {
            this.effects.Stop(lights);
        }

        foreach (LifxLight light in lights)
        {
            this.client!.SetPower(light, power.On);
        }
    }

    private void ApplyEffect(LifxCommand.Effect effect)
    {
        if (this.client == null || this.effects == null)
        {
            return;
        }

        this.lastTarget = effect.Target;
        this.lastLook = new LifxLook.Effect(effect.Kind);
        IReadOnlyList<LifxLight> lights = Resolve(effect.Target);
        int speed = effect.SpeedMs ?? LifxConstants.DefaultEffectSpeedMs;
        int fadeMs = this.host.Settings.GetInteger(FadeMsKey) ?? LifxConstants.DefaultFadeMs;
        this.effects.Start(lights, effect.Kind, speed, DefaultBrightness() * MasterScale(), fadeMs);
    }

    private void IdentifyLights()
    {
        if (this.client == null || this.effects == null)
        {
            return;
        }

        this.lastTarget = "all";
        this.lastLook = new LifxLook.Identify();
        IReadOnlyList<LifxLight> lights = this.client.GetLights();
        this.effects.Stop(lights);
        int fadeMs = this.host.Settings.GetInteger(FadeMsKey) ?? LifxConstants.DefaultFadeMs;
        double brightness = DefaultBrightness() * MasterScale();
        for (int i = 0; i < lights.Count; i++)
        {
            double hue = lights.Count <= 1 ? 0.0 : i / (double)lights.Count;
            LifxColor.HsvToRgb(hue, 1.0, 1.0, out double r, out double g, out double b);
            this.client.SetRgb(lights[i], r, g, b, LifxConstants.DefaultKelvin, fadeMs, brightness);
        }
    }

    private double MasterScale() =>
        this.host.Settings.GetBoolean(FollowMasterKey) == true
            ? LifxColor.Clamp01(this.masterLevel)
            : 1.0;

    private IReadOnlyList<LifxLight> Resolve(string target)
    {
        IReadOnlyList<LifxLight> matches = LifxTargets.Resolve(this.client!.GetLights(), target);
        if (matches.Count == 0)
        {
            this.host.Logger.LogWarning("No LIFX light matched target '{Target}'", target);
        }

        return matches;
    }

    private int TestRed() => ClampByte(this.host.Settings.GetInteger(TestRedKey) ?? 255);

    private int TestGreen() => ClampByte(this.host.Settings.GetInteger(TestGreenKey) ?? 80);

    private int TestBlue() => ClampByte(this.host.Settings.GetInteger(TestBlueKey) ?? 0);

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

    private static string ConnectionSummary(IReadOnlyList<LifxLight> lights)
    {
        if (lights.Count == 0)
        {
            return "No lights found";
        }

        string names = string.Join(", ", lights.Select(static light => light.DisplayLabel));
        return names.Length <= 80 ? names : $"{lights.Count} lights";
    }

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);

    private static string Ordinal(int index) => index switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{index}th",
    };

    private static LifxCommand WithTarget(LifxCommand command, string target) => command switch
    {
        LifxCommand.Discover discover => discover,
        LifxCommand.ListLights list => list,
        LifxCommand.Color color => color with { Target = target },
        LifxCommand.Power power => power with { Target = target },
        LifxCommand.Effect effect => effect with { Target = target },
        LifxCommand.Identify identify => identify,
        _ => throw new InvalidOperationException($"Unhandled command {command.GetType().Name}"),
    };
}
