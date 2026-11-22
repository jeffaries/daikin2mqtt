using System.Text.Json.Nodes;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Core.State;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Daikin.Mapping;
using Daikin2Mqtt.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Tests;

public class BridgeWorkerTests : IAsyncLifetime
{
    private readonly FakeMqttPublisher _mqtt = new();
    private readonly FakeDaikinClient _salon = new(new DaikinUnitOptions { Id = "salon", Host = "192.168.1.60", Name = "Clim salon", Area = "Salon" });
    private readonly FakeDaikinClient _chambre = new(new DaikinUnitOptions { Id = "chambre", Host = "192.168.1.61", Name = "Clim chambre" });
    private readonly BridgeWorker _worker;

    public BridgeWorkerTests()
    {
        var topics = new TopicBuilder("daikin");
        var entityBuilder = new UnitEntityBuilder(
            topics, Options.Create(new DeviceOptions()), Options.Create(new DaikinOptions()), Options.Create(new EntitiesOptions()),
            NullLogger<UnitEntityBuilder>.Instance);

        _worker = new BridgeWorker(
            new DaikinUnitSet(new IDaikinClient[] { _salon, _chambre }),
            _mqtt,
            topics,
            new AvailabilityManager(_mqtt, topics),
            new DiscoveryBuilder(),
            new DiscoveryPublisher(_mqtt, Options.Create(new DiscoveryOptions()), NullLogger<DiscoveryPublisher>.Instance),
            new StatePublisher(_mqtt),
            entityBuilder,
            Options.Create(new DeviceOptions()),
            NullLogger<BridgeWorker>.Instance);
    }

    public async Task InitializeAsync() => await _worker.StartAsync(CancellationToken.None);

    public async Task DisposeAsync() => await _worker.StopAsync(CancellationToken.None);

