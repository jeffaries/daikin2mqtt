using Microsoft.Extensions.Logging;

namespace Daikin2Mqtt.Daikin;

/// <summary>
/// Counts answers that could not be understood. The local API is not official and a firmware update may change it, so
/// failures are logged at Debug level individually and summarised as a Warning.
/// </summary>
public sealed class DaikinDiagnostics
{
    private readonly object _gate = new();
    private long _total;
    private long _sinceReport;
    private DateTime _lastReport = DateTime.UtcNow;
    private string? _lastSample;

    /// <summary>Total number of ignored answers since startup.</summary>
    public long TotalRejected
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public void RecordRejected(string reason, string? sample, ILogger? logger = null)
    {
        var truncated = sample is { Length: > 200 } ? sample[..200] + "..." : sample;
        lock (_gate)
        {
            _total++;
            _sinceReport++;
            _lastSample = truncated;
        }

        logger?.LogDebug("Ignored Daikin answer ({Reason}): {Sample}", reason, truncated);
    }

    /// <summary>Emits one aggregated Warning if failures happened and <paramref name="interval"/> elapsed.</summary>
    public void ReportIfDue(ILogger logger, TimeSpan interval)
    {
        long count;
        long total;
        string? sample;
        lock (_gate)
        {
            if (_sinceReport == 0 || DateTime.UtcNow - _lastReport < interval)
            {
                return;
            }

            count = _sinceReport;
            total = _total;
            sample = _lastSample;
            _sinceReport = 0;
            _lastReport = DateTime.UtcNow;
        }

        logger.LogWarning(
            "{Count} Daikin answer(s) could not be understood recently (total {Total}). Last sample: {Sample}. " +
            "The module firmware may have changed its API.",
            count, total, sample);
    }
}
