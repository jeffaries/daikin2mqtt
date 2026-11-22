using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Daikin;

namespace Daikin2Mqtt.Service;

/// <summary>Keeps the MQTT connection alive.</summary>
public sealed class MqttWorker(MqttConnection mqtt) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => mqtt.RunAsync(stoppingToken);
}

/// <summary>Polls every Daikin unit.</summary>
public sealed class DaikinWorker(DaikinUnitSet units) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => units.RunAsync(stoppingToken);
}
