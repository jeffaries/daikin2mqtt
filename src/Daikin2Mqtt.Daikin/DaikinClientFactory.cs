using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daikin2Mqtt.Daikin;

/// <summary>
/// Picks the driver of a unit. Only the classic API exists today (<see cref="DaikinPollingClient"/> over
/// <see cref="DaikinHttpApi"/>); a firmware 2.8.0+ driver would be selected here, per unit.
/// </summary>
public sealed class DaikinClientFactory : IDaikinClientFactory
{
    private readonly IHttpClientFactory _http;
    private readonly DaikinOptions _options;
    private readonly DaikinDiagnostics _diagnostics;
    private readonly ILoggerFactory _loggers;

    public DaikinClientFactory(
        IHttpClientFactory http, IOptions<DaikinOptions> options, DaikinDiagnostics diagnostics, ILoggerFactory loggers)
    {
        _http = http;
        _options = options.Value;
        _diagnostics = diagnostics;
        _loggers = loggers;
    }

    public IDaikinClient Create(DaikinUnitOptions unit)
    {
        if (_options.Fake)
        {
            return new FakeDaikinClient(unit);
        }

        var logger = _loggers.CreateLogger($"Daikin2Mqtt.Daikin.{unit.Id}");
        var api = new DaikinHttpApi(_http.CreateClient("daikin"), unit, _options, _diagnostics, logger);
        return new DaikinPollingClient(api, unit, _options, _diagnostics, logger);
    }

    /// <summary>The configured units; with <c>Fake</c> and no unit, two simulated ones.</summary>
    public static IReadOnlyList<DaikinUnitOptions> UnitsOf(DaikinOptions options) =>
        options.Units.Count == 0 && options.Fake
            ? new[]
            {
                new DaikinUnitOptions { Id = "salon", Host = "fake-salon", Name = "Clim salon", Area = "Salon" },
                new DaikinUnitOptions { Id = "chambre", Host = "fake-chambre", Name = "Clim chambre", Area = "Chambre" },
            }
            : options.Units;
}
