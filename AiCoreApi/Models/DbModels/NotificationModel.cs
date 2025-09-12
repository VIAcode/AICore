using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCoreApi.Models.DbModels
{
    [Table("notifications")]
    public class NotificationModel
    {
        [Key] public int NotificationId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsRead { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Type { get; set; } = NotificationTypes.Info;
        public int WorkspaceId { get; set; }
        public string User { get; set; } = string.Empty;
        public bool InProgress { get; set; } = false;
    }

    public static class NotificationTypes
    {
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Error = "error";
        public const string Success = "success";
    }
}