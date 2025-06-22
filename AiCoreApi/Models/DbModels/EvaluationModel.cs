using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCoreApi.Models.DbModels
{
    [Table("evaluation")]
    public class EvaluationModel
    {
        [Key] public int EvaluationId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AgentName { get; set; } = string.Empty;
        [Column(TypeName = "jsonb")]
        public List<EvaluationQuestionModel> Questions { get; set; } = new();
        public DateTime? LastRun { get; set; }
        public int LastScore { get; set; } = 0;
        public string EvaluationLlmModel { get; set; } = string.Empty;
        public string EvaluationPrompt { get; set; } = string.Empty;
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
        public int WorkspaceId { get; set; }
        public string Progress { get; set; } = string.Empty;
    }

    public class EvaluationQuestionModel
    {
        public List<string> Parameters { get; set; } = new();
        public string ExpectedResult { get; set; } = string.Empty;
    }
}