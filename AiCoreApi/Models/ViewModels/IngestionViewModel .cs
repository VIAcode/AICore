namespace AiCoreApi.Models.ViewModels
{
    public class IngestionViewModel
    {
        public int IngestionId { get; set; } = 0;
        public string Name { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public IngestionType Type { get; set; } = IngestionType.SharePoint;
        public Dictionary<string, string> Content { get; set; } = new();
        public List<TagViewModel> Tags { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime Updated { get; set; } = DateTime.UtcNow;
        public DateTime? LastSync { get; set; }
        public bool IsLastSyncFailed { get; set; } = false;
        public string? LastSyncFailedMessage { get; set; }
        public IngestionStatus Status { get; set; } = IngestionStatus.Ready;
        public int? WorkspaceId { get; set; }
    }

    public enum IngestionType
    {
        SharePoint = 1
    }

    public enum IngestionStatus
    {
        Ready = 1,
        Syncing = 2,
        PendingSync = 3,
        Removing = 4,
        PendingRemove = 5,
    }
}
