using System.Text.Json.Nodes;
using Daikin2Mqtt.Service;

namespace Daikin2Mqtt.Tests;

public class ConfigEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "daikinedit_" + Guid.NewGuid().ToString("N"));

    public ConfigEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonObject Config(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonArray Entities(string json) => JsonNode.Parse(json)!.AsArray();

    private const string TwoUnits =
        """{"Daikin":{"Units":[{"Id":"salon","Host":"192.168.1.60","Name":"Clim salon"},{"Id":"chambre","Host":"clim-chambre.lan:8080"}]}}""";

    [Fact]
    public void YamlJson_RoundTripKeepsTypes()
    {
        var node = YamlJson.Parse("A: 1\nB: true\nC: \"65537\"\nD: 1.5\nE: text\nF: ~\nG: [1, two]\n")!;
        var back = YamlJson.Parse(YamlJson.ToYaml(node))!;

        Assert.Equal(1, (long)back["A"]!);
        Assert.True((bool)back["B"]!);
        Assert.Equal("65537", (string)back["C"]!);
        Assert.Equal(1.5, (double)back["D"]!);
        Assert.Equal("text", (string)back["E"]!);
        Assert.Equal("two", (string)back["G"]![1]!);
    }

    [Fact]
    public void YamlJson_QuotesStringsThatLookLikeOtherTypes()
    {
        var node = new JsonObject { ["Key"] = "65537", ["Flag"] = "true", ["Name"] = "Clim: salon" };
        var back = YamlJson.Parse(YamlJson.ToYaml(node))!;

        Assert.Equal("65537", (string)back["Key"]!);
        Assert.Equal("true", (string)back["Flag"]!);
        Assert.Equal("Clim: salon", (string)back["Name"]!);
    }

    [Fact]
    public void Save_WritesBothFilesAndKeepsBackup()
    {
        File.WriteAllText(Path.Combine(_dir, "config.yml"), "Mqtt:\n  Host: old\n  Password: secret\n");
        var editor = new ConfigEditor(_dir);

        // As received from the HTTP body: both nodes already have a parent.
        var body = JsonNode.Parse("""
            {"config":{"Mqtt":{"Host":"broker","Password":"********"},"Daikin":{"Units":[{"Id":"salon","Host":"192.168.1.60"}]}},
             "entities":[{"Key":"salon.outdoor_temperature","Name":"Dehors","Area":"Terrasse"}]}
            """)!;
        var config = body["config"]!.AsObject();
        var entities = body["entities"]!.AsArray();

        Assert.Empty(editor.Save(config, entities));
        var text = File.ReadAllText(Path.Combine(_dir, "config.yml"));
        Assert.Contains("Host: broker", text);
        Assert.Contains("Password: secret", text); // mask resolved to the stored value
        Assert.Contains("Id: salon", text);
        Assert.True(File.Exists(Path.Combine(_dir, "config.yml.bak")));
        Assert.Contains("salon.outdoor_temperature", File.ReadAllText(Path.Combine(_dir, "entities.yml")));
    }

    [Fact]
    public void Read_ReturnsTheUnitsAndTheKnownEntityKeys()
    {
        File.WriteAllText(Path.Combine(_dir, "config.yml"), "Daikin:\n  Units:\n    - Id: salon\n      Host: 192.168.1.60\n");
        var data = new ConfigEditor(_dir).Read();

        Assert.Equal("salon", (string)data["config"]!["Daikin"]!["Units"]![0]!["Id"]!);
        Assert.Contains("climate", data["entityKeys"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Read_MasksSecrets()
    {
        File.WriteAllText(Path.Combine(_dir, "config.yml"), "Mqtt:\n  Password: secret\nWeb:\n  Password: \"\"\n");
        var data = new ConfigEditor(_dir).Read();

        Assert.Equal(ConfigEditor.Mask, (string)data["config"]!["Mqtt"]!["Password"]!);
        Assert.DoesNotContain("secret", data.ToJsonString());
    }

    [Fact]
    public void Save_RejectsInvalidEntitiesWithoutWriting()
    {
        var editor = new ConfigEditor(_dir);
        var errors = editor.Save(
            Config("{}"),
            Entities("""[{"UnitId":"salon","Id":"x","Name":"X","Component":"bogus","Source":"control","Field":"dt3"}]"""));

        Assert.NotEmpty(errors);
        Assert.False(File.Exists(Path.Combine(_dir, "config.yml")));
    }

    [Fact]
    public void Save_WithoutAFolderExplainsWhat_ToDo()
    {
        var errors = new ConfigEditor(null).Save(Config("{}"), new JsonArray());

        Assert.Contains("DAIKIN2MQTT_CONFIG_DIR", Assert.Single(errors));
    }

    [Theory]
    [InlineData("""{"Daikin":{"Units":[{"Id":"bad id","Host":"1.2.3.4"}]}}""", "Id is required")]
    [InlineData("""{"Daikin":{"Units":[{"Id":"a","Host":"1.2.3.4"},{"Id":"A","Host":"1.2.3.5"}]}}""", "used by another unit")]
    [InlineData("""{"Daikin":{"Units":[{"Id":"a","Host":"http://1.2.3.4"}]}}""", "Host must be")]
    [InlineData("""{"Daikin":{"Units":[{"Id":"a","Host":""}]}}""", "Host must be")]
    [InlineData("""{"Daikin":{"Units":[{"Id":"a","Host":"1.2.3.4","MinTemperature":30,"MaxTemperature":16}]}}""", "MaxTemperature")]
    [InlineData("""{"Daikin":{"Polling":{"IntervalSeconds":0}}}""", "IntervalSeconds")]
    [InlineData("""{"Mqtt":{"BaseTopic":"a/#"}}""", "BaseTopic")]
    [InlineData("""{"Device":{"NodeId":"my node"}}""", "NodeId")]
    [InlineData("""{"Web":{"Port":70000}}""", "Web.Port")]
    public void Save_ValidatesTheSettings(string config, string expectedError)
    {
        var errors = new ConfigEditor(_dir).Save(Config(config), new JsonArray());

        Assert.Contains(errors, e => e.Contains(expectedError));
        Assert.False(File.Exists(Path.Combine(_dir, "config.yml")));
    }

    [Theory]
    [InlineData("""[{"Key":"salon"}]""", "Key must be")]
    [InlineData("""[{"Key":"salon.nothing"}]""", "unknown entity")]
    [InlineData("""[{"Key":"garage.climate"}]""", "not in Daikin.Units")]
    [InlineData("""[{"UnitId":"salon","Id":"x","Component":"sensor","Source":"control","Field":"dt3"}]""", "Name is required")]
    [InlineData("""[{"UnitId":"salon","Id":"x","Name":"X","Component":"sensor","Source":"nope","Field":"dt3"}]""", "Source must be")]
    [InlineData("""[{"UnitId":"salon","Id":"x","Name":"X","Component":"sensor","Source":"control"}]""", "Field is required")]
    [InlineData("""[{"UnitId":"garage","Id":"x","Name":"X","Component":"sensor","Source":"control","Field":"f"}]""", "not in Daikin.Units")]
    [InlineData("""[{"UnitId":"salon","Id":"x","Name":"X","Component":"sensor","Source":"control","Field":"f"},{"UnitId":"salon","Id":"x","Name":"Y","Component":"sensor","Source":"sensor","Field":"g"}]""", "more than once")]
    public void Save_ValidatesTheEntities(string entities, string expectedError)
    {
        var errors = new ConfigEditor(_dir).Save(Config(TwoUnits), Entities(entities));

        Assert.Contains(errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void Save_AcceptsCustomisedAndDeclaredEntities()
    {
        var errors = new ConfigEditor(_dir).Save(
            Config(TwoUnits),
            Entities("""
                [{"Key":"salon.outdoor_temperature","Name":"Dehors"},
                 {"Key":"chambre.energy_today","Enabled":false},
                 {"UnitId":"salon","Id":"cool_target","Name":"Cool","Component":"sensor","Source":"control","Field":"dt3","Unit":"°C","Decimals":1}]
                """));

        Assert.Empty(errors);
        Assert.Contains("chambre.energy_today", File.ReadAllText(Path.Combine(_dir, "entities.yml")));
    }
}
