using System.Runtime.CompilerServices;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Daikin.Mapping;
using Daikin2Mqtt.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetEscapades.Configuration.Yaml;

namespace Daikin2Mqtt.Tests;

public class ExternalConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daikincfg_" + Guid.NewGuid().ToString("N"));

    public ExternalConfigTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "sub"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LocateDirectory_FindsConfigFolderInParents()
    {
        var config = Path.Combine(_root, "config");
        File.WriteAllText(Path.Combine(config, "entities.yml"), "Entities: []");

        Assert.Equal(config, ExternalConfig.LocateDirectory(null, Path.Combine(config, "sub")));
    }

    [Fact]
    public void LocateDirectory_IgnoresFolderWithoutConfigFiles() =>
        Assert.Null(ExternalConfig.LocateDirectory(null, Path.Combine(_root, "config", "sub")));

    [Fact]
    public void LocateDirectory_ExplicitMissingFolderFails() =>
        Assert.Throws<DirectoryNotFoundException>(() => ExternalConfig.LocateDirectory(Path.Combine(_root, "nope")));

    [Fact]
    public void YamlFiles_BindUnitsAndEntities()
    {
        var config = Path.Combine(_root, "config");
        File.WriteAllText(Path.Combine(config, "config.yml"), """
            Daikin:
              AutoDiscover: false
              Polling: { IntervalSeconds: 20 }
              Units:
                - { Id: salon, Host: 10.0.0.5, Name: Clim salon, Area: Salon, MinTemperature: 16 }
                - { Id: chambre, Host: 10.0.0.6 }
            """);
        File.WriteAllText(Path.Combine(config, "entities.yml"), """
            Entities:
              - { Key: "salon.outdoor_temperature", Name: Dehors, Area: Terrasse }
              - { Key: "chambre.firmware", Enabled: false }
              - { UnitId: salon, Id: cool_target, Name: Consigne, Component: sensor, Source: control, Field: dt3, Unit: "°C", Decimals: 1 }
            """);

        var configuration = new ConfigurationBuilder()
            .AddYamlFile(Path.Combine(config, "config.yml"))
            .AddYamlFile(Path.Combine(config, "entities.yml"))
            .Build();

        var daikin = configuration.GetSection("Daikin").Get<DaikinOptions>()!;
        var entities = configuration.Get<EntitiesOptions>()!;

        Assert.False(daikin.AutoDiscover);
        Assert.Equal(20, daikin.Polling.IntervalSeconds);
        Assert.Equal(300, daikin.Polling.EnergyIntervalSeconds); // default kept
        Assert.Equal(2, daikin.Units.Count);
        Assert.Equal("10.0.0.5", daikin.Units[0].Host);
        Assert.Equal(16, daikin.Units[0].MinTemperature);
        Assert.Null(daikin.Units[1].Name);
        Assert.Equal("Dehors", entities.Overrides["salon.outdoor_temperature"].Name);
        Assert.Equal("Dehors", entities.Overrides["SALON.Outdoor_Temperature"].Name);
        Assert.False(entities.Overrides["chambre.firmware"].Enabled);
        Assert.Equal("dt3", Assert.Single(entities.Declared).Field);
    }

    [Fact]
    public void EnvironmentVariables_CanSetTheUnits()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daikin:Units:0:Id"] = "salon", // what Daikin__Units__0__Id becomes
                ["Daikin:Units:0:Host"] = "10.0.0.9",
                ["Daikin:Fake"] = "true",
            })
            .Build();

        var daikin = configuration.GetSection("Daikin").Get<DaikinOptions>()!;

        Assert.True(daikin.Fake);
        Assert.Equal("10.0.0.9", Assert.Single(daikin.Units).Host);
    }

    private static string ConfigDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "config"));

    [Fact]
    public void ExampleFiles_LoadAndMap()
    {
        var configuration = new ConfigurationBuilder()
            .AddYamlFile(Path.Combine(ConfigDirectory(), "config.example.yml"))
            .AddYamlFile(Path.Combine(ConfigDirectory(), "entities.example.yml"))
            .Build();

        Assert.Equal("mosquitto", configuration["Mqtt:Host"]);
        var daikin = configuration.GetSection("Daikin").Get<DaikinOptions>()!;
        Assert.True(daikin.AutoDiscover);
        Assert.Equal("salon", Assert.Single(daikin.Units).Id);
        Assert.Equal(300, daikin.Polling.EnergyIntervalSeconds);

        var entities = configuration.Get<EntitiesOptions>()!;
        Assert.Equal("Température extérieure", entities.Overrides["salon.outdoor_temperature"].Name);
        Assert.False(entities.Overrides["salon.firmware"].Enabled);
        Assert.Equal(2, entities.Declared.Count);

        // The documented examples must produce entities without any warning.
        var info = new DaikinUnitInfo(daikin.Units[0], "Daikin", "A1B2C3D4E5F6", "1_14_88", DaikinCapabilities.Default, true, true);
        var builder = new UnitEntityBuilder(
            new TopicBuilder("daikin"), Options.Create(new DeviceOptions()), Options.Create(daikin), Options.Create(entities),
            NullLogger<UnitEntityBuilder>.Instance);
        var built = builder.Build(info);

        Assert.Contains(built, e => e.Key == "manual_cool_target");
        Assert.Contains(built, e => e.Key == "manual_led");
        Assert.DoesNotContain(built, e => e.Key == "firmware");
        Assert.Equal("Terrasse", built.Single(e => e.Key == "outdoor_temperature").Component.Area);
    }

    [Fact]
    public void ExampleFiles_PassTheWebInterfaceValidation()
    {
        var dir = Path.Combine(_root, "example");
        Directory.CreateDirectory(dir);
        var editor = new ConfigEditor(dir);
        var config = YamlJson.Parse(File.ReadAllText(Path.Combine(ConfigDirectory(), "config.example.yml")))!.AsObject();
        var entities = YamlJson.Parse(File.ReadAllText(Path.Combine(ConfigDirectory(), "entities.example.yml")))!["Entities"]!.AsArray();

        Assert.Empty(editor.Save(config, entities));
    }
}
