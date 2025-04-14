using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using AgentType = AiCoreApi.Models.ViewModels.AgentType;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/agents")]
public class AgentsController : ControllerBase
{
    private readonly IAgentsService _agentsService;

    public AgentsController(
        IAgentsService agentsService)
    {
        _agentsService = agentsService;
    }

    [HttpGet]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> List([FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var result = await _agentsService.ListAgents(workspaceId);
        return Ok(result);
    }

    [HttpGet]
    [Route("{agentId}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Get(int agentId)
    {
        var agentsList = await _agentsService.ListAgents(null);
        var agent = agentsList.FirstOrDefault(x => x.AgentId == agentId);
        return Ok(agent);
    }

    [HttpGet]
    [Route("{agentId}/parameters")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> GetParameters(int agentId)
    {
        var agentParameters = await _agentsService.GetParameters(agentId);
        return Ok(agentParameters);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Add([FromBody] AgentViewModel agentViewModel, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var newAgent = await _agentsService.AddAgent(agentViewModel, workspaceId);
        return Ok(newAgent.AgentId);
    }

    [HttpPut("{agentId}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Update([FromBody] AgentViewModel agentViewModel)
    {
        await _agentsService.UpdateAgent(agentViewModel);
        return Ok(true);
    }

    [HttpDelete("{agentId}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Delete(int agentId)
    {
        await _agentsService.DeleteAgent(agentId);
        return Ok(true);
    }

    [HttpPut("{agentId}/enable")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Enable(int agentId)
    {
        await _agentsService.SwitchEnableAgent(agentId, true);
        return Ok(true);
    }

    [HttpPut("{agentId}/disable")]
    [RoleAuthorize(Role.Admin, Role.Developer)]

    public async Task<IActionResult> Disable(int agentId)
    {
        await _agentsService.SwitchEnableAgent(agentId, false);
        return Ok(true);
    }

    [HttpGet("{agentName}/isEnabled")]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    [SwaggerOperation(Summary = "Check if specific Agent is enabled or not")]
    [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(bool))]
    public async Task<IActionResult> IsEnabled(
        [FromRoute]
        [SwaggerParameter("Agent Name of the specific Agent, which can be found in Agents tab in UI in Admin mode.", Required = true)]
        string agentName)
    {
        var result = await _agentsService.IsAgentEnabled(agentName);
        return Ok(result);
    }

    [HttpGet("export")]
    [AllowAnonymous]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Export([FromQuery] string agentIds, [FromQuery] string token)
    {
        var agentIdsList = agentIds.Split(',').Select(int.Parse).ToList();
        var result = await _agentsService.ExportAgents(agentIdsList);
        return File(result, "application/zip", $"agents-{DateTime.UtcNow:yyyy-MM-dd-hh-mm}.zip");
    }

    [HttpPost("import")]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> Import(IFormFile file, [FromForm] string agentsVersions, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var agentsVersionsDict = agentsVersions.JsonGet<Dictionary<AgentType, int>>();
        var result = await _agentsService.ImportAgents(file, agentsVersionsDict, workspaceId);
        return Ok(result);
    }

    [HttpPut("import/{confirmationId}")]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> ImportConfirm(string confirmationId, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        await _agentsService.ConfirmImportAgents(confirmationId, workspaceId);
        return Ok();
    }


    [HttpGet]
    [Route("{agentId}/history")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> GetHistory(int agentId)
    {
        var agentHistory = await _agentsService.GetHistory(agentId);
        return Ok(agentHistory);
    }


    [HttpGet]
    [Route("{agentId}/history/{title}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> GetHistory(int agentId, string title)
    {
        var agentCode = await _agentsService.GetHistoryCode(agentId, title);
        return Ok(agentCode);
    }

}