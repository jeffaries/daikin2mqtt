using System.Globalization;
using System.Text.RegularExpressions;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Daikin.Mapping;

/// <summary>
/// Builds the Home Assistant entities of a unit from what it reports it can do (<c>get_model_info</c>): an entity the
/// unit does not support is not published. Topics live under <c>&lt;BaseTopic&gt;/&lt;unit id&gt;/</c>.
/// </summary>
public sealed partial class UnitEntityBuilder
{
    /// <summary>Entity keys the web interface and the "Key" of entities.yml accept ("&lt;unit id&gt;.&lt;key&gt;").</summary>
    public static readonly IReadOnlyList<string> KnownKeys = new[]
    {
        "climate", "indoor_temperature", "outdoor_temperature", "indoor_humidity", "compressor_frequency", "error_code",
        "wifi_rssi", "special_mode", "today_runtime", "energy_today", "energy_today_heat", "energy_today_cool", "energy_week",
        "energy_month", "energy_month_heat", "energy_month_cool", "energy_year", "energy_year_heat", "energy_year_cool",
        "alert", "compressor_running", "holiday_mode", "firmware", "mac", "ip",
    };

    public static readonly IReadOnlyList<string> ManualComponents = new[] { "sensor", "binary_sensor" };
    public static readonly IReadOnlyList<string> ManualSources = new[] { "control", "sensor", "basic" };

    private const string On = "ON";
    private const string Off = "OFF";

    private readonly TopicBuilder _topics;
    private readonly string _nodeId;
    private readonly DaikinOptions _options;
    private readonly EntitiesOptions _entities;
    private readonly ILogger _logger;

    public UnitEntityBuilder(
        TopicBuilder topics,
        IOptions<DeviceOptions> device,
        IOptions<DaikinOptions> options,
        IOptions<EntitiesOptions> entities,
        ILogger<UnitEntityBuilder> logger)
    {
        _topics = topics;
        _nodeId = device.Value.NodeId;
        _options = options.Value;
        _entities = entities.Value;
        _logger = logger;
    }

    /// <summary>Discovery node id of a unit: one Home Assistant device per unit.</summary>
    public string NodeIdOf(string unitId) => $"{_nodeId}_{unitId}";

    /// <summary>Availability topic of a unit: "&lt;BaseTopic&gt;/&lt;unit id&gt;/status".</summary>
    public string AvailabilityTopicOf(string unitId) => _topics.Join(unitId, "status");

