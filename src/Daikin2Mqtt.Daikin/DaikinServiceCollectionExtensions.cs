using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Daikin;

public static class DaikinServiceCollectionExtensions
{
    public static IServiceCollection AddDaikin(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DaikinOptions>(configuration.GetSection(DaikinOptions.SectionName));
        services.Configure<DeviceOptions>(configuration.GetSection(DeviceOptions.SectionName));
        services.Configure<EntitiesOptions>(configuration);

        services.AddSingleton<DaikinDiagnostics>();
        services.AddSingleton<Mapping.UnitEntityBuilder>();

        // The modules are plain HTTP on the LAN; the timeout is handled per request by DaikinHttpApi.
        services.AddHttpClient("daikin")
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<IDaikinClientFactory, DaikinClientFactory>();
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IDaikinClientFactory>();
            return new DaikinUnitSet(DaikinClientFactory.UnitsOf(sp.GetRequiredService<IOptions<DaikinOptions>>().Value).Select(factory.Create));
        });
        services.AddSingleton<IDaikinScanner, DaikinScanner>();
        return services;
    }
}
