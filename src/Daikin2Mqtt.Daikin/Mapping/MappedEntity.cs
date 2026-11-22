using Daikin2Mqtt.Core.Discovery;

namespace Daikin2Mqtt.Daikin.Mapping;

/// <summary>State topic and how to compute its payload from a snapshot of the unit (null = nothing to publish).</summary>
public sealed record StateBinding(string Topic, Func<DaikinSnapshot, string?> ToPayload);

/// <summary>Command topic and how to turn a payload into a command for the unit (null = payload not understood).</summary>
public sealed record CommandBinding(string Topic, Func<string, DaikinCommand?> Parse);

/// <summary>One Home Assistant component of a unit with its topic bindings.</summary>
public sealed record MappedEntity(
    string Key,
    ComponentDescriptor Component,
    IReadOnlyList<StateBinding> States,
    IReadOnlyList<CommandBinding> Commands);

public sealed class EntityConfigurationException : Exception
{
    public EntityConfigurationException(string message)
        : base(message)
    {
    }
}
