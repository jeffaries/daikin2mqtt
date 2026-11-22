using System.Text.Json.Nodes;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Daikin.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Tests;

public class UnitEntityBuilderTests
{
    private static readonly DaikinUnitOptions Salon = new() { Id = "salon", Host = "192.168.1.60", Name = "Clim salon", Area = "Salon" };

    private static UnitEntityBuilder Builder(DaikinOptions? options = null, EntitiesOptions? entities = null) => new(
        new TopicBuilder("daikin"),
        Options.Create(new DeviceOptions()),
        Options.Create(options ?? new DaikinOptions()),
        Options.Create(entities ?? new EntitiesOptions()),
        NullLogger<UnitEntityBuilder>.Instance);

    private static DaikinUnitInfo Info(
        DaikinCapabilities? caps = null, bool energy = true, bool holiday = true, DaikinUnitOptions? unit = null) => new(
        unit ?? Salon, "Daikin", "A1B2C3D4E5F6", "1_14_88",
        caps ?? DaikinCapabilities.Parse(new Dictionary<string, string>
        {
            ["model"] = "0B66", ["type"] = "N", ["en_frate"] = "1", ["s_fdir"] = "3", ["temp"] = "1", ["s_humd"] = "1", ["en_spmode"] = "1",
        }),
        energy, holiday);

    private static DaikinSnapshot Snapshot(
        Dictionary<string, string>? control = null, Dictionary<string, string>? sensor = null,
        Dictionary<string, string>? basic = null, DaikinEnergy? energy = null) => new(
        DateTimeOffset.UtcNow,
        control ?? new() { ["pow"] = "1", ["mode"] = "3", ["stemp"] = "22.5", ["f_rate"] = "B", ["f_dir"] = "1", ["adv"] = "", ["alert"] = "255" },
        sensor ?? new() { ["htemp"] = "24.0", ["hhum"] = "45", ["otemp"] = "15.5", ["err"] = "0", ["cmpfreq"] = "999" },
        basic ?? new() { ["ver"] = "1_14_88", ["radio1"] = "-51", ["en_hol"] = "0" },
        energy);

    private static MappedEntity Entity(IReadOnlyList<MappedEntity> entities, string key) => entities.Single(e => e.Key == key);

    private static string? State(MappedEntity entity, DaikinSnapshot snapshot, string topicEndsWith) =>
        entity.States.Single(s => s.Topic.EndsWith(topicEndsWith, StringComparison.Ordinal)).ToPayload(snapshot);

    private static JsonNode Discovery(IReadOnlyList<MappedEntity> entities, string key)
    {
        var message = new DiscoveryBuilder().Build(
            "daikin_salon", new DeviceInfo("daikin_salon", "Clim salon"), new OriginInfo("daikin2mqtt"), "daikin/status",
            entities.Select(e => e.Component));
        return JsonNode.Parse(message.Payload)!["cmps"]![key]!;
    }

