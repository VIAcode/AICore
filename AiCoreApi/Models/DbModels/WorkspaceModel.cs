using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels;

[Table("workspaces")]
public class WorkspaceModel
{
    [JsonIgnore]
    [Key]
    public int WorkspaceId { get; set; } = 0;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public List<TagModel> Tags { get; set; } = new();
}