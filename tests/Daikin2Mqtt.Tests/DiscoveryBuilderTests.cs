using System.Text.Json.Nodes;
using Daikin2Mqtt.Core.Discovery;

namespace Daikin2Mqtt.Tests;

public class DiscoveryBuilderTests
{
    private static readonly DeviceInfo Device = new("daikin", "Daikin", "Daikin", "Daikin", "api 1.6");
    private static readonly OriginInfo Origin = new("daikin2mqtt", "1.0.0");

    private static List<ComponentDescriptor> Components() => new()
    {
        new ComponentDescriptor
        {
            Key = "relay_1",
            Platform = "switch",
            UniqueId = "daikin_relay_1",
            Name = "Garage light",
            StateTopic = "daikin/relay/1/state",
            CommandTopic = "daikin/relay/1/set",
        },
        new ComponentDescriptor
        {
            Key = "analog_1",
            Platform = "sensor",
            UniqueId = "daikin_analog_1",
            Name = "Cellar temperature",
            StateTopic = "daikin/analog/1/state",
            UnitOfMeasurement = "°C",
            DeviceClass = "temperature",
        },
    };

    [Fact]
    public void BuildAll_GroupsComponentsOfAnAreaInTheirOwnDevice()
    {
        var components = Components();
        components[0].Area = "Séjour & Cuisine";

        var messages = new DiscoveryBuilder().BuildAll("daikin", Device, Origin, "daikin/status", components);

        Assert.Equal(2, messages.Count);
        var main = JsonNode.Parse(messages[0].Payload)!;
        Assert.Equal("homeassistant/device/daikin/config", messages[0].Topic);
        Assert.Equal(new[] { "analog_1" }, main["cmps"]!.AsObject().Select(c => c.Key));

        var area = JsonNode.Parse(messages[1].Payload)!;
        Assert.Equal("homeassistant/device/daikin_area_sejour_cuisine/config", messages[1].Topic);
        Assert.Equal("Séjour & Cuisine", (string?)area["dev"]!["sa"]);
        Assert.Equal("daikin", (string?)area["dev"]!["via_device"]);
        Assert.Equal("daikin_area_sejour_cuisine", (string?)area["dev"]!["ids"]);
        Assert.Equal("Daikin - Séjour & Cuisine", (string?)area["dev"]!["name"]);
        Assert.Equal(new[] { "relay_1" }, area["cmps"]!.AsObject().Select(c => c.Key));
        Assert.StartsWith(new DiscoveryBuilder().AreaTopicPrefix("daikin"), messages[1].Topic);
    }

    [Fact]
    public void BuildAll_WithoutAreaIsTheSingleMainDevice()
    {
        var messages = new DiscoveryBuilder().BuildAll("daikin", Device, Origin, "s", Components());

        Assert.Equal(new DiscoveryBuilder().Build("daikin", Device, Origin, "s", Components()), messages.Single());
    }

    [Fact]
    public void BuildAll_ClearsTheMainDeviceWhenEveryComponentHasAnArea()
    {
        var components = Components();
        components.ForEach(c => c.Area = "Cave");

        var messages = new DiscoveryBuilder().BuildAll("daikin", Device, Origin, "s", components);

        Assert.Equal(string.Empty, messages[0].Payload);
        Assert.Equal("homeassistant/device/daikin/config", messages[0].Topic);
        Assert.Equal(2, JsonNode.Parse(messages[1].Payload)!["cmps"]!.AsObject().Count);
    }

    [Fact]
    public void Build_UsesDeviceDiscoveryTopic()
    {
        var message = new DiscoveryBuilder().Build("daikin", Device, Origin, "daikin/status", Components());

        Assert.Equal("homeassistant/device/daikin/config", message.Topic);
    }

    [Fact]
    public void Build_UsesCustomPrefix()
    {
        var message = new DiscoveryBuilder("ha").Build("daikin", Device, Origin, "daikin/status", Components());

        Assert.Equal("ha/device/daikin/config", message.Topic);
    }