    public IReadOnlyList<MappedEntity> Build(DaikinUnitInfo info)
    {
        var unit = info.Unit;
        var caps = info.Capabilities;
        var overrides = _entities.Overrides;
        var result = new List<MappedEntity>();

        // (name, area) after the user's customisation, null when the entity is disabled.
        (string Name, string? Area)? Resolve(string key, string defaultName)
        {
            if (overrides.TryGetValue($"{unit.Id}.{key}", out var o))
            {
                if (o.Enabled == false)
                {
                    return null;
                }

                return (string.IsNullOrWhiteSpace(o.Name) ? defaultName : o.Name.Trim(), string.IsNullOrWhiteSpace(o.Area) ? null : o.Area.Trim());
            }

            return (defaultName, null);
        }

        void Add(string key, string defaultName, Func<string, string, string?, MappedEntity> create)
        {
            if (Resolve(key, defaultName) is { } r)
            {
                var entity = create(key, r.Name, r.Area);
                entity.Component.Area = r.Area;
                result.Add(entity);
            }
        }

        string Topic(string key, params string[] suffix) => _topics.Join(new object[] { unit.Id, key }.Concat(suffix).ToArray());
        string UniqueId(string key) => $"{NodeIdOf(unit.Id)}_{key}";

        MappedEntity Sensor(string key, string name, Func<DaikinSnapshot, string?> value, string? unitOfMeasurement = null,
            string? deviceClass = null, string? stateClass = null, string? icon = null, bool diagnostic = false,
            Dictionary<string, object?>? extra = null)
        {
            var topic = Topic(key, "state");
            extra ??= new();
            if (diagnostic)
            {
                extra["entity_category"] = "diagnostic";
            }

            return new MappedEntity(
                key,
                new ComponentDescriptor
                {
                    Key = key, Platform = "sensor", UniqueId = UniqueId(key), Name = name, StateTopic = topic,
                    DeviceClass = deviceClass, UnitOfMeasurement = unitOfMeasurement, StateClass = stateClass, Icon = icon, Extra = extra,
                },
                new[] { new StateBinding(topic, value) },
                Array.Empty<CommandBinding>());
        }

        MappedEntity Binary(string key, string name, Func<DaikinSnapshot, string?> value, string? deviceClass = null, bool diagnostic = false)
        {
            var topic = Topic(key, "state");
            return new MappedEntity(
                key,
                new ComponentDescriptor
                {
                    Key = key, Platform = "binary_sensor", UniqueId = UniqueId(key), Name = name, StateTopic = topic, DeviceClass = deviceClass,
                    Extra = diagnostic ? new Dictionary<string, object?> { ["entity_category"] = "diagnostic" } : new Dictionary<string, object?>(),
                },
                new[] { new StateBinding(topic, value) },
                Array.Empty<CommandBinding>());
        }

        if (_options.AutoDiscover)
        {
            Add("climate", "Climate", (key, name, _) => Climate(unit, caps, key, name));

            if (caps.IndoorTemperature)
            {
                Add("indoor_temperature", "Indoor temperature", (k, n, _) =>
                    Sensor(k, n, s => Number(s.IndoorTemperature, 1), "°C", "temperature", "measurement"));
            }

            Add("outdoor_temperature", "Outdoor temperature", (k, n, _) =>
                Sensor(k, n, s => Number(s.OutdoorTemperature, 1), "°C", "temperature", "measurement"));

            if (caps.HumiditySensor)
            {
                Add("indoor_humidity", "Indoor humidity", (k, n, _) =>
                    Sensor(k, n, s => Number(s.IndoorHumidity, 0), "%", "humidity", "measurement"));
            }

            Add("compressor_frequency", "Compressor frequency", (k, n, _) =>
                Sensor(k, n, s => Number(s.CompressorFrequency, 0), "Hz", "frequency", "measurement", icon: "mdi:sine-wave"));

            Add("error_code", "Error code", (k, n, _) =>
                Sensor(k, n, s => s.Sensor.GetValueOrDefault("err") ?? s.Basic.GetValueOrDefault("err"), icon: "mdi:alert-circle-outline", diagnostic: true));

            Add("wifi_rssi", "WiFi signal", (k, n, _) =>
                Sensor(k, n, s => Number(DaikinResponseParser.ParseNumber(s.Basic.GetValueOrDefault("radio1")), 0), "dBm", "signal_strength", "measurement", diagnostic: true));

            if (caps.SpecialMode)
            {
                // Read-only: the API documents no way to switch the special modes (see Known limitations).
                Add("special_mode", "Special mode", (k, n, _) =>
                    Sensor(k, n, s => DaikinValues.PresetToHa(s.Control.GetValueOrDefault("adv")), deviceClass: "enum", icon: "mdi:flash",
                        extra: new() { ["options"] = DaikinValues.PresetModes }));
            }

            Add("alert", "Alert", (k, n, _) => Binary(k, n, s => HasAlert(s) ? On : Off, "problem"));
            Add("compressor_running", "Compressor", (k, n, _) =>
                Binary(k, n, s => s.CompressorFrequency is { } hz ? (hz > 0 ? On : Off) : null, "running"));

            if (info.SupportsHoliday)
            {
                Add("holiday_mode", "Holiday mode", (k, n, _) => HolidaySwitch(unit, k, n));
            }

            AddEnergy(info, Add, Sensor);

            Add("firmware", "Firmware", (k, n, _) =>
                Sensor(k, n, s => s.Basic.GetValueOrDefault("ver") ?? info.Firmware, icon: "mdi:chip", diagnostic: true));
            Add("mac", "MAC address", (k, n, _) => Sensor(k, n, _ => info.FormattedMac ?? info.Mac, icon: "mdi:network-outline", diagnostic: true));
            Add("ip", "IP address", (k, n, _) => Sensor(k, n, _ => unit.Host, icon: "mdi:ip-network", diagnostic: true));
        }

        foreach (var declared in _entities.Declared.Where(d => string.Equals(d.UnitId?.Trim(), unit.Id, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var entity = Manual(unit, declared);
                if (result.Any(e => e.Key == entity.Key))
                {
                    throw new EntityConfigurationException($"Entity '{declared.Name}' duplicates '{entity.Key}'.");
                }

                if (declared.Enabled != false)
                {
                    result.Add(entity);
                }
            }
            catch (EntityConfigurationException ex)
            {
                _logger.LogWarning("Unit {Unit}: declared entity skipped: {Reason}", unit.Id, ex.Message);
            }
        }

        return result;
    }

    // ---- climate -----------------------------------------------------------------------------------------------------

    private MappedEntity Climate(DaikinUnitOptions unit, DaikinCapabilities caps, string key, string name)
    {
        string T(string attribute, string kind) => _topics.Join(unit.Id, key, attribute, kind);

        var states = new List<StateBinding>
        {
            new(T("mode", "state"), s => DaikinValues.HvacMode(s.Power, s.Mode)),
            new(T("temperature", "state"), s => Number(s.TargetTemperature, 1)),
        };
        var commands = new List<CommandBinding>
        {
            new(T("mode", "set"), payload =>
            {
                var mode = payload.Trim().ToLowerInvariant();
                if (mode == DaikinValues.ModeOff)
                {
                    return new DaikinCommand { Power = false };
                }

                return DaikinValues.ModeToDaikin(mode) is { } daikin ? new DaikinCommand { Power = true, Mode = daikin } : null;
            }),
            new(T("temperature", "set"), payload =>
                DaikinResponseParser.ParseNumber(payload.Trim()) is { } target
                    ? new DaikinCommand { Temperature = ClampToStep(target, caps.MinTemperature, caps.MaxTemperature) }
                    : null),
        };
        var extra = new Dictionary<string, object?>
        {
            ["modes"] = DaikinValues.HvacModes,
            ["mode_state_topic"] = states[0].Topic,
            ["mode_command_topic"] = commands[0].Topic,
            ["temperature_state_topic"] = states[1].Topic,
            ["temperature_command_topic"] = commands[1].Topic,
            ["min_temp"] = caps.MinTemperature,
            ["max_temp"] = caps.MaxTemperature,
            ["temp_step"] = 0.5,
            ["temperature_unit"] = "C",
        };

        if (caps.IndoorTemperature)
        {
            var topic = T("current_temperature", "state");
            states.Add(new StateBinding(topic, s => Number(s.IndoorTemperature, 1)));
            extra["current_temperature_topic"] = topic;
        }

        if (caps.FanRate)
        {
            states.Add(new StateBinding(T("fan_mode", "state"), s => DaikinValues.FanToHa(s.Control.GetValueOrDefault("f_rate"))));
            commands.Add(new CommandBinding(T("fan_mode", "set"), payload =>
                DaikinValues.FanToDaikin(payload) is { } fan ? new DaikinCommand { FanRate = fan } : null));
            extra["fan_modes"] = DaikinValues.FanModes;
            extra["fan_mode_state_topic"] = T("fan_mode", "state");
            extra["fan_mode_command_topic"] = T("fan_mode", "set");
        }

        if (caps.Swing)
        {
            states.Add(new StateBinding(T("swing_mode", "state"), s => DaikinValues.SwingToHa(s.Control.GetValueOrDefault("f_dir"))));
            commands.Add(new CommandBinding(T("swing_mode", "set"), payload =>
                DaikinValues.SwingToDaikin(payload) is { } swing ? new DaikinCommand { SwingDirection = swing } : null));
            extra["swing_modes"] = DaikinValues.SwingModes;
            extra["swing_mode_state_topic"] = T("swing_mode", "state");
            extra["swing_mode_command_topic"] = T("swing_mode", "set");
        }

        return new MappedEntity(
            key,
            new ComponentDescriptor { Key = key, Platform = "climate", UniqueId = $"{NodeIdOf(unit.Id)}_{key}", Name = name, Extra = extra },
            states,
            commands);
    }

    private MappedEntity HolidaySwitch(DaikinUnitOptions unit, string key, string name)
    {
        var stateTopic = _topics.Join(unit.Id, key, "state");
        var commandTopic = _topics.Join(unit.Id, key, "set");
        return new MappedEntity(
            key,
            new ComponentDescriptor
            {
                Key = key, Platform = "switch", UniqueId = $"{NodeIdOf(unit.Id)}_{key}", Name = name,
                StateTopic = stateTopic, CommandTopic = commandTopic, Icon = "mdi:beach",
            },
            new[] { new StateBinding(stateTopic, s => s.Basic.TryGetValue("en_hol", out var hol) ? (hol == "1" ? On : Off) : null) },
            new[]
            {
                new CommandBinding(commandTopic, payload => payload.Trim().ToUpperInvariant() switch
                {
                    "ON" => new DaikinCommand { Holiday = true },
                    "OFF" => new DaikinCommand { Holiday = false },
                    _ => null,
                }),
            });
    }

    // ---- energy ------------------------------------------------------------------------------------------------------

    private void AddEnergy(
        DaikinUnitInfo info,
        Action<string, string, Func<string, string, string?, MappedEntity>> add,
        Func<string, string, Func<DaikinSnapshot, string?>, string?, string?, string?, string?, bool, Dictionary<string, object?>?, MappedEntity> sensor)
    {
        if (!_options.EnableEnergy || !info.SupportsEnergy)
        {
            return;
        }

        void Energy(string key, string name, Func<DaikinEnergy, double?> value, string? stateClass)
        {
            add(key, name, (k, n, _) => sensor(k, n, s => s.Energy is { } e ? Number(value(e), 1) : null, "kWh", "energy", stateClass, null, false, null));
        }

        add("today_runtime", "Runtime today", (k, n, _) =>
            sensor(k, n, s => s.Energy is { } e ? Number(e.TodayRuntime, 0) : null, "min", "duration", "total_increasing", "mdi:timer-outline", false, null));
        Energy("energy_today", "Energy today", e => e.Today, "total_increasing");
        Energy("energy_today_heat", "Heating energy today", e => e.TodayHeat, "total_increasing");
        Energy("energy_today_cool", "Cooling energy today", e => e.TodayCool, "total_increasing");
        // A rolling 7-day window goes down as well as up: no state class, Home Assistant would count the drops as resets.
        Energy("energy_week", "Energy last 7 days", e => e.Week, null);
        Energy("energy_month", "Energy this month", e => e.Month, "total_increasing");
        Energy("energy_month_heat", "Heating energy this month", e => e.MonthHeat, "total_increasing");
        Energy("energy_month_cool", "Cooling energy this month", e => e.MonthCool, "total_increasing");
        Energy("energy_year", "Energy this year", e => e.Year, "total_increasing");
        Energy("energy_year_heat", "Heating energy this year", e => e.YearHeat, "total_increasing");
        Energy("energy_year_cool", "Cooling energy this year", e => e.YearCool, "total_increasing");
    }

    // ---- declared entities -------------------------------------------------------------------------------------------

    private MappedEntity Manual(DaikinUnitOptions unit, EntityDefinition d)
    {
        var label = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name;
        if (string.IsNullOrWhiteSpace(d.Name))
        {
            throw new EntityConfigurationException("Every entity needs a Name.");
        }

        if (string.IsNullOrWhiteSpace(d.Id) || !IdentifierPattern().IsMatch(d.Id))
        {
            throw new EntityConfigurationException($"Entity '{label}': Id must contain only letters, digits, '_' or '-'.");
        }

        var component = d.Component.Trim().ToLowerInvariant();
        if (!ManualComponents.Contains(component))
        {
            throw new EntityConfigurationException($"Entity '{label}': Component must be one of {string.Join(", ", ManualComponents)}.");
        }

        var source = (d.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (!ManualSources.Contains(source))
        {
            throw new EntityConfigurationException($"Entity '{label}': Source must be one of {string.Join(", ", ManualSources)}.");
        }

        if (string.IsNullOrWhiteSpace(d.Field))
        {
            throw new EntityConfigurationException($"Entity '{label}': Field is required (the key to read in the '{source}' answer).");
        }

        var field = d.Field.Trim();
        var key = $"manual_{d.Id}";
        var topic = _topics.Join(unit.Id, d.Id, "state");
        var invert = d.Invert;
        var decimals = d.Decimals;

        string? Raw(DaikinSnapshot s)
        {
            var values = source switch { "control" => s.Control, "sensor" => s.Sensor, _ => s.Basic };
            return values.TryGetValue(field, out var value) ? value : null;
        }

        Func<DaikinSnapshot, string?> payload = component == "binary_sensor"
            ? s => Raw(s) is { } raw && TryOnOff(raw, out var on) ? (on ^ invert ? On : Off) : null
            : s => Raw(s) is { } raw ? (decimals is { } dec && DaikinResponseParser.ParseNumber(raw) is { } number ? Number(number, dec) : raw) : null;

        return new MappedEntity(
            key,
            new ComponentDescriptor
            {
                Key = key,
                Platform = component,
                UniqueId = $"{NodeIdOf(unit.Id)}_{key}",
                Name = d.Name.Trim(),
                StateTopic = topic,
                DeviceClass = d.DeviceClass,
                UnitOfMeasurement = component == "sensor" ? d.Unit : null,
                StateClass = component == "sensor" ? d.StateClass : null,
                Icon = d.Icon,
                Area = string.IsNullOrWhiteSpace(d.Area) ? null : d.Area.Trim(),
            },
            new[] { new StateBinding(topic, payload) },
            Array.Empty<CommandBinding>());
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>A number as published on MQTT: invariant culture, fixed decimals; null when there is no value.</summary>
    internal static string? Number(double? value, int decimals) =>
        value is { } v && double.IsFinite(v)
            ? Math.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Home Assistant sends the target in 0.5 steps within the bounds the unit accepts.</summary>
    internal static double ClampToStep(double value, double min, double max) =>
        Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, min, max);

    private static bool HasAlert(DaikinSnapshot s)
    {
        var alert = s.Control.GetValueOrDefault("alert");
        var err = s.Sensor.GetValueOrDefault("err") ?? s.Basic.GetValueOrDefault("err");
        return (alert is not null && alert != "255") || (err is not null && err != "0");
    }

    private static bool TryOnOff(string text, out bool on)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1" or "on" or "true":
                on = true;
                return true;
            case "0" or "off" or "false":
                on = false;
                return true;
            default:
                on = false;
                return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdentifierPattern();
}
