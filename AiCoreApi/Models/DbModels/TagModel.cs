using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels;

[Table("tags")]
public class TagModel
{
    [JsonIgnore]
    [Key]
    public int TagId { get; set; } = 0;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public List<GroupModel> Groups { get; set; } = new();
    public List<LoginModel> Logins { get; set; } = new();
    public List<IngestionModel> Ingestions { get; set; } = new();
    public List<RbacRoleSyncModel> RbacRoleSyncs { get; set; } = new();
    public List<AgentModel> Agents { get; set; } = new();
    public List<WorkspaceModel> Workspaces { get; set; } = new();
}