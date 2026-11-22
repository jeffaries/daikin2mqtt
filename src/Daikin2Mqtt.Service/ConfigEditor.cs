using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Daikin.Mapping;
using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;

namespace Daikin2Mqtt.Service;

/// <summary>Reads, validates and writes config.yml and entities.yml for the web interface.</summary>
public sealed partial class ConfigEditor(string? directory)
{
    public const string Mask = "********";

    /// <summary>Settings never sent to the browser in clear text (the mask means "keep the stored value").</summary>
    private static readonly string[][] SecretPaths =
    {
        new[] { "Mqtt", "Password" },
        new[] { "Web", "Password" },
    };

    public string? Directory { get; } = directory;

    private string ConfigPath => Path.Combine(Directory!, ExternalConfig.FileNames[0]);
    private string EntitiesPath => Path.Combine(Directory!, ExternalConfig.FileNames[1]);

    public JsonObject Read()
    {
        var config = Directory is null ? null : ReadFile(ConfigPath) as JsonObject;
        config ??= new JsonObject();
        var entities = Directory is null ? null : ReadFile(EntitiesPath) as JsonObject;
        var list = entities?["Entities"] as JsonArray ?? new JsonArray();

        foreach (var path in SecretPaths)
        {
            if (Navigate(config, path) is { } parent && !string.IsNullOrEmpty(parent[path[^1]]?.ToString()))
            {
                parent[path[^1]] = Mask;
            }
        }

        return new JsonObject
        {
            ["directory"] = Directory,
            ["config"] = config,
            ["entities"] = JsonNode.Parse(list.ToJsonString()),
            ["entityKeys"] = new JsonArray(UnitEntityBuilder.KnownKeys.Select(k => (JsonNode?)k).ToArray()),
        };
    }

    /// <summary>Validates the new content and writes both files (the previous ones are kept as *.bak). Returns the errors, empty when saved.</summary>
    public IReadOnlyList<string> Save(JsonObject config, JsonArray entities)
    {
        if (Directory is null)
        {
            return new[] { "No configuration folder found: mount a folder on /config or set DAIKIN2MQTT_CONFIG_DIR." };
        }

        // Secrets sent back as the mask keep their stored value.
        var current = ReadFile(ConfigPath) as JsonObject;
        foreach (var path in SecretPaths)
        {
            if (Navigate(config, path) is { } parent && parent[path[^1]]?.ToString() == Mask)
            {
                var stored = current is null ? null : Navigate(current, path)?[path[^1]];
                parent[path[^1]] = stored is null ? null : JsonNode.Parse(stored.ToJsonString());
            }
        }

        var entitiesDoc = new JsonObject { ["Entities"] = entities.DeepClone() };
        var configYaml = "# Written by the daikin2mqtt web interface.\n" + YamlJson.ToYaml(config);
        var entitiesYaml = "# Written by the daikin2mqtt web interface.\n" + YamlJson.ToYaml(entitiesDoc);

        var errors = Validate(configYaml, entitiesYaml);
        if (errors.Count > 0)
        {
            return errors;
        }

        Write(ConfigPath, configYaml);
        Write(EntitiesPath, entitiesYaml);
        return Array.Empty<string>();
    }

