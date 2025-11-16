using Newtonsoft.Json;

namespace AiCoreApi.Models.ViewModels
{
    public class McpActionViewModel
    {
        public string Title { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<McpActionParameterViewModel>? Parameters { get; set; }
        public string OutputSchema { get; set; } = string.Empty;

    }

    public class McpActionParameterViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? Enum { get; set; }
        public bool CanBeNull { get; set; } = false;
    }
}
