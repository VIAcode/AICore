using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Json.Schema.Generation;
using Json.Schema.Generation.Intents;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using DescriptionAttribute = Json.Schema.Generation.DescriptionAttribute;

namespace AiCoreApi.Common.Monitoring;


public class MonitoringConfig
{
    private DateTime _nextRefresh = DateTime.MinValue;
    private ConcurrentDictionary<string, string> _logLevelConfigValues = new();
    private ConcurrentDictionary<string, string> _openTelemetryConfigValues = new();
    private readonly ISettingsProcessor _settingsProcessor;
    private const int RefreshTimeSec = 15;
    private readonly object _lock = new();
    private readonly string _appSettings = File.ReadAllText("appsettings.json");
    private static string MONITORING_SETTINGS_PREFIX = "Monitoring";


    public MonitoringConfig(ISettingsProcessor settingsProcessor)
    {
        _settingsProcessor = settingsProcessor;
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (DateTime.Now > _nextRefresh)
            {
                _openTelemetryConfigValues = new ConcurrentDictionary<string, string>(_settingsProcessor.Get(SettingType.OpenTelemetry));
                _logLevelConfigValues = new ConcurrentDictionary<string, string>(_settingsProcessor.Get(SettingType.LogLevel));

                _nextRefresh = DateTime.Now.AddSeconds(RefreshTimeSec);
            }
        }
    }


    private T GetValue<T>(string key) => GetValue(key, default(T));
    private T GetValue<T>(string key, T defaultValue)
    {
        if (DateTime.Now > _nextRefresh)
            Reset();

        if (!_openTelemetryConfigValues.TryGetValue(key, out var value))
        {
            value = Environment.GetEnvironmentVariable($"{MONITORING_SETTINGS_PREFIX}_{key}".ToUpper());
        }
        if (value != null)
            return (T)Convert.ChangeType(value, typeof(T));

        var val = JObject.Parse(_appSettings).SelectToken($"{MONITORING_SETTINGS_PREFIX}{key}");
        if (val != null)
            return val.ToObject<T>()!;
        if (defaultValue != null)
            return defaultValue;
        return default;
    }

    private Dictionary<string, LogLevel> GetLogLevelValue()
    {
        if (DateTime.Now > _nextRefresh)
            Reset();

        return _logLevelConfigValues.ToDictionary(kv => kv.Key,
            kv => Enum.TryParse<LogLevel>(kv.Value, out var result) ? result : Microsoft.Extensions.Logging.LogLevel.None);
    }


    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [Description("Enable or disable Azure Application Insights for monitoring and telemetry.")]
    [Tooltip("When enabled, Azure Application Insights collects and analyzes telemetry data to monitor application performance and usage.")]
    public bool EnableAppInsights => GetValue<bool>(nameof(EnableAppInsights), true);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [Description("Specifies the connection string for Azure Application Insights.")]
    [Tooltip("Provide the connection string to connect your application to Azure Application Insights for telemetry data collection.")]
    public string? AppInsightsConnectionString => GetValue<string?>(nameof(AppInsightsConnectionString), "InstrumentationKey=50e7c903-c15a-4b46-b9f5-1919d0688856;IngestionEndpoint=https://eastus-8.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/;ApplicationId=087833e8-6d07-4090-bbaa-f633a9239950");

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [Description("Enable or disable PostgreSQL instrumentation for tracing and metrics.")]
    [Tooltip("When enabled, collects traces and metrics for PostgreSQL database interactions.")]
    public bool EnableNpgsqlInstrumentation => GetValue<bool>(nameof(EnableNpgsqlInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [Description("Enable or disable ASP.NET Core instrumentation for tracing and metrics.")]
    [Tooltip("When enabled, collects traces and metrics for ASP.NET Core applications. Ignored if Azure Application Insights is enabled.")]
    public bool EnableAspNetCoreInstrumentation => GetValue<bool>(nameof(EnableAspNetCoreInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [Description("Enable or disable HTTP Client instrumentation for tracing and metrics.")]
    [Tooltip("When enabled, collects traces and metrics for HTTP Client requests. Ignored if Azure Application Insights is enabled.")]
    public bool EnableHttpClientInstrumentation => GetValue<bool>(nameof(EnableHttpClientInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.LogLevels)]
    [Description("Specifies the log levels for different components.")]
    [Tooltip("Define the logging levels (e.g., Debug, Information, Warning, Error, Critical) for various components in the application.")]
    public Dictionary<string, LogLevel> LogLevel => GetLogLevelValue();

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Filters)]
    [Description("Log Level Threshold for Standard Output")]
    [Tooltip("Log Level is used to specify the level of logging that the system should use. The log level determines the amount of information that is logged by the system. The available log levels are: Debug, Information, Warning, Error, and Critical.")]
    public string LogLevelConsole => GetValue<string>(nameof(LogLevelConsole), "Information");


    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Filters)]
    [Description("Filter activity sources for tracing.")]
    [Tooltip("Specify a comma-separated list of activity sources to include into tracing.")]
    public string? ActivitySourcesFilter => GetValue<string?>(nameof(ActivitySourcesFilter), "AiCoreApi.SemanticKernel.Agents.*");

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Filters)]
    [Description("Filter metrics for monitoring.")]
    [Tooltip("Specify a comma-separated list of metrics to include or exclude from monitoring.")]
    public string? MetricsFilter => GetValue<string?>(nameof(MetricsFilter), "aicore.*");

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [Description("Enable or disable the OpenTelemetry Protocol (OTLP) exporter.")]
    [Tooltip("When enabled, exports telemetry data using the OpenTelemetry Protocol (OTLP).")]
    public bool EnableOtlpExporter => GetValue<bool>(nameof(EnableOtlpExporter), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [Description("Enable or disable the Console exporter.")]
    [Tooltip("When enabled, exports telemetry data to the console for debugging and analysis.")]
    public bool EnableConsoleExporter => GetValue<bool>(nameof(EnableConsoleExporter), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [Description("Enable or disable the Prometheus exporter.")]
    [Tooltip("When enabled, exports telemetry data in a format compatible with Prometheus for monitoring.")]
    public bool EnablePrometheusExporter => GetValue<bool>(nameof(EnablePrometheusExporter), false);

}


[AttributeUsage(AttributeTargets.Property)]
public class MonitoringCategoryAttribute : Attribute, IAttributeHandler
{
    public MonitoringCategoryAttribute() { }

    public ConfigCategoryEnum Category { get; }
    public MonitoringCategoryAttribute(ConfigCategoryEnum category)
    {
        Category = category;
    }

    void IAttributeHandler.AddConstraints(SchemaGenerationContextBase context, Attribute attribute)
    {
        context.Intents.Add(new DescriptionIntent(Category.ToString()));
    }

    public enum ConfigCategoryEnum
    {
        [Description("Common")]
        Common,
        [Description("Instrumentation")]
        Instrumentation,
        [Description("Filters")]
        Filters,
        [Description("Exporters")]
        Exporters,
        [Description("Log Levels")]
        LogLevels
    }
}