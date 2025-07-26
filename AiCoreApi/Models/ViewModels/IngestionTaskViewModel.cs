using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Models.ViewModels;


public class IngestionTaskViewModel
{
    public int TaskId { get; set; } = 0;
    public int IngestionId { get; set; } = 0;
    public string IngestionName { get; set; } = string.Empty;
    public TaskType Type { get; set; } = TaskType.DataSync;
    public TaskState State { get; set; } = TaskState.New;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Context { get; set; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
    public int? LockerTaskId { get; set; }
}