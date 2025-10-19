using Json.Schema.Generation;
using Json.Schema.Generation.Intents;
using Newtonsoft.Json.Linq;

namespace AiCoreApi.Common.Monitoring;


public class MonitoringConfig
{
    private readonly string _appSettings = File.ReadAllText("appsettings.json");
    private static string MONITORING_SETTINGS_PREFIX = "Monitoring";

    private T GetOtelValue<T>(string key) => GetOtelValue(key, default(T));
    private T GetOtelValue<T>(string key, T defaultValue)
    {
        var value = Environment.GetEnvironmentVariable($"{MONITORING_SETTINGS_PREFIX}_{key}".ToUpper());
        if (value != null)
            return (T)Convert.ChangeType(value, typeof(T));

        var val = JObject.Parse(_appSettings).SelectToken($"{MONITORING_SETTINGS_PREFIX}{key}");
        if (val != null)
            return val.ToObject<T>()!;
        if (defaultValue != null)
            return defaultValue;
        return default;
    }

    private T GetLoggingValue<T>(string key, T defaultValue)
    {
        var value = Environment.GetEnvironmentVariable($"{MONITORING_SETTINGS_PREFIX}_{key}".ToUpper());
        if (value != null)
            return (T)Convert.ChangeType(value, typeof(T));

        var val = JObject.Parse(_appSettings).SelectToken($"{MONITORING_SETTINGS_PREFIX}{key}");
        if (val != null)
            return val.ToObject<T>()!;
        if (defaultValue != null)
            return defaultValue;
        return default;
    }

    private Dictionary<string, LogLevel> GetLogLevelsValue()
    {
        var envVar = Environment.GetEnvironmentVariable($"{MONITORING_SETTINGS_PREFIX}_LOGLEVELS".ToUpper());
        if (!string.IsNullOrWhiteSpace(envVar))
        {
            try
            {
                // Format:
                // 1) JSON: {"Microsoft":"Warning","Default":"Information"}
                // 2) CSV: Microsoft=Warning,Default=Information
                if (envVar.TrimStart().StartsWith("{"))
                {
                    var jObj = JObject.Parse(envVar);
                    return jObj.Properties()
                        .ToDictionary(
                            p => p.Name,
                            p => Enum.TryParse<LogLevel>(p.Value.ToString(), true, out var level)
                                ? level
                                : LogLevel.None,
                            StringComparer.OrdinalIgnoreCase
                        );
                }
                else
                {
                    return envVar
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries))
                        .Where(p => p.Length == 2)
                        .ToDictionary(
                            p => p[0],
                            p => Enum.TryParse<LogLevel>(p[1], true, out var level) ? level : LogLevel.None,
                            StringComparer.OrdinalIgnoreCase
                        );
                }
            }
            catch
            {
                // Do nothing, return empty dictionary
            }
        }
        return new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
    }

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Common)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("OpenTelemetry")]
    [Tooltip("Enable OpenTelemetry tracing and metrics.")]
    public bool EnableOpenTelemetry => GetOtelValue<bool>(nameof(EnableAppInsights), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Azure Application Insights")]
    [Tooltip("Send telemetry to Azure Application Insights.")]
    public bool EnableAppInsights => GetOtelValue<bool>(nameof(EnableAppInsights), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("App Insights Connection String")]
    [Tooltip("Connection string for Azure Application Insights.")]
    public string? AppInsightsConnectionString => GetOtelValue<string?>(nameof(AppInsightsConnectionString));

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("PostgreSQL")]
    [Tooltip("Trace and monitor PostgreSQL interactions.")]
    public bool EnableNpgsqlInstrumentation => GetOtelValue<bool>(nameof(EnableNpgsqlInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("ASP.NET Core")]
    [Tooltip("Trace and monitor ASP.NET Core requests.")]
    public bool EnableAspNetCoreInstrumentation => GetOtelValue<bool>(nameof(EnableAspNetCoreInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Instrumentation)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("HTTP Client")]
    [Tooltip("Trace and monitor HTTP Client calls.")]
    public bool EnableHttpClientInstrumentation => GetOtelValue<bool>(nameof(EnableHttpClientInstrumentation), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Filters)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Activity Sources Filter")]
    [Tooltip("Comma-separated list of activity sources to include.")]
    public string? ActivitySourcesFilter => GetOtelValue<string?>(nameof(ActivitySourcesFilter));

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Filters)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Metrics Filter")]
    [Tooltip("Comma-separated list of metrics to include.")]
    public string? MetricsFilter => GetOtelValue<string?>(nameof(MetricsFilter));

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("OTLP Exporter")]
    [Tooltip("Export telemetry via OpenTelemetry Protocol.")]
    public bool EnableOtlpExporter => GetOtelValue<bool>(nameof(EnableOtlpExporter), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Console Exporter")]
    [Tooltip("Write telemetry to the console.")]
    public bool EnableConsoleExporter => GetOtelValue<bool>(nameof(EnableConsoleExporter), false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Exporters)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Prometheus Exporter")]
    [Tooltip("Expose metrics in Prometheus format.")]
    public bool EnablePrometheusExporter => GetOtelValue<bool>(nameof(EnablePrometheusExporter), false);


    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Console Log Level")]
    [Tooltip("Set minimum log level for console output.")]
    public string LogLevelConsole => GetLoggingValue<string>(nameof(LogLevelConsole), "Information");

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Login/Logout")]
    [Tooltip("Log user login and logout events.")]
    public bool LogLoginLogout => GetLoggingValue<bool>("LogLoginLogout", false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Access Token Checks")]
    [Tooltip("Log access token validation events.")]
    public bool LogAccessTokenCheck => GetLoggingValue<bool>("LogAccessTokenCheck", false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent Runs")]
    [Tooltip("Log Agent execution events.")]
    public bool LogAgentRun => GetLoggingValue<bool>("LogAgentRun", false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent Results")]
    [Tooltip("Log Agent result outputs.")]
    public bool LogAgentResult => GetLoggingValue<bool>("LogAgentResult", false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent PII")]
    [Tooltip("Log Agent input/output with PII data.")]
    public bool LogAgentPii => GetLoggingValue<bool>("LogAgentPii", false);

    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log NuGet Loads")]
    [Tooltip("Log NuGet package load events.")]
    public bool LogNugetPackageLoad => GetLoggingValue<bool>("LogNugetPackageLoad", false);


    [MonitoringCategory(MonitoringCategoryAttribute.ConfigCategoryEnum.LogLevels)]
    [Description("Component Log Levels")]
    [Tooltip("Set log levels for individual components.")]
    public Dictionary<string, LogLevel> LogLevels => GetLogLevelsValue();
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
        [System.ComponentModel.Description("Common")]
        Common,
        [System.ComponentModel.Description("Instrumentation")]
        Instrumentation,
        [System.ComponentModel.Description("Filters")]
        Filters,
        [System.ComponentModel.Description("Exporters")]
        Exporters,
        [System.ComponentModel.Description("Log Levels")]
        LogLevels,
        [System.ComponentModel.Description("Logging")]
        Logging
    }
}