    // ---- climate -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Climate_DiscoveryDescribesTheUnit()
    {
        var entities = Builder().Build(Info());
        var climate = Discovery(entities, "climate");

        Assert.Equal("climate", (string?)climate["p"]);
        Assert.Equal("daikin_salon_climate", (string?)climate["unique_id"]);
        Assert.Equal(new[] { "off", "heat_cool", "dry", "cool", "heat", "fan_only" }, climate["modes"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(new[] { "auto", "quiet", "1", "2", "3", "4", "5" }, climate["fan_modes"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(new[] { "off", "vertical", "horizontal", "both" }, climate["swing_modes"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(10, (double)climate["min_temp"]!);
        Assert.Equal(32, (double)climate["max_temp"]!);
        Assert.Equal(0.5, (double)climate["temp_step"]!);
        Assert.Equal("C", (string?)climate["temperature_unit"]);
        Assert.Equal("daikin/salon/climate/mode/set", (string?)climate["mode_command_topic"]);
        Assert.Equal("daikin/salon/climate/mode/state", (string?)climate["mode_state_topic"]);
        Assert.Equal("daikin/salon/climate/temperature/set", (string?)climate["temperature_command_topic"]);
        Assert.Equal("daikin/salon/climate/temperature/state", (string?)climate["temperature_state_topic"]);
        Assert.Equal("daikin/salon/climate/current_temperature/state", (string?)climate["current_temperature_topic"]);
        Assert.Equal("daikin/salon/climate/fan_mode/set", (string?)climate["fan_mode_command_topic"]);
        Assert.Equal("daikin/salon/climate/swing_mode/state", (string?)climate["swing_mode_state_topic"]);
        // a climate has no generic state / command topic
        Assert.Null(climate["state_topic"]);
        Assert.Null(climate["command_topic"]);
    }

    [Fact]
    public void Climate_TemperatureBoundsComeFromTheUnitOptions()
    {
        var unit = new DaikinUnitOptions { Id = "salon", Host = "h", MinTemperature = 16, MaxTemperature = 30 };
        var caps = DaikinCapabilities.Parse(new Dictionary<string, string>(), unit);

        var climate = Discovery(Builder().Build(Info(caps, unit: unit)), "climate");

        Assert.Equal(16, (double)climate["min_temp"]!);
        Assert.Equal(30, (double)climate["max_temp"]!);
    }

    [Fact]
    public void Climate_OnlyOffersWhatTheUnitSupports()
    {
        var caps = DaikinCapabilities.Parse(new Dictionary<string, string> { ["en_frate"] = "0", ["s_fdir"] = "0", ["temp"] = "0" });
        var entities = Builder().Build(Info(caps));
        var climate = Discovery(entities, "climate");

        Assert.Null(climate["fan_modes"]);
        Assert.Null(climate["fan_mode_command_topic"]);
        Assert.Null(climate["swing_modes"]);
        Assert.Null(climate["current_temperature_topic"]);
        Assert.Equal(3, Entity(entities, "climate").Commands.Count);
    }

    [Fact]
    public void Climate_PublishesTheStateOfTheUnit()
    {
        var climate = Entity(Builder().Build(Info()), "climate");
        var snapshot = Snapshot();

        Assert.Equal("cool", State(climate, snapshot, "/mode/state"));
        Assert.Equal("22.5", State(climate, snapshot, "/temperature/state"));
        Assert.Equal("24.0", State(climate, snapshot, "current_temperature/state"));
        Assert.Equal("quiet", State(climate, snapshot, "fan_mode/state"));
        Assert.Equal("vertical", State(climate, snapshot, "swing_mode/state"));
    }

    [Fact]
    public void Climate_ModeIsOffWhenPoweredOffAndHasNoTargetInDryMode()
    {
        var climate = Entity(Builder().Build(Info()), "climate");
        var off = Snapshot(new() { ["pow"] = "0", ["mode"] = "4", ["stemp"] = "21.0" });
        var dry = Snapshot(new() { ["pow"] = "1", ["mode"] = "2", ["stemp"] = "M" });

        Assert.Equal("off", State(climate, off, "/mode/state"));
        Assert.Equal("dry", State(climate, dry, "/mode/state"));
        Assert.Null(State(climate, dry, "/temperature/state"));
    }

    [Theory]
    [InlineData("off", false, null)]
    [InlineData("cool", true, "3")]
    [InlineData("heat", true, "4")]
    [InlineData("heat_cool", true, "1")]
    [InlineData("dry", true, "2")]
    [InlineData("fan_only", true, "6")]
    public void Climate_ModeCommands(string payload, bool power, string? mode)
    {
        var command = Entity(Builder().Build(Info()), "climate").Commands.Single(c => c.Topic.EndsWith("/mode/set")).Parse(payload)!;

        Assert.Equal(power, command.Power);
        Assert.Equal(mode, command.Mode);
        Assert.Null(command.Temperature);
    }

    [Theory]
    [InlineData("away", true)]
    [InlineData("none", false)]
    public void Climate_PresetCommandsDriveTheHolidayMode(string payload, bool holiday)
    {
        var command = Entity(Builder().Build(Info()), "climate").Commands.Single(c => c.Topic.EndsWith("preset_mode/set")).Parse(payload)!;

        Assert.Equal(holiday, command.Holiday);
        Assert.Null(Entity(Builder().Build(Info()), "climate").Commands.Single(c => c.Topic.EndsWith("preset_mode/set")).Parse("eco"));
    }

    [Fact]
    public void Climate_PresetModesDoNotListNone()
    {
        var presets = Discovery(Builder().Build(Info()), "climate")["preset_modes"]!.AsArray().Select(n => (string)n!);

        Assert.Equal(new[] { "away" }, presets);
    }

    [Fact]
    public void Climate_WithoutHolidaySupportHasNoPreset()
    {
        Assert.Null(Discovery(Builder().Build(Info(holiday: false)), "climate")["preset_modes"]);
    }

    [Theory]
    [InlineData("3", "off,vertical,horizontal,both")]
    [InlineData("1", "off,vertical")]
    [InlineData("2", "off,horizontal")]
    public void Climate_SwingModesFollowTheAxesOfTheUnit(string sFdir, string expected)
    {
        var caps = DaikinCapabilities.Parse(new Dictionary<string, string> { ["en_fdir"] = "1", ["s_fdir"] = sFdir });
        var climate = Discovery(Builder().Build(Info(caps)), "climate");

        Assert.Equal(expected, string.Join(",", climate["swing_modes"]!.AsArray().Select(n => (string)n!)));
    }

    [Fact]
    public void Capabilities_SwingIsOffOnlyWhenSFdirIsZero()
    {
        Assert.True(DaikinCapabilities.Parse(new Dictionary<string, string> { ["s_fdir"] = "3" }).Swing);
        Assert.False(DaikinCapabilities.Parse(new Dictionary<string, string> { ["s_fdir"] = "0" }).Swing);
    }

    [Fact]
    public void Climate_UnknownPayloadsAreRejected()
    {
        var climate = Entity(Builder().Build(Info()), "climate");

        Assert.Null(climate.Commands.Single(c => c.Topic.EndsWith("/mode/set")).Parse("auto"));
        Assert.Null(climate.Commands.Single(c => c.Topic.EndsWith("temperature/set")).Parse("warm"));
        Assert.Null(climate.Commands.Single(c => c.Topic.EndsWith("fan_mode/set")).Parse("turbo"));
        Assert.Null(climate.Commands.Single(c => c.Topic.EndsWith("swing_mode/set")).Parse("diagonal"));
    }

    [Theory]
    [InlineData("22", 22.0)]
    [InlineData("22.3", 22.5)]
    [InlineData("22.8", 23.0)]
    [InlineData("5", 10.0)]
    [InlineData("40", 32.0)]
    public void Climate_TemperatureCommandsUseHalfDegreeStepsWithinBounds(string payload, double expected)
    {
        var command = Entity(Builder().Build(Info()), "climate").Commands.Single(c => c.Topic.EndsWith("temperature/set")).Parse(payload)!;

        Assert.Equal(expected, command.Temperature);
    }

    [Fact]
    public void Climate_FanAndSwingCommands()
    {
        var climate = Entity(Builder().Build(Info()), "climate");

        Assert.Equal("B", climate.Commands.Single(c => c.Topic.EndsWith("fan_mode/set")).Parse("quiet")!.FanRate);
        Assert.Equal("7", climate.Commands.Single(c => c.Topic.EndsWith("fan_mode/set")).Parse("5")!.FanRate);
        Assert.Equal("3", climate.Commands.Single(c => c.Topic.EndsWith("swing_mode/set")).Parse("both")!.SwingDirection);
    }

    // ---- sensors -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Sensors_DiscoveryHasClassesUnitsAndTopics()
    {
        var entities = Builder().Build(Info());

        var indoor = Discovery(entities, "indoor_temperature");
        Assert.Equal("sensor", (string?)indoor["p"]);
        Assert.Equal("temperature", (string?)indoor["device_class"]);
        Assert.Equal("°C", (string?)indoor["unit_of_measurement"]);
        Assert.Equal("measurement", (string?)indoor["state_class"]);
        Assert.Equal("daikin/salon/indoor_temperature/state", (string?)indoor["state_topic"]);
        Assert.Equal("daikin_salon_indoor_temperature", (string?)indoor["unique_id"]);

        Assert.Equal("humidity", (string?)Discovery(entities, "indoor_humidity")["device_class"]);
        Assert.Equal("Hz", (string?)Discovery(entities, "compressor_frequency")["unit_of_measurement"]);
        var rssi = Discovery(entities, "wifi_rssi");
        Assert.Equal("signal_strength", (string?)rssi["device_class"]);
        Assert.Equal("diagnostic", (string?)rssi["entity_category"]);
        Assert.Equal("diagnostic", (string?)Discovery(entities, "firmware")["entity_category"]);
    }

    [Fact]
    public void Sensors_PublishTheValuesOfTheUnit()
    {
        var entities = Builder().Build(Info());
        var snapshot = Snapshot();

        Assert.Equal("24.0", State(Entity(entities, "indoor_temperature"), snapshot, "/state"));
        Assert.Equal("15.5", State(Entity(entities, "outdoor_temperature"), snapshot, "/state"));
        Assert.Equal("45", State(Entity(entities, "indoor_humidity"), snapshot, "/state"));
        Assert.Equal("0", State(Entity(entities, "compressor_frequency"), snapshot, "/state")); // 999 = idle
        Assert.Equal("0", State(Entity(entities, "error_code"), snapshot, "/state"));
        Assert.Equal("-51", State(Entity(entities, "wifi_rssi"), snapshot, "/state"));
        Assert.Equal("1_14_88", State(Entity(entities, "firmware"), snapshot, "/state"));
        Assert.Equal("a1:b2:c3:d4:e5:f6", State(Entity(entities, "mac"), snapshot, "/state"));
        Assert.Equal("192.168.1.60", State(Entity(entities, "ip"), snapshot, "/state"));
    }

    [Fact]
    public void Sensors_WithoutAValueArePublishedAsNothing()
    {
        var entities = Builder().Build(Info());
        var snapshot = Snapshot(sensor: new() { ["htemp"] = "-", ["hhum"] = "-", ["otemp"] = "--", ["cmpfreq"] = "-" });

        Assert.Null(State(Entity(entities, "indoor_temperature"), snapshot, "/state"));
        Assert.Null(State(Entity(entities, "outdoor_temperature"), snapshot, "/state"));
        Assert.Null(State(Entity(entities, "indoor_humidity"), snapshot, "/state"));
        Assert.Null(State(Entity(entities, "compressor_running"), snapshot, "/state"));
    }

    [Fact]
    public void Sensors_AreNotPublishedWhenTheUnitLacksThem()
    {
        var caps = DaikinCapabilities.Parse(new Dictionary<string, string> { ["temp"] = "0", ["s_humd"] = "0", ["en_spmode"] = "0" });
        var keys = Builder().Build(Info(caps)).Select(e => e.Key).ToList();

        Assert.DoesNotContain("indoor_temperature", keys);
        Assert.DoesNotContain("indoor_humidity", keys);
        Assert.DoesNotContain("special_mode", keys);
        Assert.Contains("outdoor_temperature", keys);
        Assert.Contains("compressor_frequency", keys);
    }

    [Fact]
    public void SpecialMode_IsAReadOnlyEnumSensor()
    {
        var entities = Builder().Build(Info());
        var special = Entity(entities, "special_mode");

        Assert.Empty(special.Commands);
        Assert.Equal("enum", (string?)Discovery(entities, "special_mode")["device_class"]);
        Assert.Equal("none", State(special, Snapshot(), "/state"));
        Assert.Equal("powerful", State(special, Snapshot(new() { ["adv"] = "13" }), "/state"));
        Assert.Equal("streamer", State(special, Snapshot(new() { ["adv"] = "2" }), "/state"));
    }

    [Fact]
    public void BinarySensors_ReportAlertsAndTheCompressor()
    {
        var entities = Builder().Build(Info());
        var alert = Entity(entities, "alert");
        var compressor = Entity(entities, "compressor_running");

        Assert.Equal("problem", (string?)Discovery(entities, "alert")["device_class"]);
        Assert.Equal("OFF", State(alert, Snapshot(), "/state"));
        Assert.Equal("ON", State(alert, Snapshot(new() { ["alert"] = "12" }), "/state"));
        Assert.Equal("ON", State(alert, Snapshot(sensor: new() { ["err"] = "17" }), "/state"));

        Assert.Equal("OFF", State(compressor, Snapshot(), "/state"));
        Assert.Equal("ON", State(compressor, Snapshot(sensor: new() { ["cmpfreq"] = "42" }), "/state"));
    }

    [Fact]
    public void HolidaySwitch_FollowsAndSetsTheHolidayMode()
    {
        var holiday = Entity(Builder().Build(Info()), "holiday_mode");

        Assert.Equal("switch", holiday.Component.Platform);
        Assert.Equal("OFF", State(holiday, Snapshot(), "/state"));
        Assert.Equal("ON", State(holiday, Snapshot(basic: new() { ["en_hol"] = "1" }), "/state"));
        Assert.True(holiday.Commands.Single().Parse("ON")!.Holiday);
        Assert.False(holiday.Commands.Single().Parse("off")!.Holiday);
        Assert.Null(holiday.Commands.Single().Parse("maybe"));
        Assert.DoesNotContain(Builder().Build(Info(holiday: false)), e => e.Key == "holiday_mode");
    }

    // ---- energy ------------------------------------------------------------------------------------------------------

    private static readonly DaikinEnergy SomeEnergy = new()
    {
        TodayRuntime = 146, TodayHeat = 0.5, TodayCool = 1.5, Week = 9.8, MonthHeat = 3, MonthCool = 20, YearHeat = 100.25, YearCool = 300,
    };

    [Fact]
    public void Energy_SensorsHaveEnergyClasses()
    {
        var entities = Builder().Build(Info());

        var today = Discovery(entities, "energy_today");
        Assert.Equal("energy", (string?)today["device_class"]);
        Assert.Equal("kWh", (string?)today["unit_of_measurement"]);
        Assert.Equal("total_increasing", (string?)today["state_class"]);
        Assert.Equal("total_increasing", (string?)Discovery(entities, "energy_year_heat")["state_class"]);
        // rolling window: no state class
        Assert.Null(Discovery(entities, "energy_week")["state_class"]);

        var runtime = Discovery(entities, "today_runtime");
        Assert.Equal("duration", (string?)runtime["device_class"]);
        Assert.Equal("min", (string?)runtime["unit_of_measurement"]);
    }

    [Fact]
    public void Energy_PublishesKwhAndSplitsHeatingFromCooling()
    {
        var entities = Builder().Build(Info());
        var snapshot = Snapshot(energy: SomeEnergy);

        Assert.Equal("146", State(Entity(entities, "today_runtime"), snapshot, "/state"));
        Assert.Equal("2.0", State(Entity(entities, "energy_today"), snapshot, "/state"));
        Assert.Equal("0.5", State(Entity(entities, "energy_today_heat"), snapshot, "/state"));
        Assert.Equal("1.5", State(Entity(entities, "energy_today_cool"), snapshot, "/state"));
        Assert.Equal("9.8", State(Entity(entities, "energy_week"), snapshot, "/state"));
        Assert.Equal("23.0", State(Entity(entities, "energy_month"), snapshot, "/state"));
        Assert.Equal("400.3", State(Entity(entities, "energy_year"), snapshot, "/state")); // 400.25 rounded
        Assert.Null(State(Entity(entities, "energy_today"), Snapshot(), "/state")); // energy not read yet
    }

    [Fact]
    public void Energy_IsNotPublishedWhenDisabledOrUnsupported()
    {
        var disabled = Builder(new DaikinOptions { EnableEnergy = false }).Build(Info());
        var unsupported = Builder().Build(Info(energy: false));

        Assert.DoesNotContain(disabled, e => e.Key.StartsWith("energy_") || e.Key == "today_runtime");
        Assert.DoesNotContain(unsupported, e => e.Key.StartsWith("energy_") || e.Key == "today_runtime");
    }

    // ---- topics, ids, customisation ----------------------------------------------------------------------------------

    [Fact]
    public void Entities_HaveUniqueKeysIdsAndTopicsBelowTheBaseTopic()
    {
        var entities = Builder().Build(Info());
        var topics = entities.SelectMany(e => e.States.Select(s => s.Topic).Concat(e.Commands.Select(c => c.Topic))).ToList();

        Assert.Equal(entities.Count, entities.Select(e => e.Key).Distinct().Count());
        Assert.Equal(entities.Count, entities.Select(e => e.Component.UniqueId).Distinct().Count());
        Assert.Equal(topics.Count, topics.Distinct().Count());
        Assert.All(topics, t => Assert.StartsWith("daikin/salon/", t));
        Assert.All(entities, e => Assert.All(e.Component.Key, _ => { }));
        Assert.All(entities.Select(e => e.Key), key => Assert.Contains(key, UnitEntityBuilder.KnownKeys));
    }

    [Fact]
    public void EveryKnownKeyIsPublishedForAFullUnit()
    {
        var keys = Builder().Build(Info()).Select(e => e.Key).Order().ToArray();

        Assert.Equal(UnitEntityBuilder.KnownKeys.Order().ToArray(), keys);
    }

    [Fact]
    public void Two_UnitsDoNotShareAnyTopicOrUniqueId()
    {
        var second = new DaikinUnitOptions { Id = "chambre", Host = "192.168.1.61" };
        var builder = Builder();
        var a = builder.Build(Info());
        var b = builder.Build(Info(unit: second));

        var all = a.Concat(b).ToList();
        Assert.Equal(all.Count, all.Select(e => e.Component.UniqueId).Distinct().Count());
        Assert.Empty(a.SelectMany(e => e.States.Select(s => s.Topic)).Intersect(b.SelectMany(e => e.States.Select(s => s.Topic))));
        Assert.Equal("daikin_chambre", builder.NodeIdOf("chambre"));
        Assert.Equal("daikin/chambre/status", builder.AvailabilityTopicOf("chambre"));
    }

    [Fact]
    public void Overrides_RenameMoveToAnAreaAndDisable()
    {
        var entities = new EntitiesOptions
        {
            Entities =
            {
                new EntityDefinition { Key = "salon.outdoor_temperature", Name = "Dehors", Area = "Terrasse" },
                new EntityDefinition { Key = "SALON.Firmware", Enabled = false },
                new EntityDefinition { Key = "chambre.mac", Enabled = false }, // another unit: ignored
            },
        };
        var built = Builder(entities: entities).Build(Info());

        var outdoor = Entity(built, "outdoor_temperature");
        Assert.Equal("Dehors", outdoor.Component.Name);
        Assert.Equal("Terrasse", outdoor.Component.Area);
        Assert.Equal("daikin_salon_outdoor_temperature", outdoor.Component.UniqueId); // renaming does not change the id
        Assert.DoesNotContain(built, e => e.Key == "firmware");
        Assert.Contains(built, e => e.Key == "mac");
        Assert.Null(Entity(built, "indoor_temperature").Component.Area);
    }

    [Fact]
    public void AutoDiscoverOff_OnlyPublishesDeclaredEntities()
    {
        var entities = new EntitiesOptions
        {
            Entities = { new EntityDefinition { UnitId = "salon", Id = "dt3", Name = "Cool target", Component = "sensor", Source = "control", Field = "dt3" } },
        };

        var built = Builder(new DaikinOptions { AutoDiscover = false }, entities).Build(Info());

        Assert.Equal("manual_dt3", Assert.Single(built).Key);
    }

    // ---- declared entities -------------------------------------------------------------------------------------------

    [Fact]
    public void Declared_SensorReadsAnyKeyOfTheAnswers()
    {
        var entities = new EntitiesOptions
        {
            Entities =
            {
                new EntityDefinition
                {
                    UnitId = "salon", Id = "cool_target", Name = "Cool target", Component = "sensor", Source = "control", Field = "dt3",
                    Unit = "°C", DeviceClass = "temperature", StateClass = "measurement", Decimals = 1, Area = "Salon",
                },
                new EntityDefinition { UnitId = "salon", Id = "led", Name = "LED", Component = "binary_sensor", Source = "basic", Field = "led", Invert = true },
                new EntityDefinition { UnitId = "chambre", Id = "other", Name = "Other unit", Component = "sensor", Source = "control", Field = "dt3" },
            },
        };
        var built = Builder(entities: entities).Build(Info());
        var snapshot = Snapshot(new() { ["dt3"] = "24.55" }, basic: new() { ["led"] = "1" });

        var cool = Entity(built, "manual_cool_target");
        Assert.Equal("daikin/salon/cool_target/state", cool.States.Single().Topic);
        Assert.Equal("24.6", cool.States.Single().ToPayload(snapshot));
        Assert.Equal("°C", cool.Component.UnitOfMeasurement);
        Assert.Equal("Salon", cool.Component.Area);
        Assert.Equal("OFF", Entity(built, "manual_led").States.Single().ToPayload(snapshot)); // inverted
        Assert.DoesNotContain(built, e => e.Key == "manual_other");
        Assert.Null(cool.States.Single().ToPayload(Snapshot()));
    }

    [Theory]
    [InlineData("", "x", "sensor", "control", "f")]
    [InlineData("N", "bad id", "sensor", "control", "f")]
    [InlineData("N", "x", "light", "control", "f")]
    [InlineData("N", "x", "sensor", "monitor", "f")]
    [InlineData("N", "x", "sensor", "control", "")]
    public void Declared_InvalidDefinitionsAreSkipped(string name, string id, string component, string source, string field)
    {
        var entities = new EntitiesOptions
        {
            Entities = { new EntityDefinition { UnitId = "salon", Id = id, Name = name, Component = component, Source = source, Field = field } },
        };

        Assert.DoesNotContain(Builder(entities: entities).Build(Info()), e => e.Key.StartsWith("manual_"));
    }

    [Fact]
    public void Declared_DisabledEntityIsNotPublished()
    {
        var entities = new EntitiesOptions
        {
            Entities = { new EntityDefinition { UnitId = "salon", Id = "x", Name = "X", Component = "sensor", Source = "control", Field = "dt3", Enabled = false } },
        };

        Assert.DoesNotContain(Builder(entities: entities).Build(Info()), e => e.Key == "manual_x");
    }
}
