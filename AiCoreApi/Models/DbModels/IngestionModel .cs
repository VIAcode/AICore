using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels
{
    [Table("ingestion")]
    public class IngestionModel
    {
        [JsonIgnore]
        [Key] 
        public int IngestionId { get; set; } = 0;
        public string Name { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public IngestionType Type { get; set; } = IngestionType.SharePoint;
        [Column(TypeName = "jsonb")] 
        public Dictionary<string, string> Content { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime Updated { get; set; } = DateTime.UtcNow;
        public List<TagModel> Tags { get; set; } = new();
        public DateTime LastSync { get; set; } = DateTime.UtcNow;
        public int? WorkspaceId { get; set; }
    }

    public enum IngestionType
    {
        SharePoint = 1,
        WebUrl = 2,
        UploadFile = 3
    }
}