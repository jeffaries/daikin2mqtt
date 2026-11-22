using System.Text.Json.Nodes;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Tests;

/// <summary>Generic additions made to Core for daikin2mqtt (several availability topics, MAC connections, kept topics).</summary>
public class CoreExtensionsTests
{
    private static readonly OriginInfo Origin = new("daikin2mqtt", "1.0.0");

    private static ComponentDescriptor Sensor(string area = "") => new()
    {
        Key = "s",
        Platform = "sensor",
        UniqueId = "daikin_s",
        Name = "S",
        StateTopic = "daikin/s",
        Area = string.IsNullOrEmpty(area) ? null : area,
    };

    [Fact]
    public void Build_WithAdditionalAvailabilityTopics_UsesAnAllModeList()
    {
        var message = new DiscoveryBuilder().Build(
            "daikin_salon", new DeviceInfo("daikin_salon", "Salon"), Origin, "daikin/status", new[] { Sensor() },
            new[] { "daikin/salon/status" });
        var json = JsonNode.Parse(message.Payload)!;

        Assert.Null(json["avty_t"]);
        Assert.Equal("all", (string?)json["avty_mode"]);
        Assert.Equal("daikin/status", (string?)json["avty"]![0]!["topic"]);
        Assert.Equal("daikin/salon/status", (string?)json["avty"]![1]!["topic"]);
    }

    [Fact]
    public void Build_WithoutAdditionalAvailabilityTopics_KeepsTheSingleTopic()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build(
            "n", new DeviceInfo("n", "N"), Origin, "daikin/status", new[] { Sensor() }).Payload)!;

        Assert.Equal("daikin/status", (string?)json["avty_t"]);
        Assert.Null(json["avty"]);
    }

    [Fact]
    public void Build_WritesDeviceConnections_ButNotOnAreaDevices()
    {
        var device = new DeviceInfo("n", "N", Connections: new[] { ("mac", "aa:bb:cc:dd:ee:ff") });
        var messages = new DiscoveryBuilder().BuildAll("n", device, Origin, "s", new[] { Sensor(), Sensor("Salon") }.Select((c, i) =>
        {
            return new ComponentDescriptor { Key = "k" + i, Platform = "sensor", UniqueId = "u" + i, Name = "N" + i, StateTopic = "t" + i, Area = c.Area };
        }));

        var main = JsonNode.Parse(messages[0].Payload)!;
        var area = JsonNode.Parse(messages[1].Payload)!;
        Assert.Equal("mac", (string?)main["dev"]!["cns"]![0]![0]);
        Assert.Equal("aa:bb:cc:dd:ee:ff", (string?)main["dev"]!["cns"]![0]![1]);
        Assert.Null(area["dev"]!["cns"]);
    }

    [Fact]
    public async Task Publish_NeverClearsKeptTopics()
    {
        var mqtt = new FakeMqttPublisher();
        var publisher = new DiscoveryPublisher(mqtt, Options.Create(new DiscoveryOptions()), NullLogger<DiscoveryPublisher>.Instance)
        {
            StaleTopicPrefix = "homeassistant/device/daikin_",
            KeepTopic = topic => topic.Contains("daikin_cave"),
        };
        await publisher.StartAsync(CancellationToken.None);

        await mqtt.ReceiveAsync("homeassistant/device/daikin_cave/config", "{}");
        await mqtt.ReceiveAsync("homeassistant/device/daikin_old/config", "{}");
        await publisher.PublishAsync(new DiscoveryMessage("homeassistant/device/daikin_salon/config", "{}"));

        var cleared = mqtt.Published.Where(m => m.Payload.Length == 0).Select(m => m.Topic).ToArray();
        Assert.Equal(new[] { "homeassistant/device/daikin_old/config" }, cleared);
    }
}
