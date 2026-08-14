using System.Text.Json;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.LIFX.Tests;

[TestClass]
public class LifxPluginTests
{
    private static async Task<(LifxPlugin Plugin, TestPluginHost Host, FakeLifxClient Client)> CreateInitializedAsync(
        Action<TestPluginHost, FakeLifxClient>? configure = null)
    {
        var client = new FakeLifxClient();
        var kitchen = new LifxLight([1, 2, 3, 4, 5, 6, 7, 8], "192.168.1.10", "Kitchen")
        {
            ModelName = "LIFX A19",
            Product = 27,
            Power = 65535,
        };
        client.Lights.Add(kitchen);

        var plugin = new LifxPlugin(_ => client);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting(LifxPlugin.AutoDiscoverKey, "false");
        configure?.Invoke(host, client);

        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, client);
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

        CollectionAssert.Contains(client.Probes, "10.0.0.9");
    }

    [TestMethod]
    public async Task Shutdown_DisposesSubscriptions()
    {
        var (plugin, host, client) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);
        await host.SimulateMqttMessageAsync(LifxStatus.CommandTopic("test-serial"), "discover");

        Assert.AreEqual(0, client.DiscoverCalls);
    }
}
