using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;

namespace AiCoreApi.SemanticKernel
{
    public class PlannerHelpers : IPlannerHelpers
    {
        public const string AssistantName = "assistant";

        public static class PlannerPromptPlaceholders
        {
            public const string CurrentQuestionPlaceholder = "{{currentQuestion}}";
            public const string PluginsInstructionsPlaceholder = "{{pluginsInstructions}}";
            public const string HasFilesPlaceholder = "{{hasFiles}}";
            public const string FilesNamesPlaceholder = "{{filesNames}}";
            public const string FilesDataPlaceholder = "{{filesData}}";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly ILogger<PlannerHelpers> _logger;

        public PlannerHelpers(
            RequestAccessor requestAccessor,
            IAgentsProcessor agentsProcessor,
            ILogger<PlannerHelpers> logger)
        {
            _requestAccessor = requestAccessor;
            _agentsProcessor = agentsProcessor;
            _logger = logger;
        }

        public string ApplyPlaceholders(string plannerPrompt)
        {
            try
            {
                var lastMessage = _requestAccessor.MessageDialog?.Messages?.LastOrDefault();
                if (lastMessage == null)
                {
                    _logger.LogWarning("PlannerHelpers.ApplyPlaceholders: No last message found in RequestAccessor.MessageDialog");
                    return plannerPrompt;
                }

                var replaced = plannerPrompt
                    .Replace(PlannerPromptPlaceholders.CurrentQuestionPlaceholder,
                        _requestAccessor.MessageDialog!.GetQuestion())
                    .Replace(PlannerPromptPlaceholders.HasFilesPlaceholder,
                        lastMessage.HasFiles().ToString())
                    .Replace(PlannerPromptPlaceholders.FilesNamesPlaceholder,
                        lastMessage.GetFileNames())
                    .Replace(PlannerPromptPlaceholders.FilesDataPlaceholder,
                        lastMessage.GetFileContents());

                return replaced;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PlannerHelpers.ApplyPlaceholders failed.");
                return plannerPrompt;
            }
        }

        public string GetPlannerCacheKey(string plannerPrompt, Kernel kernel)
        {
            var pluginNames = string.Join(",", kernel.Plugins.Select(p => p.Name));
            var key = $"planner_{plannerPrompt.GetHash()}_{pluginNames.GetHash()}";
            _logger.LogTrace("Planner cache key generated: {Key}", key);
            return key;
        }

        private List<AgentModel>? _agentsList;
        public async Task<List<AgentModel>> GetAgentsList() => _agentsList ??= await _agentsProcessor.List(_requestAccessor.WorkspaceId);

    }

    public interface IPlannerHelpers
    {
        string ApplyPlaceholders(string plannerPrompt);
        string GetPlannerCacheKey(string plannerPrompt, Kernel kernel);
        Task<List<AgentModel>> GetAgentsList();
    }
}
