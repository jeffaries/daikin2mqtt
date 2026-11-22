using Daikin2Mqtt.Core.Discovery;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Core.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the reusable MQTT / Discovery / availability building blocks.</summary>
    public static IServiceCollection AddDaikin2MqttCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.SectionName));
        services.Configure<DiscoveryOptions>(configuration.GetSection(DiscoveryOptions.SectionName));

        services.AddSingleton(sp => new TopicBuilder(sp.GetRequiredService<IOptions<MqttOptions>>().Value.BaseTopic));
        services.AddSingleton<MqttConnection>();
        services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttConnection>());
        services.AddSingleton<AvailabilityManager>();
        services.AddSingleton(sp => new DiscoveryBuilder(sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value.Prefix));
        services.AddSingleton<DiscoveryPublisher>();
        services.AddSingleton<StatePublisher>();
        return services;
    }
}
