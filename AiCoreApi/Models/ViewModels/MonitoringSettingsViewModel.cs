namespace AiCoreApi.Models.ViewModels;

public class MonitoringSettingsViewModel
{
    public List<SettingsViewModel> OpenTelemetrySettings { get; set; } = new();
    public List<LogLevelSettingsViewModel> LogLevelSettings { get; set; } = new();
    public List<SettingsViewModel> LoggingSettings { get; set; } = new();
}