    [Fact]
    public void Build_WritesDeviceAndOrigin()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("daikin", Device, Origin, "daikin/status", Components()).Payload)!;

        Assert.Equal("daikin", (string?)json["dev"]!["ids"]);
        Assert.Equal("Daikin", (string?)json["dev"]!["name"]);
        Assert.Equal("Daikin", (string?)json["dev"]!["mf"]);
        Assert.Equal("Daikin", (string?)json["dev"]!["mdl"]);
        Assert.Equal("api 1.6", (string?)json["dev"]!["sw"]);
        Assert.Equal("daikin2mqtt", (string?)json["o"]!["name"]);
        Assert.Equal("1.0.0", (string?)json["o"]!["sw"]);
    }

    [Fact]
    public void Build_KeepsTopicsOnEachComponentNotOnRoot()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("daikin", Device, Origin, "daikin/status", Components()).Payload)!;

        Assert.Null(json["state_topic"]);
        Assert.Null(json["command_topic"]);
        Assert.Equal("daikin/relay/1/state", (string?)json["cmps"]!["relay_1"]!["state_topic"]);
        Assert.Equal("daikin/relay/1/set", (string?)json["cmps"]!["relay_1"]!["command_topic"]);
        Assert.Equal("daikin/analog/1/state", (string?)json["cmps"]!["analog_1"]!["state_topic"]);
        Assert.Null(json["cmps"]!["analog_1"]!["command_topic"]);
    }

    [Fact]
    public void Build_WritesPlatformUniqueIdNameAndSensorAttributes()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("daikin", Device, Origin, "daikin/status", Components()).Payload)!;

        var relay = json["cmps"]!["relay_1"]!;
        Assert.Equal("switch", (string?)relay["p"]);
        Assert.Equal("daikin_relay_1", (string?)relay["unique_id"]);
        Assert.Equal("Garage light", (string?)relay["name"]);

        var sensor = json["cmps"]!["analog_1"]!;
        Assert.Equal("sensor", (string?)sensor["p"]);
        Assert.Equal("°C", (string?)sensor["unit_of_measurement"]);
        Assert.Equal("temperature", (string?)sensor["device_class"]);
    }

    [Fact]
    public void Build_SharesAvailabilityAtRootLevel()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("daikin", Device, Origin, "daikin/status", Components()).Payload)!;

        Assert.Equal("daikin/status", (string?)json["avty_t"]);
    }

    [Fact]
    public void Build_WritesExtraKeys()
    {
        var light = new ComponentDescriptor
        {
            Key = "dimmer_1",
            Platform = "light",
            UniqueId = "daikin_dimmer_1",
            Name = "Dimmer",
            Extra = new Dictionary<string, object?> { ["brightness_scale"] = 100, ["brightness_command_topic"] = "t/set", ["ignored"] = null },
        };

        var json = JsonNode.Parse(new DiscoveryBuilder().Build("daikin", Device, Origin, "s", new[] { light }).Payload)!;

        Assert.Equal(100, (int?)json["cmps"]!["dimmer_1"]!["brightness_scale"]);
        Assert.Equal("t/set", (string?)json["cmps"]!["dimmer_1"]!["brightness_command_topic"]);
        Assert.Null(json["cmps"]!["dimmer_1"]!["ignored"]);
    }

    [Fact]
    public void BuildRemoval_IsEmptyPayloadOnConfigTopic()
    {
        var removal = new DiscoveryBuilder().BuildRemoval("daikin");

        Assert.Equal("homeassistant/device/daikin/config", removal.Topic);
        Assert.Equal(string.Empty, removal.Payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad node")]
    [InlineData("a/b")]
    [InlineData("é")]
    public void Build_RejectsInvalidNodeId(string nodeId)
    {
        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build(nodeId, Device, Origin, "s", Components()));
    }

    [Fact]
    public void Build_RejectsDuplicateKeysAndEmptyList()
    {
        var duplicate = Components();
        duplicate.Add(duplicate[0]);

        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build("daikin", Device, Origin, "s", duplicate));
        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build("daikin", Device, Origin, "s", Array.Empty<ComponentDescriptor>()));
    }
}
