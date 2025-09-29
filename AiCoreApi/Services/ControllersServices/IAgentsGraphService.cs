using AiCoreApi.Models.ViewModels;

namespace AiCoreApi.Services.ControllersServices;

public interface IAgentsGraphService
{
    Task<DependencyGraphViewModel> GetDependencyGraph(int workspaceId);
}
