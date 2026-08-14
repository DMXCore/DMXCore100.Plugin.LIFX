using System.Text.Json;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxPluginTests
{
    private readonly List<LifxPlugin> plugins = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (LifxPlugin plugin in this.plugins)
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }

        this.plugins.Clear();
    }

    private async Task<(LifxPlugin Plugin, TestPluginHost Host, FakeLifxClient Client)> CreateInitializedAsync(
        Action<TestPluginHost, FakeLifxClient>? configure = null)
    {
        var client = new FakeLifxClient();
        var kitchen = new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
        {
            ModelName = "LIFX A19",
            Product = 27,
            Power = 65535,
        };
        client.AddLight(kitchen);

        var plugin = new LifxPlugin(_ => client);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting(LifxPlugin.AutoDiscoverKey, "false");
        configure?.Invoke(host, client);

        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, client);
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(failure);
        }
    }

    private static (string Topic, string Payload, bool Retain)? FindPublished(TestPluginHost host, string topic) =>
        host.PublishedMessages.Where(x => x.Topic == topic).Select(x => ((string, string, bool)?)x).LastOrDefault();

    [TestMethod]
    public async Task Discover_PublishesLightsAndFiresTrigger()
    {
        var (_, host, client) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");

        Assert.AreEqual(1, client.DiscoverCalls);
        Assert.AreEqual(LifxPlugin.DiscoveredTrigger, host.FiredTriggers.Single());

        var message = FindPublished(host, LifxStatus.LightsTopic("test-serial"));
        Assert.IsNotNull(message);
        Assert.IsTrue(message.Value.Retain);
        using var doc = JsonDocument.Parse(message.Value.Payload);
        Assert.AreEqual(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual("Kitchen", doc.RootElement.GetProperty("lights")[0].GetProperty("label").GetString());
    }

    [TestMethod]
    public async Task ColorCommand_SendsRgbAndPublishesState()
    {
        var (_, host, client) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");
        host.PublishedMessages.Clear();

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "color all 255 0 0");

        Assert.AreEqual(1, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].R, 1e-9);
        Assert.AreEqual(0.0, client.Colors[0].G, 1e-9);
        Assert.AreEqual(45, client.Colors[0].DurationMs);

        var state = FindPublished(host, LifxStatus.LightStateTopic("test-serial", client.Lights[0].Id));
        Assert.IsNotNull(state);
        using var doc = JsonDocument.Parse(state.Value.Payload);
        Assert.AreEqual(255, doc.RootElement.GetProperty("r").GetInt32());
        Assert.AreEqual("none", doc.RootElement.GetProperty("effect").GetString());
    }

    [TestMethod]
    public async Task PowerCommand_TurnsLightOff()
    {
        var (_, host, client) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "power Kitchen off");

        Assert.AreEqual((client.Lights[0].Id, false), client.Powers.Single());
        Assert.AreEqual(0, client.Lights[0].Power);
    }

    [TestMethod]
    public async Task PerLightSet_OnOff()
    {
        var (_, host, client) = await CreateInitializedAsync();
        string id = client.Lights[0].Id;

        await host.SimulateMqttMessageAsync(LifxStatus.LightSetTopic("test-serial", id), "OFF");

        Assert.AreEqual(false, client.Powers.Single().On);
    }

    [TestMethod]
    public async Task JsonColor_UsesFadeAndBrightness()
    {
        var (_, host, client) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync(
            LifxStatus.CommandTopic("test-serial"),
            """{"cmd":"color","target":"all","r":0,"g":255,"b":0,"brightness":0.5,"fade_ms":120}""");

        Assert.AreEqual(0.5, client.Colors.Single().Brightness, 1e-9);
        Assert.AreEqual(120, client.Colors.Single().DurationMs);
    }

    [TestMethod]
    public async Task EffectCommand_PublishesEffectField()
    {
        var (_, host, client) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");
        host.PublishedMessages.Clear();

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "effect all chase");

        var state = FindPublished(host, LifxStatus.LightStateTopic("test-serial", client.Lights[0].Id));
        Assert.IsNotNull(state);
        using var doc = JsonDocument.Parse(state.Value.Payload);
        Assert.AreEqual("chase", doc.RootElement.GetProperty("effect").GetString());
    }

    [TestMethod]
    public async Task UnknownCommand_DoesNotThrow()
    {
        var (_, host, client) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "nope");

        Assert.AreEqual(0, client.Colors.Count);
        Assert.AreEqual(0, client.DiscoverCalls);
    }

    [TestMethod]
    public async Task Reconnect_RepublishesLights()
    {
        var (_, host, _) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");
        host.PublishedMessages.Clear();

        await host.SimulateMqttConnectionChangedAsync(false);
        await host.SimulateMqttConnectionChangedAsync(true);

        Assert.IsNotNull(FindPublished(host, LifxStatus.LightsTopic("test-serial")));
    }

    [TestMethod]
    public async Task PersistedLights_AreProbedOnDiscover()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.StateJson = """{"Lights":[{"Id":"aabb","Ip":"10.0.0.9","Label":"Saved","Product":27}]}""";
        });

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");

        Assert.IsTrue(client.Probes.Contains("10.0.0.9"));
    }

    [TestMethod]
    public async Task Shutdown_DisposesSubscriptions()
    {
        var (plugin, host, client) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");

        Assert.AreEqual(0, client.DiscoverCalls);
    }

    [TestMethod]
    public void Settings_HaveNoBindIpAndIncludeLightSlots()
    {
        var plugin = new LifxPlugin(_ => new FakeLifxClient());

        CollectionAssert.DoesNotContain(plugin.Info.Settings.Select(x => x.Key).ToArray(), "bind-ip");
        Assert.IsTrue(plugin.Info.Settings.Any(x => x.Key == LifxPlugin.DiscoverNowKey));
        Assert.IsTrue(plugin.Info.Settings.Any(x => x.Key == LifxPlugin.LightSlotKey(1)));
        Assert.IsTrue(plugin.Info.Settings.Any(x => x.Key == LifxPlugin.FollowFixtureKey));
        Assert.IsTrue(plugin.Info.Settings.Any(x => x.Key == LifxPlugin.IdentifyKey));
        Assert.IsTrue(plugin.Info.Settings.Any(x => x.Key == LifxPlugin.FollowMasterKey));
        Assert.AreEqual(LifxPlugin.LightSlotCount, plugin.Info.Settings.Count(x => x.Key.StartsWith("light-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DiscoverNow_ScansAndMapsSlots()
    {
        var (_, host, client) = await CreateInitializedAsync();

        host.SetSetting(LifxPlugin.DiscoverNowKey, "true");
        await host.TriggerSettingsChangedAsync();
        await WaitForAsync(
            () => client.DiscoverCalls >= 1 && host.FiredTriggers.Contains(LifxPlugin.DiscoveredTrigger),
            "discovery did not finish");

        Assert.AreEqual(1, client.DiscoverCalls);
        Assert.AreEqual(LifxPlugin.DiscoveredTrigger, host.FiredTriggers.Single());
    }

    [TestMethod]
    public async Task LightSlotToggle_SendsTestColour()
    {
        var (_, host, client) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");
        client.Reset();

        host.SetSetting(LifxPlugin.LightSlotKey(1), "true");
        await host.TriggerSettingsChangedAsync();

        Assert.AreEqual(1, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].R, 1e-9);
        Assert.AreEqual(80 / 255.0, client.Colors[0].G, 1e-3);
        Assert.AreEqual(0.0, client.Colors[0].B, 1e-9);
    }

    [TestMethod]
    public async Task FadeChange_DoesNotRediscover()
    {
        var (_, host, client) = await CreateInitializedAsync();

        host.SetSetting(LifxPlugin.FadeMsKey, "90");
        await host.TriggerSettingsChangedAsync();

        Assert.AreEqual(0, client.DiscoverCalls);
    }

    [TestMethod]
    public async Task FollowFixture_AppliesEntityColour()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.SetSetting(LifxPlugin.FollowFixtureKey, "HOUSE");
        });

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = "fixture.HOUSE",
            Text = """{"red":1,"green":0,"blue":0,"intensity":0.5}""",
        });

        Assert.AreEqual(1, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].R, 1e-9);
        Assert.AreEqual(0.0, client.Colors[0].G, 1e-9);
        Assert.AreEqual(0.5, client.Colors[0].Brightness, 1e-9);
    }

    [TestMethod]
    public async Task FollowFixture_IgnoresOtherEntities()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.SetSetting(LifxPlugin.FollowFixtureKey, "HOUSE");
        });

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = "fixture.WASH",
            Text = """{"red":0,"green":1,"blue":0,"intensity":1}""",
        });

        Assert.AreEqual(0, client.Colors.Count);
    }

    [TestMethod]
    public async Task FollowFixture_EmptyDoesNothing()
    {
        var (_, host, client) = await CreateInitializedAsync();

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = "fixture.HOUSE",
            Text = """{"red":1,"green":0,"blue":0,"intensity":1}""",
        });

        Assert.AreEqual(0, client.Colors.Count);
    }

    [TestMethod]
    public async Task FollowFixture_ZeroIntensityPowersOff()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.SetSetting(LifxPlugin.FollowFixtureKey, "HOUSE");
        });

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = "HOUSE",
            Text = """{"red":1,"green":1,"blue":1,"intensity":0}""",
        });

        Assert.AreEqual((client.Lights[0].Id, false), client.Powers.Single());
        Assert.AreEqual(0, client.Colors.Count);
    }

    [TestMethod]
    public async Task CueEnded_BlackoutWhenEnabled()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.SetSetting(LifxPlugin.BlackoutOnCueEndKey, "true");
        });

        await host.SimulateCueEndedAsync("PARTY");

        Assert.AreEqual((client.Lights[0].Id, false), client.Powers.Single());
    }

    [TestMethod]
    public async Task CueEnded_DoesNothingWhenBlackoutOff()
    {
        var (_, host, client) = await CreateInitializedAsync();

        await host.SimulateCueEndedAsync("PARTY");

        Assert.AreEqual(0, client.Powers.Count);
        Assert.AreEqual(0, client.Colors.Count);
    }

    [TestMethod]
    public async Task FollowMaster_ScalesLastColour()
    {
        var (_, host, client) = await CreateInitializedAsync((h, _) =>
        {
            h.SetSetting(LifxPlugin.FollowMasterKey, "true");
        });

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "color all 255 0 0");
        Assert.AreEqual(1.0, client.Colors.Single().Brightness, 1e-9);

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = LifxPlugin.MasterDimmerCode,
            Level = 0.4,
        });

        Assert.AreEqual(2, client.Colors.Count);
        Assert.AreEqual(0.4, client.Colors[1].Brightness, 1e-9);
    }

    [TestMethod]
    public async Task MasterDimmer_IgnoredWhenFollowOff()
    {
        var (_, host, client) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "color all 255 0 0");

        await host.SimulateEntityStateAsync(new PluginEntityState
        {
            Code = LifxPlugin.MasterDimmerCode,
            Level = 0.2,
        });

        Assert.AreEqual(1, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].Brightness, 1e-9);
    }

    [TestMethod]
    public async Task Identify_PaintsDistinctHues()
    {
        var (_, host, client) = await CreateInitializedAsync((_, fake) =>
        {
            fake.AddLight(new LifxLight([8, 7, 6, 5, 4, 3, 2, 1], "192.168.1.11", "Bar")
            {
                Power = 65535,
            });
        });

        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "identify");

        Assert.AreEqual(2, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].R, 1e-9);
        Assert.AreEqual(0.0, client.Colors[0].G, 1e-9);
        Assert.AreNotEqual(client.Colors[0].R, client.Colors[1].R, 1e-9);
    }

    [TestMethod]
    public async Task IdentifyToggle_UsesSettingsPage()
    {
        var (_, host, client) = await CreateInitializedAsync();

        host.SetSetting(LifxPlugin.IdentifyKey, "true");
        await host.TriggerSettingsChangedAsync();

        Assert.AreEqual(1, client.Colors.Count);
        Assert.AreEqual(1.0, client.Colors[0].R, 1e-9);
    }

    [TestMethod]
    public async Task ChaseToggle_StartsEffect()
    {
        var (_, host, client) = await CreateInitializedAsync();

        host.SetSetting(LifxPlugin.ChaseKey, "true");
        await host.TriggerSettingsChangedAsync();
        await WaitForAsync(() => client.Colors.Count > 0, "chase did not start");

        Assert.IsTrue(client.Colors.Count > 0);
    }
}
