using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using AgentType = AiCoreApi.Models.DbModels.AgentType;

namespace AiCoreApi.SemanticKernel
{
    public class AgentLifecycleService : IAgentLifecycleService
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IAgentRegistry _registry;

        public AgentLifecycleService(
            IAgentsProcessor agentsProcessor,
            IAgentRegistry registry)
        {
            _agentsProcessor = agentsProcessor;
            _registry = registry;
        }

        public async Task OnAddUpdateAsync(AgentModel agentModel)
        {
            if (IsListenerAgentType(agentModel.Type))
                return;
            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnAddUpdate(agentModel);
        }

        public async Task OnDeleteAsync(int agentId)
        {
            var agentModel = await _agentsProcessor.GetById(agentId);
            if (agentModel == null)
                throw new AiCoreUiException($"Agent not found with ID: {agentId}");

            if (IsListenerAgentType(agentModel.Type))
                return;

            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnDelete(agentModel);
        }

        public async Task OnExportAsync(AgentModel agentModel, Dictionary<int, AgentModelProcessed> agentsToExport)
        {
            if (IsListenerAgentType(agentModel.Type))
            {
                agentsToExport[agentModel.AgentId].Processed = true;
                return;
            }

            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnExport(agentModel, agentsToExport);
        }

        public async Task OnImportAsync(AgentModel agentModel, Dictionary<string, AgentModelProcessed> agentsToImport)
        {
            if (IsListenerAgentType(agentModel.Type))
            {
                agentsToImport[agentModel.Name].Processed = true;
                return;
            }

            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnImport(agentModel, agentsToImport);
        }

        private static readonly HashSet<AgentType> ListenerAgentTypes = new()
        {
            AgentType.AzureServiceBusListener,
            AgentType.RabbitMqListener,
            AgentType.Imap,
            AgentType.GraphMail,
            AgentType.GraphTeamsListener,
            AgentType.Scheduler
        };

        public bool IsListenerAgentType(AgentType agentType) => ListenerAgentTypes.Contains(agentType);
    }

    public interface IAgentLifecycleService
    {
        Task OnAddUpdateAsync(AgentModel agentModel);
        Task OnDeleteAsync(int agentId);
        Task OnExportAsync(AgentModel agentModel, Dictionary<int, AgentModelProcessed> agentsToExport);
        Task OnImportAsync(AgentModel agentModel, Dictionary<string, AgentModelProcessed> agentsToImport);
    }
}
