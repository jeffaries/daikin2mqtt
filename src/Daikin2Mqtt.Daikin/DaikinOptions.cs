using Daikin2Mqtt.Daikin.Mapping;

namespace Daikin2Mqtt.Daikin;

/// <summary>Settings for the Daikin indoor units (BRP069C4x WiFi module, classic local API).</summary>
public sealed class DaikinOptions
{
    public const string SectionName = "Daikin";

    /// <summary>Expose every entity supported by each unit, in addition to the declared Entities (which win).</summary>
    public bool AutoDiscover { get; set; } = true;

    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Extra attempts (with exponential backoff) for a request that failed because of the network.</summary>
    public int RequestRetries { get; set; } = 2;

    /// <summary>First retry delay; doubled at each attempt.</summary>
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>A unit is reported unavailable once it has not answered for this long.</summary>
    public int OfflineGraceSeconds { get; set; } = 30;

    /// <summary>Publish the energy / runtime sensors (needs the energy endpoints of the unit).</summary>
    public bool EnableEnergy { get; set; } = true;

    /// <summary>Simulated units, to see the entities appear in Home Assistant without hardware.</summary>
    public bool Fake { get; set; }

    /// <summary>How long the network scan of the web interface waits for answers.</summary>
    public int ScanTimeoutSeconds { get; set; } = 3;

    public PollingSettings Polling { get; set; } = new();

    public List<DaikinUnitOptions> Units { get; set; } = new();
}

public sealed class PollingSettings
{
    /// <summary>Period of the control / sensor polling (the module has no push channel).</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Period of the energy polling: much slower, the values only change slowly.</summary>
    public int EnergyIntervalSeconds { get; set; } = 300;
}

/// <summary>One indoor unit (= one Home Assistant device).</summary>
public sealed class DaikinUnitOptions
{
    /// <summary>Identifier used in topics and unique ids: letters, digits, '_' and '-'.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>IP address or host name of the WiFi module, optionally with a port ("192.168.1.60" or "host:8080").</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Device name in Home Assistant; empty = name stored in the module, else the Id.</summary>
    public string? Name { get; set; }

    /// <summary>Suggested area of the device ("Salon").</summary>
    public string? Area { get; set; }

    /// <summary>Bounds of the target temperature slider. The module does not report them (defaults 10 and 32).</summary>
    public double? MinTemperature { get; set; }

    public double? MaxTemperature { get; set; }
}

public sealed class DeviceOptions
{
    public const string SectionName = "Device";

    /// <summary>Prefix of the discovery node ids and unique ids; the node id of a unit is "&lt;NodeId&gt;_&lt;unit id&gt;".</summary>
    public string NodeId { get; set; } = "daikin";

    public string Manufacturer { get; set; } = "Daikin";
}

/// <summary>Root-bound list of customised or declared entities (entities.yml).</summary>
public sealed class EntitiesOptions
{
    /// <summary>
    /// Entries with a <see cref="EntityDefinition.Key"/> customise an auto-published entity; the others declare one.
    /// </summary>
    public List<EntityDefinition> Entities { get; set; } = new();

    /// <summary>Entities declared by hand (no Key).</summary>
    public IReadOnlyList<EntityDefinition> Declared => Entities.Where(e => string.IsNullOrWhiteSpace(e.Key)).ToList();

    /// <summary>Customisations of auto-published entities, keyed by "&lt;unit id&gt;.&lt;entity&gt;".</summary>
    public IReadOnlyDictionary<string, EntityOverride> Overrides =>
        Entities.Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .GroupBy(e => e.Key!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new EntityOverride { Name = g.Last().Name, Area = g.Last().Area, Enabled = g.Last().Enabled },
                StringComparer.OrdinalIgnoreCase);
}

/// <summary>Changes applied to one auto-published entity.</summary>
public sealed class EntityOverride
{
    public string? Name { get; set; }

    public string? Area { get; set; }

    /// <summary>False = the entity is not published.</summary>
    public bool? Enabled { get; set; }
}
