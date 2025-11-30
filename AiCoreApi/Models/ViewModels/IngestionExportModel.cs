namespace AiCoreApi.Models.ViewModels
{
    public class IngestionExportModel
    {
        public string Name { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Content { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public DateTime? Created { get; set; } = null;
        public string? CreatedBy { get; set; } = string.Empty;
    }
}
