namespace Daikin2Mqtt.Daikin;

/// <summary>
/// Everything the bridge needs from ONE indoor unit, whatever the firmware family behind it. The classic HTTP driver is
/// <see cref="DaikinPollingClient"/>; a driver for the firmware 2.8.0+ API (<c>/dsiot/multireq</c>, JSON) would implement
/// this interface too and be chosen by <see cref="IDaikinClientFactory"/>.
/// </summary>
public interface IDaikinClient
{
    DaikinUnitOptions Unit { get; }

    /// <summary>True while the unit answers (after the grace period for a unit that stops answering).</summary>
    bool IsAvailable { get; }

    /// <summary>Identity and capabilities; null until the unit has answered once.</summary>
    DaikinUnitInfo? Info { get; }

    /// <summary>The latest known state; null until the unit has answered once.</summary>
    DaikinSnapshot? Snapshot { get; }

    /// <summary>Raised when <see cref="Info"/> becomes known (the entities of the unit can then be built).</summary>
    event Action<DaikinUnitInfo>? InfoAvailable;

    /// <summary>Raised after every successful poll or command, with the complete state; handlers must not block.</summary>
    event Action<DaikinSnapshot>? StateChanged;

    /// <summary>Raised when <see cref="IsAvailable"/> changes.</summary>
    event Action<bool>? AvailabilityChanged;

    /// <summary>Polls the unit until cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>
    /// Sends a command. Throws when the unit refuses or does not answer. The new state is read back at once and
    /// reported through <see cref="StateChanged"/>.
    /// </summary>
    Task SendAsync(DaikinCommand command, CancellationToken ct);
}

/// <summary>Creates the client of a unit; where the firmware strategy lives.</summary>
public interface IDaikinClientFactory
{
    IDaikinClient Create(DaikinUnitOptions unit);
}

/// <summary>The units of the configuration, each with its client.</summary>
public sealed class DaikinUnitSet
{
    public DaikinUnitSet(IEnumerable<IDaikinClient> clients) => Clients = clients.ToList();

    public IReadOnlyList<IDaikinClient> Clients { get; }

    /// <summary>Runs every unit until cancelled.</summary>
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Clients.Select(c => c.RunAsync(ct)));
}
