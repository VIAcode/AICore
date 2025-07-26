using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels
{
    [Table("task")]
    public class TaskModel 
    {
        [JsonIgnore]
        [Key]
        public int TaskId { get; set; } = 0;
        public TaskType Type { get; set; } = TaskType.DataSync;
        public TaskState State { get; set; } = TaskState.New;
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime Updated { get; set; } = DateTime.UtcNow;
        [Column(TypeName = "jsonb")]
        public Dictionary<string, object> Context { get; set; } = new();
        public string ErrorMessage { get; set; } = string.Empty;
        public bool IsRetriable { get; set; }
        public int? LockerTaskId { get; set; }

        // relations

        [Required]
        public int IngestionId { get; set; }

        [ForeignKey(nameof(IngestionId))]
        // Delete all tasks if Ingestion is deleted
        [DeleteBehavior(DeleteBehavior.Cascade)]
        public IngestionModel? Ingestion { get; set; } = null;
    }

    public enum TaskState
    {
        New = 1,
        InProgress = 2,
        Completed = 3,
        Failed = 4,
    }

    public enum TaskType
    {
        DataSync = 1,
        Remove = 2,
        TagSync = 3,
        Feedback = 4,
        Evaluate = 5,
    }
}
