using Azure.Monitor.OpenTelemetry.AspNetCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AiCoreApi.Common.Monitoring;

public static class ServiceCollectionExtensions
{

    public static void AddMonitoring(this IServiceCollection services, MonitoringConfig monitoringConfig)
    {
        ConfigureLogs(services, monitoringConfig);

        if (monitoringConfig.EnableOpenTelemetry)
        {
            SetTracesFilter(services, monitoringConfig);

            var builder = services.AddOpenTelemetry();
            if (monitoringConfig.EnableAppInsights)
            {
                builder.UseAzureMonitor(config =>
                {
                    config.ConnectionString = monitoringConfig.AppInsightsConnectionString;
                }); // includes AddAspNetCoreInstrumentation, AddHttpClientInstrumentation, AddHttpClientAndServerMetrics, AddAzureMonitorTraceExporter,  AddAzureMonitorMetricExporter
            }

            SetInstrumentation(builder, monitoringConfig);

            SetMetrics(builder, monitoringConfig);

            SetMetricsFilter(builder, monitoringConfig);

            SetExporters(builder, monitoringConfig);
        }
    }

    private static void SetTracesFilter(IServiceCollection services, MonitoringConfig monitoringConfig)
    {
        if (monitoringConfig.ActivitySourcesFilter != null)
        {
            services.ConfigureOpenTelemetryTracerProvider((sp, builder) => builder.AddProcessor(new ActivityFilteringProcessor(monitoringConfig.ActivitySourcesFilter)));
        }
    }

    private static void ConfigureLogs(IServiceCollection services, MonitoringConfig monitoringConfig)
    {
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();

            if (monitoringConfig.EnableOpenTelemetry)
            {
                loggingBuilder.AddOpenTelemetry();
            }

            if (monitoringConfig.LogLevels != null)
            {
                foreach (var setting in monitoringConfig.LogLevels)
                {
                    loggingBuilder.AddFilter(setting.Key, setting.Value);
                }
            }

            loggingBuilder.AddConsole(opt => opt.LogToStandardErrorThreshold = Enum.Parse<LogLevel>(monitoringConfig.LogLevelConsole));
        });
    }

    private static void SetMetrics(OpenTelemetryBuilder builder, MonitoringConfig monitoringConfig)
    {
        builder.WithMetrics(metricsBuilder =>
        {
            metricsBuilder.AddMeter(MetricsAccessor.METRICS_PREFIX);

            if (monitoringConfig.EnableNpgsqlInstrumentation)
            {
                metricsBuilder.AddNpgsqlInstrumentation();
            }

            if (!monitoringConfig.EnableAppInsights && monitoringConfig.EnableAspNetCoreInstrumentation)
            {
                metricsBuilder.AddAspNetCoreInstrumentation();
            }

            if (!monitoringConfig.EnableAppInsights && monitoringConfig.EnableHttpClientInstrumentation)
            {
                metricsBuilder.AddHttpClientInstrumentation();
            }
        });
    }

    private static void SetMetricsFilter(OpenTelemetryBuilder builder, MonitoringConfig monitoringConfig)
    {
        if (monitoringConfig.MetricsFilter != null)
        {
            builder.WithMetrics(metricsBuilder =>
            {
                var filter = new SourceFilter(monitoringConfig.MetricsFilter);
                metricsBuilder.AddView(instrument => {
                    return filter.IsMatch(instrument.Name) ? null : MetricStreamConfiguration.Drop;
                });
            });
        }
    }

    private static void SetExporters(OpenTelemetryBuilder builder, MonitoringConfig monitoringConfig)
    {
        builder.WithTracing(tracesBuilder =>
        {
            if (monitoringConfig.EnableConsoleExporter)
            {
                tracesBuilder.AddConsoleExporter();
            }

            if (monitoringConfig.EnableOtlpExporter)
            {
                tracesBuilder.AddOtlpExporter();
            }
        });

        builder.WithMetrics(metricsBuilder =>
        {
            // exporters
            if (monitoringConfig.EnablePrometheusExporter)
            {
                metricsBuilder.AddPrometheusExporter();
            }

            if (monitoringConfig.EnableConsoleExporter)
            {
                metricsBuilder.AddConsoleExporter();
            }

            if (monitoringConfig.EnableOtlpExporter)
            {
                metricsBuilder.AddOtlpExporter();
            }
        });
    }

    private static void SetInstrumentation(OpenTelemetryBuilder builder, MonitoringConfig monitoringConfig)
    {
        builder.WithTracing(tracesBuilder =>
        {
            if (monitoringConfig.EnableNpgsqlInstrumentation)
            {
                tracesBuilder.AddNpgsql();
            }

            if (!monitoringConfig.EnableAppInsights && monitoringConfig.EnableAspNetCoreInstrumentation)
            {
                tracesBuilder.AddAspNetCoreInstrumentation();
            }

            if (!monitoringConfig.EnableAppInsights && monitoringConfig.EnableHttpClientInstrumentation)
            {
                tracesBuilder.AddHttpClientInstrumentation();
            }
        });
    }
}


internal class SourceFilter
{
    private Regex? _sourcesRegex;

    private HashSet<string>? _sources = new HashSet<string>();


    public SourceFilter(string filter)
    {
        var activitySourcesItems = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (filter.Contains('*') || filter.Contains('?'))
        {
            _sourcesRegex = GetWildcardRegex(activitySourcesItems);
        }
        else
        {
            _sources = new HashSet<string>(activitySourcesItems, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static Regex GetWildcardRegex(IEnumerable<string> patterns)
    {
        var convertedPattern = string.Join("|",
            from p in patterns
            select "(?:" + Regex.Escape(p).Replace("\\*", ".*")
            .Replace("\\?", ".") + ')');

        return new Regex("^(?:" + convertedPattern + ")$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public bool IsMatch(string source)
    {
        return (_sourcesRegex != null && _sourcesRegex.IsMatch(source)) ||
            (_sources != null && _sources.Contains(source));
    }

}

internal class ActivityFilteringProcessor : BaseProcessor<Activity>
{
    private SourceFilter _sourceFilter;

    public ActivityFilteringProcessor(string filter)
    {
        _sourceFilter = new SourceFilter(filter);
    }

    public override void OnStart(Activity activity)
    {
        activity.IsAllDataRequested = _sourceFilter.IsMatch(activity.Source.Name);
    }
}