    private static List<string> Validate(string configYaml, string entitiesYaml)
    {
        var errors = new List<string>();
        MqttOptions mqtt;
        DeviceOptions device;
        DaikinOptions daikin;
        WebOptions web;
        List<EntityDefinition> entities;
        try
        {
            var root = new ConfigurationBuilder()
                .AddYamlStream(new MemoryStream(Encoding.UTF8.GetBytes(configYaml)))
                .AddYamlStream(new MemoryStream(Encoding.UTF8.GetBytes(entitiesYaml)))
                .Build();
            mqtt = root.GetSection(MqttOptions.SectionName).Get<MqttOptions>() ?? new();
            root.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>();
            daikin = root.GetSection(DaikinOptions.SectionName).Get<DaikinOptions>() ?? new();
            device = root.GetSection(DeviceOptions.SectionName).Get<DeviceOptions>() ?? new();
            web = root.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new();
            entities = root.Get<EntitiesOptions>()?.Entities ?? new();
        }
        catch (Exception ex)
        {
            return new List<string> { $"Invalid value: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(mqtt.Host)) errors.Add("Mqtt.Host is required.");
        if (mqtt.Port is < 1 or > 65535) errors.Add("Mqtt.Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(mqtt.BaseTopic)) errors.Add("Mqtt.BaseTopic is required.");
        else if (mqtt.BaseTopic.IndexOfAny(new[] { '+', '#' }) >= 0) errors.Add("Mqtt.BaseTopic must not contain '+' or '#'.");
        if (!Identifier().IsMatch(device.NodeId)) errors.Add("Device.NodeId: letters, digits, '_' and '-' only.");
        if (web.Port is < 1 or > 65535) errors.Add("Web.Port must be between 1 and 65535.");

        if (daikin.RequestTimeoutSeconds < 1) errors.Add("Daikin.RequestTimeoutSeconds must be at least 1.");
        if (daikin.OfflineGraceSeconds < 0) errors.Add("Daikin.OfflineGraceSeconds must not be negative.");
        if (daikin.Polling.IntervalSeconds < 1) errors.Add("Daikin.Polling.IntervalSeconds must be at least 1.");
        if (daikin.Polling.EnergyIntervalSeconds < 1) errors.Add("Daikin.Polling.EnergyIntervalSeconds must be at least 1.");

        var unitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < daikin.Units.Count; i++)
        {
            var unit = daikin.Units[i];
            var label = $"Daikin.Units[{i + 1}] ({(string.IsNullOrWhiteSpace(unit.Id) ? "?" : unit.Id)})";
            if (!Identifier().IsMatch(unit.Id ?? string.Empty)) errors.Add($"{label}: Id is required (letters, digits, '_' and '-').");
            else if (!unitIds.Add(unit.Id!)) errors.Add($"{label}: Id is used by another unit.");
            if (!HostPattern().IsMatch(unit.Host ?? string.Empty)) errors.Add($"{label}: Host must be an IP address or a host name, optionally followed by :port (no http://).");
            if (unit.MinTemperature is { } min && unit.MaxTemperature is { } max && max <= min)
            {
                errors.Add($"{label}: MaxTemperature must be greater than MinTemperature.");
            }
        }

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            var label = $"Entities[{i + 1}] ({(string.IsNullOrWhiteSpace(e.Key) ? e.Name : e.Key)})";
            if (!string.IsNullOrWhiteSpace(e.Key))
            {
                var parts = e.Key.Trim().Split('.', 2);
                if (parts.Length != 2 || !Identifier().IsMatch(parts[0]))
                {
                    errors.Add($"{label}: Key must be \"<unit id>.<entity>\", for example \"salon.outdoor_temperature\".");
                }
                else if (!UnitEntityBuilder.KnownKeys.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{label}: unknown entity '{parts[1]}'. Known: {string.Join(", ", UnitEntityBuilder.KnownKeys)}.");
                }
                else if (daikin.Units.Count > 0 && !unitIds.Contains(parts[0]))
                {
                    errors.Add($"{label}: unit '{parts[0]}' is not in Daikin.Units.");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(e.Name)) errors.Add($"{label}: Name is required.");
            if (!Identifier().IsMatch(e.Id ?? string.Empty)) errors.Add($"{label}: Id is required (letters, digits, '_' and '-').");
            if (string.IsNullOrWhiteSpace(e.UnitId)) errors.Add($"{label}: UnitId is required.");
            else if (daikin.Units.Count > 0 && !unitIds.Contains(e.UnitId.Trim())) errors.Add($"{label}: unit '{e.UnitId}' is not in Daikin.Units.");
            if (!UnitEntityBuilder.ManualComponents.Contains(e.Component)) errors.Add($"{label}: Component must be one of {string.Join(", ", UnitEntityBuilder.ManualComponents)}.");
            if (!UnitEntityBuilder.ManualSources.Contains(e.Source ?? string.Empty)) errors.Add($"{label}: Source must be one of {string.Join(", ", UnitEntityBuilder.ManualSources)}.");
            if (string.IsNullOrWhiteSpace(e.Field)) errors.Add($"{label}: Field is required.");
        }

        var declared = entities.Where(e => string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.UnitId) && !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => $"{e.UnitId!.Trim()}/{e.Id!.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var duplicate in declared)
        {
            errors.Add($"Entities: '{duplicate.Key}' is declared more than once (same UnitId and Id).");
        }

        return errors;
    }

    private static JsonNode? ReadFile(string path) =>
        File.Exists(path) ? YamlJson.Parse(File.ReadAllText(path)) : null;

    private static void Write(string path, string content)
    {
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns the object holding the last segment of <paramref name="path"/>, or null when a parent is missing.</summary>
    private static JsonObject? Navigate(JsonObject root, string[] path)
    {
        var node = root;
        foreach (var segment in path[..^1])
        {
            if (node[segment] is not JsonObject child)
            {
                return null;
            }

            node = child;
        }

        return node;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?(:\d{1,5})?$")]
    private static partial Regex HostPattern();
}
