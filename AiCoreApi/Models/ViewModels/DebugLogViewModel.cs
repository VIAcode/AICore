namespace AiCoreApi.Models.ViewModels
{
    public class DebugLogViewModel
    {
        public int DebugLogId { get; set; }
        public DateTime Date { get; set; } = DateTime.UtcNow;
        public string Login { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public List<DebugMessageViewModel>? DebugMessages { get; set; } = new();
        public Dictionary<string, TokensSpentViewModel>? SpentTokens { get; set; }
        public List<string>? Files { get; set; }
        public int? WorkspaceId { get; set; }
    }

    public class DebugMessageViewModel
    {
        public string Sender { get; set; } = string.Empty;
        public DateTime DateTime { get; set; } = DateTime.Now;
        public string Title { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class TokensSpentViewModel
    {
        public int Request { get; set; } = 0;
        public int Response { get; set; } = 0;
    }

    public class DebugLogFilterViewModel
    {
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 0;
        public DateTime? DateFrom { get; set; }
        public string? Login { get; set; } = string.Empty;
        public string? Prompt { get; set; } = string.Empty;
        public string? Result { get; set; } = string.Empty;
    }
}
