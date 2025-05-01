using AiCoreApi.Common;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.Monitoring;
using Json.Schema.Generation;


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
        var props = typeof(MonitoringConfig)
            .GetProperties()
            .ToList();
        _monitoringConfig.Reset();

        var otelSettingsValues = _settingsProcessor.Get(SettingType.OpenTelemetry);

        return new MonitoringSettingsViewModel
        {
            OpenTelemetrySettings = props
                .Where(prop => prop.GetCustomAttributes(false).OfType<MonitoringCategoryAttribute>().FirstOrDefault()?.Category != MonitoringCategoryAttribute.ConfigCategoryEnum.LogLevels)
                .Select(prop => new SettingsViewModel
                {
                    SettingId = prop.Name,
                    Value = otelSettingsValues.ContainsKey(prop.Name)
                        ? otelSettingsValues[prop.Name]
                        : prop.GetValue(_monitoringConfig)?.ToString() ?? "",
                    Tooltip = prop.GetCustomAttributes(false).OfType<TooltipAttribute>().FirstOrDefault()?.TooltipText ?? "",
                    DateType = prop.GetCustomAttributes(false).OfType<DataTypeAttribute>().FirstOrDefault()?.DataType.ToString() ?? DataTypeAttribute.ConfigDataTypeEnum.String.ToString(),
                    Description = prop.GetCustomAttributes(false).OfType<DescriptionAttribute>().FirstOrDefault()?.Description ?? "",
                    Category = prop.GetCustomAttributes(false).OfType<MonitoringCategoryAttribute>().FirstOrDefault()?.Category.GetDescription() ?? MonitoringCategoryAttribute.ConfigCategoryEnum.Common.GetDescription()
                })
                .ToList(),

            LogLevelSettings = _settingsProcessor
                .Get(SettingType.LogLevel)
                .Select(s => new LogLevelSettingsViewModel { Category = s.Key, LogLevel = s.Value })
                .ToList()
        };
    }
    

    public void Set(MonitoringSettingsViewModel settings)
    {
        var openTelemetrySettings = settings.OpenTelemetrySettings
            .ToDictionary(x => x.SettingId, x => x.Value);
        _settingsProcessor.Set(SettingType.OpenTelemetry, openTelemetrySettings);

        var logLevelSettings = settings.LogLevelSettings
            .ToDictionary(x => x.Category, x => x.LogLevel);
        _settingsProcessor.Set(SettingType.LogLevel, logLevelSettings);
        _monitoringConfig.Reset();
    }

    public void Reboot()
    {
        _instanceSync.SetRestartNeeded();
    }

}

public interface IMonitoringSettingsService
{
    MonitoringSettingsViewModel Get();
    void Set(MonitoringSettingsViewModel settingsViewModels);
    void Reboot();
}