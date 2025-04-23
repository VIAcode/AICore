using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels
{
    [Table("debug_log")]
    public class DebugLogModel
    {
        [JsonIgnore]
        [Key]
        public int DebugLogId { get; set; }
        public DateTime Date { get; set; } = DateTime.UtcNow;
        public string Login { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        [Column(TypeName = "jsonb")]
        public List<DebugMessage>? DebugMessages { get; set; } = new();
        [Column(TypeName = "jsonb")]
        public Dictionary<string, TokensSpent>? SpentTokens { get; set; }
        [Column(TypeName = "jsonb")]
        public List<string>? Files { get; set; }
        public int? WorkspaceId { get; set; }
    }

    public class DebugMessage
    {
        public string Sender { get; set; } = string.Empty;
        public DateTime DateTime { get; set; } = DateTime.Now;
        public string Title { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class TokensSpent
    {
        public int Request { get; set; } = 0;
        public int Response { get; set; } = 0;
    }

    public class DebugLogFilterModel
    {
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 0;
        public DateTime? DateFrom { get; set; }
        public string? Login { get; set; }
        public string? Prompt { get; set; }
        public string? Result { get; set; }
    }
}
