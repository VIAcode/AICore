using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Services.ControllersServices;
using AiCoreApi.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/agents-graph")]
    public class AgentsGraphController : ControllerBase
    {
        private readonly IAgentsGraphService _agentsGraphService;

        public AgentsGraphController(IAgentsGraphService agentsGraphService)
        {
            _agentsGraphService = agentsGraphService;
        }

        [HttpGet]
        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [SwaggerOperation(Summary = "Get agents dependency graph", Description = "Returns a graph structure showing dependencies between agents and flows")]
        [SwaggerResponse(200, "Successfully retrieved dependency graph", typeof(DependencyGraphViewModel))]
        [SwaggerResponse(401, "Unauthorized")]
        [SwaggerResponse(403, "Forbidden")]
        public async Task<ActionResult<DependencyGraphViewModel>> GetDependencyGraph([FromQuery(Name = "workspace_id")] int workspaceId = 0)
        {
            var dependencyGraph = await _agentsGraphService.GetDependencyGraph(workspaceId);
            return Ok(dependencyGraph);
        }

    }
}
