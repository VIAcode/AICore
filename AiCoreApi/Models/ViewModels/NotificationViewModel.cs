namespace AiCoreApi.Models.ViewModels;

public class NotificationViewModel
{
    public int NotificationId { get; set; } = 0;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "info"; // info, warning, error, success
    public int WorkspaceId { get; set; }
    public string User { get; set; } = string.Empty;
    public bool InProgress { get; set; } = false;
}