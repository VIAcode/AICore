using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class HistoryAgent : BaseAgent, IHistoryAgent
    {
        private string _debugMessageSenderName = "HistoryAgent";

        private static class AgentContentParameters
        {
            public const string Count = "count";
        }
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public HistoryAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            MonitoringConfig monitoringConfig,
            ILogger<HistoryAgent> logger) : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";
            var count = agent.Content[AgentContentParameters.Count].Value;
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Get Message History", count);
            var result = _requestAccessor.MessageDialog!.GetHistory(Convert.ToInt32(count));
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Get Message History Result", result);
            return result;
        }
    }

    public interface IHistoryAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
