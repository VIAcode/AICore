using System.Diagnostics.Metrics;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.Common.Monitoring;

public class MetricsAccessor : IMetricsAccessor
{
    public const string METRICS_PREFIX = "aicore";

    private readonly Meter _meter;

    public MetricsAccessor(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(METRICS_PREFIX);
    }

    public void SetHistogramValue<T>(string metricName, T value, string? unit = null, string? description = null,
        params string[] tags) where T : struct
    {
        // works as "get or create"
        var histogram = _meter.CreateHistogram<T>($"{METRICS_PREFIX}.{metricName}", unit, description);
        histogram.Record(value, NormalizeTags(tags));
    }

    public void SetCounterValue<T>(string metricName, T value, string? unit = null, string? description = null,
        params string[] tags) where T : struct
    {
        var counter = _meter.CreateCounter<T>($"{METRICS_PREFIX}.{metricName}", unit, description);
        counter.Add(value, NormalizeTags(tags));
    }

    public void SetGaugeValue<T>(string metricName, T value, string? unit = null, string? description = null,
        params string[] tags) where T : struct
    {
        var gauge = _meter.CreateGauge<T>($"{METRICS_PREFIX}.{metricName}", unit, description);
        gauge.Record(value, NormalizeTags(tags));
    }

    public void SetUpDownValue<T>(string metricName, T value, string? unit = null, string? description = null,
        params string[] tags) where T : struct
    {
        var upDown = _meter.CreateUpDownCounter<T>($"{METRICS_PREFIX}.{metricName}", unit, description);
        upDown.Add(value, NormalizeTags(tags));
    }

    private KeyValuePair<string, object?>[] NormalizeTags(string[]? tags)
    {
        if (tags.Length % 2 != 0)
            throw new AiCoreUiException("Tags must be provided in key-value pairs.");
        
        return tags == null
            ? ([])
            : Enumerable.Range(0, tags.Length / 2)
                .Select(i => new KeyValuePair<string, object?>($"{METRICS_PREFIX}.{tags[i * 2]}", tags[i * 2 + 1])).ToArray();
    }
}

public interface IMetricsAccessor
{
    void SetHistogramValue<T>(string metricName, T value, string? unit = null, string? description = null, params string[] tags) where T : struct;

    void SetCounterValue<T>(string metricName, T value, string? unit = null, string? description = null, params string[] tags) where T : struct;

    void SetGaugeValue<T>(string metricName, T value, string? unit = null, string? description = null, params string[] tags) where T : struct;

    void SetUpDownValue<T>(string metricName, T value, string? unit = null, string? description = null, params string[] tags) where T : struct;
}
