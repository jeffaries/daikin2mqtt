namespace Daikin2Mqtt.Daikin.Mapping;

/// <summary>One entry of the "Entities" list (entities.yml).</summary>
public sealed class EntityDefinition
{
    /// <summary>
    /// When set, this entry does not declare a new entity: it customises an auto-published one, identified by
    /// "&lt;unit id&gt;.&lt;entity&gt;" (for example "salon.outdoor_temperature"). Only Name, Area and Enabled are used.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>False = the entity is not published (with Key: an auto-published entity; without: a declared one).</summary>
    public bool? Enabled { get; set; }

    /// <summary>Declared entity: id of the unit (from Daikin.Units) it belongs to.</summary>
    public string? UnitId { get; set; }

    /// <summary>Declared entity: identifier used in the topic (&lt;BaseTopic&gt;/&lt;unit&gt;/&lt;Id&gt;/state): letters, digits, '_' and '-'.</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Room of the entity. Entities of a same area form one Home Assistant device carrying that area.</summary>
    public string? Area { get; set; }

    /// <summary>Declared entity: sensor or binary_sensor.</summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>Declared entity: which answer of the unit holds the value: control, sensor or basic.</summary>
    public string? Source { get; set; }

    /// <summary>Declared entity: the key in that answer (for example <c>dt3</c>, <c>cmpfreq</c>, <c>radio1</c>).</summary>
    public string? Field { get; set; }

    public string? DeviceClass { get; set; }

    /// <summary>Unit of measurement (sensor).</summary>
    public string? Unit { get; set; }

    public string? StateClass { get; set; }
    public string? Icon { get; set; }

    /// <summary>binary_sensor only: report ON when the value is 0 / off.</summary>
    public bool Invert { get; set; }

    /// <summary>sensor only: number of decimals published for numeric values.</summary>
    public int? Decimals { get; set; }
}
