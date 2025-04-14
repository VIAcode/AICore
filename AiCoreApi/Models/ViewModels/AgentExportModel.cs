using Newtonsoft.Json;

namespace AiCoreApi.Models.ViewModels
{
    public class AgentExportModel
    {
        public bool IsEnabled { get; set; } = true;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string LlmType { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, ConfigurableExportSetting> Content { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public int Version { get; set; } = 0;
    }

    public class ConfigurableExportSetting
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Extension { get; set; }
    }
}
