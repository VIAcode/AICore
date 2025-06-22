namespace AiCoreApi.Models.ViewModels;

public class EvaluationViewModel
{
    public int EvaluationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public List<EvaluationQuestionViewModel> Questions { get; set; } = new();
    public DateTime? LastRun { get; set; }
    public int LastScore { get; set; } = 0;
    public string EvaluationLlmModel { get; set; } = string.Empty;
    public string EvaluationPrompt { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public int WorkspaceId { get; set; }
    public string Progress { get; set; } = string.Empty;
}

public class EvaluationQuestionViewModel
{
    public List<string> Parameters { get; set; } = new();
    public string ExpectedResult { get; set; } = string.Empty;
}
