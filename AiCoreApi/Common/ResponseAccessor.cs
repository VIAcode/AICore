using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel;

namespace AiCoreApi.Common
{
    public class ResponseAccessor
    {
        private readonly ExtendedConfig _extendedConfig;
        private readonly ILogger<ResponseAccessor> _logger;
        private readonly RequestAccessor _requestAccessor;
        private readonly ICacheAccessor _cacheAccessor;
        private const string DebugCachePrefix = "Debug_";
        private const string ReasoningCachePrefix = "Reasoning_";
        private const int DebugCacheTimeout = 600;
        private const int ReasoningCacheTimeout = 600;
        public ResponseAccessor(
            ExtendedConfig extendedConfig,
            ILogger<ResponseAccessor> logger,
            RequestAccessor requestAccessor,
            ICacheAccessor cacheAccessor)
        {
            _extendedConfig = extendedConfig;
            _logger = logger;
            _requestAccessor = requestAccessor;
            _cacheAccessor = cacheAccessor;
        }

        public Dictionary<string, string> Context { get; set; } = new();

        public int AddNotification(string userName, string type, string title, string message, bool inProgress = false) => 
            _requestAccessor.AgentsHelper?.AddNotification(userName, type, title, message, inProgress, _requestAccessor.WorkspaceId ?? 0) ?? 0;

        public int UpdateNotification(int notificationId, string userName, string type, string title, string message, bool inProgress = false) =>
            _requestAccessor.AgentsHelper?.UpdateNotification(notificationId, userName, type, title, message, inProgress, _requestAccessor.WorkspaceId ?? 0) ?? 0;

        public string? StepState { get; set; }
        public MessageDialogViewModel.Message CurrentMessage { get; set; } = new() { Sender = PlannerHelpers.AssistantName };
        public void AddDebugMessage(string sender, string title, string details)
        {
            if (_requestAccessor.UseDebug || _extendedConfig.UseDebugLogForEachCall)
            {
                CurrentMessage.DebugMessages ??= new List<MessageDialogViewModel.DebugMessage>();
                CurrentMessage.DebugMessages.Add(new MessageDialogViewModel.DebugMessage
                {
                    Sender = sender,
                    Title = title,
                    Details = details,
                    DateTime = DateTime.UtcNow,
                    Level = Level,
                });
                var chatItemId = _requestAccessor?.MessageDialog?.Messages?.Last().ChatItemId;
                if (!string.IsNullOrEmpty(chatItemId))
                {
                    _cacheAccessor.SetCacheValue($"{DebugCachePrefix}{chatItemId}", CurrentMessage.DebugMessages.ToJson()!, DebugCacheTimeout);
                }
            }
            _logger.LogDebug("{4}, {0}: {1}, {2}", sender, title, details, _requestAccessor.Login);
        }

        public void AddReasoningMessage(string message)
        {
            if (_extendedConfig.UseReasoningMessages)
            {
                var chatItemId = _requestAccessor?.MessageDialog?.Messages?.Last().ChatItemId;
                if (!string.IsNullOrEmpty(chatItemId))
                {
                    var existingMessages = GetReasoningMessages(chatItemId);
                    existingMessages.Add(message);
                    _cacheAccessor.SetCacheValue($"{ReasoningCachePrefix}{chatItemId}", existingMessages.ToJson()!, ReasoningCacheTimeout);
                }
            }
            _logger.LogDebug("Reasoning: {0}, {1}", message, _requestAccessor.Login);
        }

        public int Level { get; set; } = 0;

        public List<MessageDialogViewModel.DebugMessage> GetDebugMessages(string chatMessageId)
        {
            if (string.IsNullOrEmpty(chatMessageId))
                return new List<MessageDialogViewModel.DebugMessage>();
            var resultString = _cacheAccessor.GetCacheValue($"{DebugCachePrefix}{chatMessageId}");
            if (string.IsNullOrEmpty(resultString))
                return new List<MessageDialogViewModel.DebugMessage>();
            var result = resultString.JsonGet<List<MessageDialogViewModel.DebugMessage>>();
            if (result == null)
            {
                _logger.LogWarning("GetDebugMessages: result is null for chatMessageId {ChatMessageId}", chatMessageId);
                return new List<MessageDialogViewModel.DebugMessage>();
            }
            return result;
        }

        public List<string> GetReasoningMessages(string chatMessageId)
        {
            if (string.IsNullOrEmpty(chatMessageId))
                return new List<string>();
            var resultString = _cacheAccessor.GetCacheValue($"{ReasoningCachePrefix}{chatMessageId}");
            if (string.IsNullOrEmpty(resultString))
                return new List<string>();
            var result = resultString.JsonGet<List<string>>();
            if (result == null)
            {
                _logger.LogWarning("GetReasoningMessages: result is null for chatMessageId {ChatMessageId}", chatMessageId);
                return new List<string>();
            }
            return result;
        }

        public void AddSpentTokens(string modelName, int requestTokens, int responseTokens)
        {
            CurrentMessage.SpentTokens ??= new Dictionary<string, MessageDialogViewModel.TokensSpent>();
            if (!CurrentMessage.SpentTokens.ContainsKey(modelName))
                CurrentMessage.SpentTokens[modelName] = new MessageDialogViewModel.TokensSpent();
            CurrentMessage.SpentTokens[modelName].Request += requestTokens;
            CurrentMessage.SpentTokens[modelName].Response += responseTokens;
        }
    }
}
