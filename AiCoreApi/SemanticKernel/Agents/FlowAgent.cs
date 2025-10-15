using System.Text.Json;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class FlowAgent : BaseEnabledAgentsAgent, IFlowAgent
    {
        private string _debugMessageSenderName = "FlowAgent";
        private readonly ResponseAccessor _responseAccessor;
        private readonly IAgentExecutor _agentExecutor;
        private readonly IAgentsProcessor _agentsProcessor;

        private static class AgentContentParameters
        {
            public const string AgentToCall = "agentToCall";
        }

        public FlowAgent(
            IBaseAgentHelper baseAgentHelper,
            IAgentsProcessor agentsProcessor,
            IAgentExecutor agentExecutor,
            ResponseAccessor responseAccessor,
            ILogger<FlowAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _responseAccessor = responseAccessor;
            _agentExecutor = agentExecutor;
            _agentsProcessor = agentsProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", JsonSerializer.Serialize(parameters, new JsonSerializerOptions { WriteIndented = true }));
            var agentToCall = await GetParameterValueAsync(AgentContentParameters.AgentToCall);
            var result = await ExecuteAgent(agentToCall, parameters.Values.ToList());
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        public override async Task OnAddUpdate(AgentModel agentModel)
        {
            var enabledAgents = agentModel.Content[EnabledAgents].Value.JsonGet<Dictionary<int, bool>>()
                .Where(x => x.Value)
                .Select(x => x.Key)
                .ToList();

            if (agentModel.AgentId > 0) // update
            {
                var agentBeforeChanges = await _agentsProcessor.GetById(agentModel.AgentId);
                if (agentBeforeChanges == null)
                    throw new ExceptionHandlingMiddleware.AiCoreUiException($"Agent with id {agentModel.AgentId} not found");
                await _agentsProcessor.UpdateFlowNameForAsync(agentBeforeChanges.Name, null, agentModel.WorkspaceId);
            }
            await _agentsProcessor.UpdateFlowNameForAsync(enabledAgents, agentModel.Name, agentModel.WorkspaceId);
        }

        public override async Task OnDelete(AgentModel agentModel)
        {
            await _agentsProcessor.UpdateFlowNameForAsync(agentModel.Name, null, agentModel.WorkspaceId);
        }

        private async Task<string> ExecuteAgent(string agentName, List<string>? parameters = null)
        {
            try
            {
                return await _agentExecutor.ExecuteAsync(agentName, parameters);
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "ExecuteAgent Error", $"Agent: {agentName}\r\n\r\n Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}");
                throw;
            }
        }
    }

    public interface IFlowAgent
    {
        Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