    private async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        var published = string.Join(" | ", _mqtt.Published.ToArray().TakeLast(15).Select(m => $"{m.Topic}={(m.Payload.Length > 20 ? m.Payload[..20] : m.Payload)}"));
        Assert.True(condition(), "Condition not reached in time. Last published: " + published);
    }

    private bool Has(string topic, string payload) => _mqtt.Published.ToArray().Any(m => m.Topic == topic && m.Payload == payload);

    private string? Last(string topic) => _mqtt.Published.ToArray().LastOrDefault(m => m.Topic == topic)?.Payload;

    private IEnumerable<string> Cleared() => _mqtt.Published.ToArray().Where(m => m.Payload.Length == 0).Select(m => m.Topic);

    [Fact]
    public async Task Start_SubscribesToHomeAssistantStatusAndPublishesTheBridgeAvailability()
    {
        await WaitUntil(() => Has("daikin/status", "online"));

        Assert.Contains("homeassistant/status", _mqtt.Subscriptions);
    }

    [Fact]
    public async Task AUnit_IsPublishedOnlyOnceItHasAnswered()
    {
        await WaitUntil(() => Has("daikin/status", "online"));
        await Task.Delay(100);
        Assert.DoesNotContain(_mqtt.Published.ToArray(), m => m.Topic.StartsWith("homeassistant/device/"));

        _salon.Connect();
        await WaitUntil(() => _mqtt.Published.Any(m => m.Topic == "homeassistant/device/daikin_salon/config"));

        Assert.DoesNotContain(_mqtt.Published.ToArray(), m => m.Topic == "homeassistant/device/daikin_chambre/config");
        Assert.Contains("daikin/salon/climate/mode/set", _mqtt.Subscriptions);
        Assert.Contains("daikin/salon/climate/temperature/set", _mqtt.Subscriptions);
        Assert.DoesNotContain("daikin/chambre/climate/mode/set", _mqtt.Subscriptions);
    }

    [Fact]
    public async Task TheDeviceOfAUnit_DescribesItAndTracksTheAvailabilityOfTheBridgeAndOfTheUnit()
    {
        _salon.Connect();
        await WaitUntil(() => _mqtt.Published.Any(m => m.Topic == "homeassistant/device/daikin_salon/config"));

        var json = JsonNode.Parse(_mqtt.Published.ToArray().Last(m => m.Topic == "homeassistant/device/daikin_salon/config").Payload)!;
        var dev = json["dev"]!;
        Assert.Equal("daikin_salon", (string?)dev["ids"]);
        Assert.Equal("Clim salon", (string?)dev["name"]);
        Assert.Equal("Daikin", (string?)dev["mf"]);
        Assert.Equal("0B66 (N)", (string?)dev["mdl"]);
        Assert.Equal("1_14_88", (string?)dev["sw"]);
        Assert.Equal("Salon", (string?)dev["sa"]);
        Assert.Equal("mac", (string?)dev["cns"]![0]![0]);
        Assert.Equal("all", (string?)json["avty_mode"]);
        Assert.Equal("daikin/status", (string?)json["avty"]![0]!["topic"]);
        Assert.Equal("daikin/salon/status", (string?)json["avty"]![1]!["topic"]);
        Assert.Equal("climate", (string?)json["cmps"]!["climate"]!["p"]);
        Assert.NotNull(json["cmps"]!["outdoor_temperature"]);
    }

    [Fact]
    public async Task TheStatesOfAUnit_ArePublishedOnlyWhenTheyChange()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off"));
        Assert.True(Has("daikin/salon/climate/temperature/state", "24.0"));
        Assert.True(Has("daikin/salon/outdoor_temperature/state", "12.0"));
        Assert.True(Has("daikin/salon/compressor_frequency/state", "0"));
        Assert.True(Has("daikin/salon/energy_today/state", "1.6"));
        Assert.All(_mqtt.Published.ToArray().Where(m => m.Topic.EndsWith("/state")), m => Assert.True(m.Retain));

        _salon.Update(control: c => c["pow"] = "1");
        _salon.Update(control: c => c["pow"] = "1");
        _salon.Update(control: c => c["pow"] = "1", sensor: s => s["otemp"] = "13.5");

        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "cool") && Has("daikin/salon/outdoor_temperature/state", "13.5"));
        var modes = _mqtt.Published.ToArray().Where(m => m.Topic == "daikin/salon/climate/mode/state").Select(m => m.Payload);
        Assert.Equal(new[] { "off", "cool" }, modes);
    }

    [Fact]
    public async Task APollBeforeTheInfo_IsPublishedOnceTheEntitiesExist()
    {
        // The client reported a state, then the identity: the state must not be lost.
        _salon.Update(control: c => c["stemp"] = "19.5");
        await Task.Delay(50);
        Assert.DoesNotContain(_mqtt.Published.ToArray(), m => m.Topic.StartsWith("daikin/salon/") && !m.Topic.EndsWith("/status"));

        _salon.Connect();

        await WaitUntil(() => Has("daikin/salon/climate/temperature/state", "19.5"));
    }

    [Fact]
    public async Task ACommand_IsSentToTheRightUnitAndTheStateIsPublishedWhenTheUnitConfirms()
    {
        _salon.Connect();
        _chambre.Connect();
        await WaitUntil(() => Has("daikin/chambre/climate/mode/state", "off"));

        await _mqtt.ReceiveAsync("daikin/chambre/climate/mode/set", "heat");
        await WaitUntil(() => _chambre.Commands.Count == 1);

        Assert.Empty(_salon.Commands);
        Assert.Equal(true, _chambre.Commands[0].Power);
        Assert.Equal("4", _chambre.Commands[0].Mode);
        await WaitUntil(() => Has("daikin/chambre/climate/mode/state", "heat"));
        Assert.Equal("off", Last("daikin/salon/climate/mode/state"));
    }

    [Fact]
    public async Task TemperatureFanAndSwingCommands_AreTranslated()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off"));

        await _mqtt.ReceiveAsync("daikin/salon/climate/temperature/set", "21.3");
        await _mqtt.ReceiveAsync("daikin/salon/climate/fan_mode/set", "quiet");
        await _mqtt.ReceiveAsync("daikin/salon/climate/swing_mode/set", "both");
        await WaitUntil(() => _salon.Commands.Count == 3);

        Assert.Contains(_salon.Commands, c => c.Temperature == 21.5);
        Assert.Contains(_salon.Commands, c => c.FanRate == "B");
        Assert.Contains(_salon.Commands, c => c.SwingDirection == "3");
        await WaitUntil(() => Has("daikin/salon/climate/temperature/state", "21.5") && Has("daikin/salon/climate/fan_mode/state", "quiet")
                              && Has("daikin/salon/climate/swing_mode/state", "both"));
    }

    [Fact]
    public async Task TheHolidaySwitch_SendsACommand()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/holiday_mode/state", "OFF"));

        await _mqtt.ReceiveAsync("daikin/salon/holiday_mode/set", "ON");

        await WaitUntil(() => Has("daikin/salon/holiday_mode/state", "ON"));
        Assert.True(_salon.Commands.Single().Holiday);
    }

    [Fact]
    public async Task UnsupportedCommandPayload_IsNotSent()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off"));

        await _mqtt.ReceiveAsync("daikin/salon/climate/mode/set", "BANANA");
        await _mqtt.ReceiveAsync("daikin/salon/climate/temperature/set", "warm");
        await Task.Delay(100);

        Assert.Empty(_salon.Commands);
    }

    [Fact]
    public async Task ACommandThatFails_DoesNotStopTheBridge()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off"));
        _salon.SendFailure = new HttpRequestException("unit unreachable");

        await _mqtt.ReceiveAsync("daikin/salon/climate/mode/set", "cool");
        await Task.Delay(100);
        Assert.Equal("off", Last("daikin/salon/climate/mode/state")); // nothing confirmed

        _salon.SendFailure = null;
        await _mqtt.ReceiveAsync("daikin/salon/climate/mode/set", "cool");
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "cool"));
    }

    [Fact]
    public async Task Availability_FollowsEachUnit()
    {
        // At startup the units are not available yet: the retained value of a previous run is replaced.
        await WaitUntil(() => Has("daikin/salon/status", "offline") && Has("daikin/chambre/status", "offline"));

        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/status", "online"));
        Assert.Equal("offline", Last("daikin/chambre/status"));

        _salon.SetAvailable(false);
        await WaitUntil(() => Last("daikin/salon/status") == "offline");
        Assert.Equal("online", Last("daikin/status")); // the bridge itself is fine
    }

    [Fact]
    public async Task AUnitThatHasNotAnsweredYet_KeepsItsRetainedDiscovery()
    {
        // Retained devices left by a previous run: salon (down now), chambre (answers) and a unit that was removed.
        await _mqtt.ReceiveAsync("homeassistant/device/daikin_salon/config", "{}");
        await _mqtt.ReceiveAsync("homeassistant/device/daikin_salon_area_terrasse/config", "{}");
        await _mqtt.ReceiveAsync("homeassistant/device/daikin_cave/config", "{}");
        await _mqtt.ReceiveAsync("homeassistant/device/other_thing/config", "{}");

        _chambre.Connect();
        await WaitUntil(() => _mqtt.Published.Any(m => m.Topic == "homeassistant/device/daikin_chambre/config"));
        await WaitUntil(() => Cleared().Contains("homeassistant/device/daikin_cave/config"));

        Assert.DoesNotContain("homeassistant/device/daikin_salon/config", Cleared());
        Assert.DoesNotContain("homeassistant/device/daikin_salon_area_terrasse/config", Cleared());
        Assert.DoesNotContain("homeassistant/device/other_thing/config", Cleared());
        Assert.DoesNotContain("homeassistant/device/daikin_chambre/config", Cleared());
    }

    [Fact]
    public async Task MqttReconnection_RepublishesDiscoveryAvailabilityAndStates()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/status", "online") && Has("daikin/salon/climate/mode/state", "off"));
        _mqtt.Published.Clear();

        await _mqtt.RaiseConnectedAsync();

        await WaitUntil(() =>
            _mqtt.Published.ToArray().Any(m => m.Topic == "homeassistant/device/daikin_salon/config") &&
            Has("daikin/status", "online") &&
            Has("daikin/salon/status", "online") &&
            Has("daikin/chambre/status", "offline") &&
            Has("daikin/salon/climate/mode/state", "off"));
    }

    [Fact]
    public async Task HomeAssistantRestart_RepublishesStatesAfterDiscovery()
    {
        _salon.Connect();
        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off"));
        _mqtt.Published.Clear();

        await _mqtt.ReceiveAsync("homeassistant/status", "online");

        await WaitUntil(() => Has("daikin/salon/climate/mode/state", "off") &&
                              _mqtt.Published.ToArray().Any(m => m.Topic == "homeassistant/device/daikin_salon/config"));
    }

    [Fact]
    public async Task Stop_PublishesOffline()
    {
        await WaitUntil(() => Has("daikin/status", "online"));

        await _worker.StopAsync(CancellationToken.None);

        Assert.Equal("offline", Last("daikin/status"));
    }
}
