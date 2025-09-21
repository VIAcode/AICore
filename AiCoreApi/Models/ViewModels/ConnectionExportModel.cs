namespace AiCoreApi.Models.ViewModels
{
    public class ConnectionExportModel
    {
        public string Name { get; set; } = string.Empty;
        public ConnectionType Type { get; set; } = ConnectionType.SharePoint;
        public Dictionary<string, string> Content { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
    }
}
