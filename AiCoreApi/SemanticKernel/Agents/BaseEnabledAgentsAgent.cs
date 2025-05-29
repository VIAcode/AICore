using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.ViewModels;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseEnabledAgentsAgent : BaseAgent
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly RequestAccessor _requestAccessor;


        public const string EnabledAgents = "enabledAgents";
        public const string AgentsList = "agentsList"; 

        public BaseEnabledAgentsAgent(
            IAgentsProcessor agentsProcessor,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            MonitoringConfig monitoringConfig,
            ILogger<BaseEnabledAgentsAgent> logger)
            : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _agentsProcessor = agentsProcessor;
            _requestAccessor = requestAccessor;
        }

        private string GetParameterName(AgentModel agentModel)
        {
            if (agentModel.Content.ContainsKey(EnabledAgents))
                return EnabledAgents;
            if (agentModel.Content.ContainsKey(AgentsList))
                return AgentsList;
            return string.Empty;
        }

        public override async Task OnExport(AgentModel agentModel, Dictionary<int, AgentModelProcessed> agentsToExport)
        {
            var parameterName = GetParameterName(agentModel);
            if (string.IsNullOrEmpty(parameterName))
            {
                agentsToExport[agentModel.AgentId].Processed = true;
                return;
            }
            var enabledAgentIds = agentModel.Content[parameterName].Value.JsonGet<Dictionary<int, bool>>()
                .Where(x => x.Value)
                .Select(x => x.Key)
                .ToList();
            var allAgents = await _agentsProcessor.List(agentModel.WorkspaceId);
            var enabledAgents = allAgents
                .Where(agent => enabledAgentIds.Contains(agent.AgentId))
                .ToList();
            agentModel.Content[parameterName].Value = enabledAgents.Select(x => x.Name).ToList().ToJson()!;
            foreach (var enabledAgent in enabledAgents)
            {
                if (!agentsToExport.ContainsKey(enabledAgent.AgentId))
                {
                    agentsToExport.Add(enabledAgent.AgentId, new AgentModelProcessed
                    {
                        AgentModel = enabledAgent,
                        Processed = false
                    });
                }
            }
            agentsToExport[agentModel.AgentId].Processed = true;
        }

        public override async Task OnImport(AgentModel agentModel, Dictionary<string, AgentModelProcessed> agentsToImport)
        {
            var parameterName = GetParameterName(agentModel);
            if (string.IsNullOrEmpty(parameterName))
            {
                agentsToImport[agentModel.Name].Processed = true;
                return;
            }
            var allAgents = await _agentsProcessor.List(_requestAccessor.WorkspaceId);
            var allAgentNames = allAgents.Select(x => x.Name).ToList();
            var enabledAgentNames = agentModel.Content[parameterName].Value.JsonGet<List<string>>();
            if (enabledAgentNames.Any(x => !allAgentNames.Contains(x)))
                return;

            var enabledAgentIds = allAgents.Where(x => enabledAgentNames.Contains(x.Name))
                .Select(x => x.AgentId)
                .ToList();
            agentModel.Content[parameterName].Value = enabledAgentIds.ToDictionary(x => x, _ => true).ToJson()!;
            agentsToImport[agentModel.Name].Processed = true;
        }
    }
}
