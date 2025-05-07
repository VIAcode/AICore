namespace AiCoreApi.Models.ViewModels;

public class MonitoringSettingsViewModel
{
    public required List<SettingsViewModel> OpenTelemetrySettings { get; set; }
    public required List<LogLevelSettingsViewModel> LogLevelSettings { get; set; }
    public required List<SettingsViewModel> LoggingSettings { get; set; }
}
