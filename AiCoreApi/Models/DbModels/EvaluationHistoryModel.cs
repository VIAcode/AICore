using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCoreApi.Models.DbModels
{
    [Table("evaluation_history")]
    public class EvaluationHistoryModel
    {
        [Key] public int EvaluationHistoryId { get; set; }
        public int EvaluationId { get; set; }
        public string AgentName { get; set; } = string.Empty;
        [Column(TypeName = "jsonb")]
        public List<EvaluationHistoryQuestionModel> Questions { get; set; } = new();
        public string EvaluationLlmModel { get; set; } = string.Empty;
        public string EvaluationPrompt { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
        public int WorkspaceId { get; set; }
    }

    public class EvaluationHistoryQuestionModel
    {
        public List<string> Parameters { get; set; } = new();
        public string ExpectedResult { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string DebugMessages { get; set; } = string.Empty;
        public int Score { get; set; }
    }
}