using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Core.State;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Daikin.Mapping;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Service;

/// <summary>
/// Glue between the Daikin units and MQTT. Each unit is one Home Assistant device, published as soon as the unit has
/// answered once (its entities depend on its capabilities), independently of the others. All events (unit states,
/// availability, MQTT connections, commands) are queued and handled one at a time, so publications never race.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
    private abstract record BridgeEvent;

    private sealed record InfoEvent(UnitRuntime Unit, DaikinUnitInfo Info) : BridgeEvent;

    private sealed record StateEvent(UnitRuntime Unit, DaikinSnapshot Snapshot) : BridgeEvent;

    private sealed record UnitAvailabilityEvent(UnitRuntime Unit, bool Online) : BridgeEvent;

    private sealed record MqttConnectedEvent : BridgeEvent;

    private sealed record CommandEvent(string Topic, string Payload) : BridgeEvent;

    /// <summary>What the bridge knows about one unit.</summary>
    private sealed class UnitRuntime(IDaikinClient client, AvailabilityManager availability, string nodeId)
    {
        public IDaikinClient Client { get; } = client;
        public AvailabilityManager Availability { get; } = availability;
        public string NodeId { get; } = nodeId;

        /// <summary>Null until the unit has answered and its entities have been built.</summary>
        public IReadOnlyList<MappedEntity>? Entities { get; set; }

        public IReadOnlyList<DiscoveryMessage>? Messages { get; set; }
        public DaikinSnapshot? Latest { get; set; }
        public string? LastSummary { get; set; }
        public string Id => Client.Unit.Id;
    }

    private readonly IMqttPublisher _mqtt;
    private readonly AvailabilityManager _availability;
    private readonly DiscoveryBuilder _discoveryBuilder;
    private readonly DiscoveryPublisher _discovery;
    private readonly StatePublisher _states;
    private readonly UnitEntityBuilder _entityBuilder;
    private readonly DeviceOptions _device;
    private readonly ILogger<BridgeWorker> _logger;
    private readonly MonitorStream? _monitor;
    private readonly List<UnitRuntime> _units = new();
    private readonly ConcurrentDictionary<string, (UnitRuntime Unit, CommandBinding Binding)> _commands = new(StringComparer.Ordinal);
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public BridgeWorker(
        DaikinUnitSet units,
        IMqttPublisher mqtt,
        TopicBuilder topics,
        AvailabilityManager availability,
        DiscoveryBuilder discoveryBuilder,
        DiscoveryPublisher discovery,
        StatePublisher states,
        UnitEntityBuilder entityBuilder,
        IOptions<DeviceOptions> device,
        ILogger<BridgeWorker> logger,
        MonitorStream? monitor = null)
    {
        _mqtt = mqtt;
        _availability = availability;
        _discoveryBuilder = discoveryBuilder;
        _discovery = discovery;
        _states = states;
        _entityBuilder = entityBuilder;
        _device = device.Value;
        _logger = logger;
        _monitor = monitor;

        // Wired in the constructor (not in ExecuteAsync): ExecuteAsync may start after the other hosted services
        // have already produced events, and those must not be lost.
        foreach (var client in units.Clients)
        {
            var unit = new UnitRuntime(
                client, new AvailabilityManager(mqtt, new TopicBuilder(topics.Join(client.Unit.Id))), entityBuilder.NodeIdOf(client.Unit.Id));
            _units.Add(unit);

            client.InfoAvailable += info => Post(new InfoEvent(unit, info));
            client.StateChanged += snapshot =>
            {
                MonitorState(unit, snapshot);
                Post(new StateEvent(unit, snapshot));
            };
            client.AvailabilityChanged += online => Post(new UnitAvailabilityEvent(unit, online));

            if (client.Info is { } known)
            {
                Post(new InfoEvent(unit, known));
            }

            if (client.Snapshot is { } current)
            {
                Post(new StateEvent(unit, current));
            }

            Post(new UnitAvailabilityEvent(unit, client.IsAvailable));
        }

        _mqtt.Connected += () =>
        {
            Post(new MqttConnectedEvent());
            return Task.CompletedTask;
        };
        _mqtt.MessageReceived += message =>
        {
            if (_commands.ContainsKey(message.Topic))
            {
                Post(new CommandEvent(message.Topic, message.Payload));
            }

            return Task.CompletedTask;
        };
    }

    private void Post(BridgeEvent e) => _events.Writer.TryWrite(e);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _discovery.HomeAssistantOnline = token => _states.RepublishAllAsync(token);
        // Retained devices of this node that are no longer published (a unit was removed, an area renamed) are cleared,
        // except those of a unit that has not answered yet: its entities are only known once it does.
        _discovery.StaleTopicPrefix = $"{_discoveryBuilder.Prefix}/device/{_device.NodeId}_";
        _discovery.KeepTopic = IsPendingUnitTopic;
        await _discovery.StartAsync(ct).ConfigureAwait(false);

        // The bridge itself is up: each unit adds its own availability on top (see BuildDiscovery).
        await _availability.SetAsync(true, ct).ConfigureAwait(false);
        _logger.LogInformation("Bridge started: {Count} unit(s) configured, waiting for them to answer", _units.Count);

        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error while handling {Event}", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _availability.SetAsync(false, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not publish offline on shutdown");
            }
        }
    }

    private bool IsPendingUnitTopic(string topic) =>
        _units.Any(u => u.Messages is null &&
            (topic == _discoveryBuilder.ConfigTopic(u.NodeId) || topic.StartsWith(_discoveryBuilder.AreaTopicPrefix(u.NodeId), StringComparison.Ordinal)));

    private async Task HandleAsync(BridgeEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case InfoEvent i:
                await OnInfoAsync(i.Unit, i.Info, ct).ConfigureAwait(false);
                break;

            case StateEvent s:
                s.Unit.Latest = s.Snapshot;
                if (s.Unit.Entities is not null)
                {
                    await PublishStatesAsync(s.Unit, s.Snapshot, ct).ConfigureAwait(false);
                }

                break;

            case UnitAvailabilityEvent a:
                _monitor?.Publish(MonitorKinds.System, unit: a.Unit.Id, detail: $"unit {a.Unit.Id} {(a.Online ? "online" : "offline")}");
                await a.Unit.Availability.SetAsync(a.Online, ct).ConfigureAwait(false);
                break;

            case MqttConnectedEvent:
                _monitor?.Publish(MonitorKinds.System, detail: "MQTT connected");
                // The broker may have lost its retained messages: send everything again.
                await _discovery.RepublishCurrentAsync(ct).ConfigureAwait(false);
                await _availability.PublishCurrentAsync(ct).ConfigureAwait(false);
                foreach (var unit in _units)
                {
                    await unit.Availability.PublishCurrentAsync(ct).ConfigureAwait(false);
                }

                await _states.RepublishAllAsync(ct).ConfigureAwait(false);
                break;

            case CommandEvent c when _commands.TryGetValue(c.Topic, out var target):
                var command = target.Binding.Parse(c.Payload);
                if (command is null)
                {
                    _logger.LogWarning("Ignoring unsupported payload '{Payload}' on {Topic}", c.Payload, c.Topic);
                    break;
                }

                // The state is NOT published here: it will be published when the unit confirms it (the client reads it back).
                _ = Task.Run(() => SendCommandAsync(target.Unit, command, c.Topic), ct);
                break;
        }
    }

    /// <summary>The unit answered for the first time: build its entities, subscribe to their commands, publish its device.</summary>
    private async Task OnInfoAsync(UnitRuntime unit, DaikinUnitInfo info, CancellationToken ct)
    {
        var entities = _entityBuilder.Build(info);
        unit.Entities = entities;
        unit.Messages = BuildDiscovery(unit, info, entities);

        foreach (var entity in entities)
        {
            foreach (var binding in entity.Commands)
            {
                _commands[binding.Topic] = (unit, binding);
                await _mqtt.SubscribeAsync(binding.Topic, ct).ConfigureAwait(false);
            }
        }

        await _discovery.PublishAsync(_units.Where(u => u.Messages is not null).SelectMany(u => u.Messages!).ToList(), ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Unit {Unit}: {Count} entities exposed on device '{Node}'", unit.Id, entities.Count, unit.NodeId);

        if (unit.Latest is { } latest)
        {
            await PublishStatesAsync(unit, latest, ct).ConfigureAwait(false);
        }
    }

    private async Task PublishStatesAsync(UnitRuntime unit, DaikinSnapshot snapshot, CancellationToken ct)
    {
        foreach (var entity in unit.Entities!)
        {
            foreach (var binding in entity.States)
            {
                var payload = binding.ToPayload(snapshot);
                if (payload is not null && await _states.PublishIfChangedAsync(binding.Topic, payload, ct).ConfigureAwait(false))
                {
                    _logger.LogDebug("{Topic} = {Payload}", binding.Topic, payload);
                }
            }
        }
    }

    private async Task SendCommandAsync(UnitRuntime unit, DaikinCommand command, string topic)
    {
        try
        {
            await unit.Client.SendAsync(command, CancellationToken.None).ConfigureAwait(false);
            _monitor?.Publish(MonitorKinds.DaikinCommand, topic: topic, unit: unit.Id, payload: command.ToString(), ok: true);
            _logger.LogInformation("Command sent to unit {Unit}: {Command} (from {Topic})", unit.Id, command, topic);
        }
        catch (Exception ex)
        {
            _monitor?.Publish(MonitorKinds.DaikinCommand, topic: topic, unit: unit.Id, payload: command.ToString(), ok: false, detail: ex.Message);
            _logger.LogError("Command to unit {Unit} failed (from {Topic}): {Reason}", unit.Id, topic, ex.Message);
            _logger.LogDebug(ex, "Command failure details");
        }
    }

    /// <summary>Only a change of what the unit reports is shown in the monitor: polling every few seconds would drown it.</summary>
    private void MonitorState(UnitRuntime unit, DaikinSnapshot snapshot)
    {
        if (_monitor is null)
        {
            return;
        }

        var summary = string.Join(' ', new[]
        {
            snapshot.Control.TryGetValue("pow", out var pow) ? $"pow={pow}" : null,
            snapshot.Control.TryGetValue("mode", out var mode) ? $"mode={mode}" : null,
            snapshot.Control.TryGetValue("stemp", out var stemp) ? $"stemp={stemp}" : null,
            snapshot.Control.TryGetValue("f_rate", out var fan) ? $"f_rate={fan}" : null,
            snapshot.Control.TryGetValue("f_dir", out var dir) ? $"f_dir={dir}" : null,
            snapshot.Sensor.TryGetValue("htemp", out var htemp) ? $"htemp={htemp}" : null,
            snapshot.Sensor.TryGetValue("otemp", out var otemp) ? $"otemp={otemp}" : null,
            snapshot.Sensor.TryGetValue("cmpfreq", out var cmp) ? $"cmpfreq={cmp}" : null,
        }.Where(s => s is not null));

        if (summary == unit.LastSummary)
        {
            return;
        }

        unit.LastSummary = summary;
        _monitor.Publish(MonitorKinds.DaikinState, unit: unit.Id, payload: summary);
    }

    private IReadOnlyList<DiscoveryMessage> BuildDiscovery(UnitRuntime unit, DaikinUnitInfo info, IReadOnlyList<MappedEntity> entities)
    {
        var options = info.Unit;
        var name = !string.IsNullOrWhiteSpace(options.Name) ? options.Name.Trim()
            : !string.IsNullOrWhiteSpace(info.Name) ? info.Name.Trim()
            : options.Id;
        var connections = info.FormattedMac is { } mac ? new[] { ("mac", mac) } : null;

        return _discoveryBuilder.BuildAll(
            unit.NodeId,
            new DeviceInfo(
                unit.NodeId, name, _device.Manufacturer, info.ModelLabel, info.Firmware,
                string.IsNullOrWhiteSpace(options.Area) ? null : options.Area.Trim(), Connections: connections),
            new OriginInfo("daikin2mqtt", _version),
            _availability.Topic,
            entities.Select(e => e.Component),
            new[] { unit.Availability.Topic });
    }
}
