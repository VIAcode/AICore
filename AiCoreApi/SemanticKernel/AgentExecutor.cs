using AiCoreApi.Authorization;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.SemanticKernel
{
    public interface IAgentExecutor
    {
        Task<string> ExecuteAsync(string agentName, List<string>? parameters = null, bool checkAgentCallType = false);
    }

    public class AgentExecutor : IAgentExecutor
    {
        private readonly RequestAccessor _requestAccessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IAgentRegistry _registry;
        private readonly ILogger<AgentExecutor> _logger;

        public AgentExecutor(
            RequestAccessor requestAccessor,
            ExtendedConfig extendedConfig,
            IAgentsProcessor agentsProcessor,
            IAgentRegistry registry,
            ILogger<AgentExecutor> logger)
        {
            _requestAccessor = requestAccessor;
            _extendedConfig = extendedConfig;
            _agentsProcessor = agentsProcessor;
            _registry = registry;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<string> ExecuteAsync(string agentName, List<string>? parameters = null, bool checkAgentCallType = false)
        {
            if (string.IsNullOrWhiteSpace(agentName))
                throw new ArgumentNullException(nameof(agentName), "Agent name cannot be null or empty.");

            _logger.LogDebug("Executing agent: {AgentName}, Workspace: {WorkspaceId}", agentName, _requestAccessor.WorkspaceId);

            var agent = await _agentsProcessor.GetByName(agentName, _requestAccessor.WorkspaceId);

            if (agent == null)
                throw new AiCoreUiException($"Agent not found: {agentName}");

            _requestAccessor.AgentId = agent.AgentId;

            if (checkAgentCallType)
                ValidateCallType(agent);

            var parametersDict = (parameters ?? new List<string>())
                .Select((v, i) => new KeyValuePair<string, string>($"parameter{i + 1}", v))
                .ToDictionary(k => k.Key, v => v.Value);

            _logger.LogTrace("Resolved {AgentName} (Type: {AgentType}), calling agent handler...", agent.Name, agent.Type);

            var agentInstance = _registry.Resolve(agent.Type);
            if (agentInstance == null)
                throw new AiCoreUiException($"Agent type not registered: {agent.Type}");

            try
            {
                var result = await agentInstance.DoCallWrapper(agent, parametersDict);
                _logger.LogDebug("Agent {AgentName} completed successfully.", agentName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing agent {AgentName}", agentName);
                throw;
            }
        }

        private void ValidateCallType(AgentModel agent)
        {
            var callType = agent.Content.ContainsKey(AgentTypeCalls.AgentCallTypeFieldName)
                ? agent.Content[AgentTypeCalls.AgentCallTypeFieldName].Value
                : AgentTypeCalls.PrivateCall;

            var allowPublic = callType.Contains(AgentTypeCalls.PublicCall) && _extendedConfig.UsePublicCalls;
            var allowPrivate = callType.Contains(AgentTypeCalls.PrivateCall);
            var allowWebHook = callType.Contains(AgentTypeCalls.WebHook) && _extendedConfig.UseWebHooks;

            if (allowWebHook && _requestAccessor.IsWebHookCall)
                return;
            if (allowPublic && _requestAccessor.IsPublicCall)
                return;
            if (allowPrivate && !_requestAccessor.IsPublicCall)
                return;

            throw new AiCoreAuthException(
                $"Agent {agent.Name} cannot be called according to its call type ({callType}).");
        }
    }
}
