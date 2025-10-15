using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.SemanticKernel
{
    public class AgentLifecycleService : IAgentLifecycleService
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IAgentRegistry _registry;
        private readonly RequestAccessor _requestAccessor;

        public AgentLifecycleService(
            IAgentsProcessor agentsProcessor,
            IAgentRegistry registry,
            RequestAccessor requestAccessor)
        {
            _agentsProcessor = agentsProcessor;
            _registry = registry;
            _requestAccessor = requestAccessor;
        }

        public async Task OnAddUpdateAsync(AgentModel agentModel)
        {
            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnAddUpdate(agentModel);
        }

        public async Task OnDeleteAsync(int agentId)
        {
            var agents = await _agentsProcessor.List(_requestAccessor.WorkspaceId);
            var agentModel = agents.FirstOrDefault(x => x.AgentId == agentId);
            if (agentModel == null)
                throw new AiCoreUiException($"Agent not found with ID: {agentId}");
            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnDelete(agentModel);
        }

        public async Task OnExportAsync(AgentModel agentModel, Dictionary<int, AgentModelProcessed> agentsToExport)
        {
            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnExport(agentModel, agentsToExport);
        }

        public async Task OnImportAsync(AgentModel agentModel, Dictionary<string, AgentModelProcessed> agentsToImport)
        {
            var baseAgent = _registry.Resolve(agentModel.Type);
            await baseAgent.OnImport(agentModel, agentsToImport);
        }
    }

    public interface IAgentLifecycleService
    {
        Task OnAddUpdateAsync(AgentModel agentModel);
        Task OnDeleteAsync(int agentId);
        Task OnExportAsync(AgentModel agentModel, Dictionary<int, AgentModelProcessed> agentsToExport);
        Task OnImportAsync(AgentModel agentModel, Dictionary<string, AgentModelProcessed> agentsToImport);
    }
}
