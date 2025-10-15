using AiCoreApi.Common;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.Monitoring;
using Json.Schema.Generation;
using Category = AiCoreApi.Common.Monitoring.MonitoringCategoryAttribute.ConfigCategoryEnum;


namespace AiCoreApi.Services.ControllersServices;

public class MonitoringSettingsService : IMonitoringSettingsService
{
    private readonly MonitoringConfig _monitoringConfig;
    private readonly ISettingsProcessor _settingsProcessor;
    private readonly IInstanceSync _instanceSync;

    public MonitoringSettingsService(
        ISettingsProcessor settingsProcessor,
        MonitoringConfig monitoringConfig,
        IInstanceSync instanceSync)
    {
        _settingsProcessor = settingsProcessor;
        _monitoringConfig = monitoringConfig;
        _instanceSync = instanceSync;
    }

    public MonitoringSettingsViewModel Get()
    {
        return new MonitoringSettingsViewModel
        {
            OpenTelemetrySettings = GetSettingsViewModel(SettingType.OpenTelemetry, Category.Instrumentation, Category.Exporters, Category.Filters),
            LoggingSettings = GetSettingsViewModel(SettingType.Logging, Category.Logging),
            LogLevelSettings = _settingsProcessor
                .Get(SettingType.LogLevel)
                .Select(s => new LogLevelSettingsViewModel { Category = s.Key, LogLevel = s.Value })
                .ToList(),
        };
    }

    public void Set(MonitoringSettingsViewModel settings)
    {
        var openTelemetrySettings = settings.OpenTelemetrySettings
            .ToDictionary(x => x.SettingId, x => x.Value);
        _settingsProcessor.Set(SettingType.OpenTelemetry, openTelemetrySettings);

        var loggingSettings = settings.LoggingSettings
            .ToDictionary(x => x.SettingId, x => x.Value);
        _settingsProcessor.Set(SettingType.Logging, loggingSettings);

        var logLevelSettings = settings.LogLevelSettings
            .ToDictionary(x => x.Category, x => x.LogLevel);
        _settingsProcessor.Set(SettingType.LogLevel, logLevelSettings);
    }

    public void Reboot()
    {
        _instanceSync.SetRestartNeeded();
    }

    private List<SettingsViewModel> GetSettingsViewModel(SettingType settingType, params Category[] categories)
    {
        var props = typeof(MonitoringConfig)
            .GetProperties()
            .ToList();

        var values = _settingsProcessor.Get(settingType);
        return props
                .Where(prop => categories.Contains(prop.GetCustomAttributes(false).OfType<MonitoringCategoryAttribute>().FirstOrDefault()?.Category ?? 0))
                .Select(prop => new SettingsViewModel
                {
                    SettingId = prop.Name,
                    Value = values.ContainsKey(prop.Name)
                        ? values[prop.Name]
                        : prop.GetValue(_monitoringConfig)?.ToString() ?? "",
                    Tooltip = prop.GetCustomAttributes(false).OfType<TooltipAttribute>().FirstOrDefault()?.TooltipText ?? "",
                    DateType = prop.GetCustomAttributes(false).OfType<DataTypeAttribute>().FirstOrDefault()?.DataType.ToString() ?? DataTypeAttribute.ConfigDataTypeEnum.String.ToString(),
                    Description = prop.GetCustomAttributes(false).OfType<DescriptionAttribute>().FirstOrDefault()?.Description ?? "",
                    Category = prop.GetCustomAttributes(false).OfType<MonitoringCategoryAttribute>().FirstOrDefault()?.Category.GetDescription() ?? Category.Common.GetDescription()
                })
                .ToList();
    }
}

public interface IMonitoringSettingsService
{
    MonitoringSettingsViewModel Get();
    void Set(MonitoringSettingsViewModel settingsViewModels);
    void Reboot();
}