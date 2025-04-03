using AiCoreApi.Authorization;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.DbModels;
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
    private readonly IConnectService _connectService;

    public AgentsController(
        IAgentsService agentsService,
        IConnectService connectService)
    {
        _agentsService = agentsService;
        _connectService = connectService;
    }

    [HttpGet]
    [CombinedAuthorize]
    [AdminAuthorize]
    public async Task<IActionResult> List()
    {
        var result = await _agentsService.ListAgents();
        return Ok(result);
    }

    [HttpGet]
    [Route("{agentId}")]
    [AdminAuthorize]
    public async Task<IActionResult> Get(int agentId)
    {
        var agentsList = await _agentsService.ListAgents();
        var agent = agentsList.FirstOrDefault(x => x.AgentId == agentId);
        return Ok(agent);
    }

    [HttpGet]
    [Route("{agentId}/parameters")]
    [AdminAuthorize]
    public async Task<IActionResult> GetParameters(int agentId)
    {
        var agentParameters = await _agentsService.GetParameters(agentId);
        return Ok(agentParameters);
    }

    [HttpPost]
    [AdminAuthorize]
    public async Task<IActionResult> Add([FromBody] AgentViewModel agentViewModel)
    {
        var newAgent = await _agentsService.AddAgent(agentViewModel);
        return Ok(newAgent.AgentId);
    }

    [HttpPut("{agentId}")]
    [AdminAuthorize]
    public async Task<IActionResult> Update([FromBody] AgentViewModel agentViewModel)
    {
        await _agentsService.UpdateAgent(agentViewModel);
        return Ok(true);
    }

    [HttpDelete("{agentId}")]
    [AdminAuthorize]
    public async Task<IActionResult> Delete(int agentId)
    {
        await _agentsService.DeleteAgent(agentId);
        return Ok(true);
    }

    [HttpPut("{agentId}/enable")]
    [AdminAuthorize]
    public async Task<IActionResult> Enable(int agentId)
    {
        await _agentsService.SwitchEnableAgent(agentId, true);
        return Ok(true);
    }

    [HttpPut("{agentId}/disable")]
    [AdminAuthorize]

    public async Task<IActionResult> Disable(int agentId)
    {
        await _agentsService.SwitchEnableAgent(agentId, false);
        return Ok(true);
    }

    [HttpGet("{agentName}/isEnabled")]
    [CombinedAuthorize]
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
    public async Task<IActionResult> Export([FromQuery] string agentIds, [FromQuery] string token)
    {
        var agentIdsList = agentIds.Split(',').Select(int.Parse).ToList();
        // Check if the token is valid and has the required permissions (Admin)
        // We use AllowAnonymous attribute just to allow the request to be made without a token in Header, but we still need to check the token
        var loginModel = await _connectService.CheckAccessToken(token);
        if (loginModel == null)
            return Unauthorized("Invalid Token");
        if (loginModel.Role != RoleEnum.Admin)
            return Unauthorized("No Permissions");

        var result = await _agentsService.ExportAgents(agentIdsList);
        return File(result, "application/zip", $"agents-{DateTime.UtcNow:yyyy-MM-dd-hh-mm}.zip");
    }

    [HttpPost("import")]
    [CombinedAuthorize]
    [AdminAuthorize]
    public async Task<IActionResult> Import(IFormFile file, [FromForm] string agentsVersions)
    {
        var agentsVersionsDict = agentsVersions.JsonGet<Dictionary<AgentType, int>>();
        var result = await _agentsService.ImportAgents(file, agentsVersionsDict);
        return Ok(result);
    }

    [HttpPut("import/{confirmationId}")]
    [CombinedAuthorize]
    [AdminAuthorize]
    public async Task<IActionResult> ImportConfirm(string confirmationId)
    {
        await _agentsService.ConfirmImportAgents(confirmationId);
        return Ok();
    }


    [HttpGet]
    [Route("{agentId}/history")]
    [AdminAuthorize]
    public async Task<IActionResult> GetHistory(int agentId)
    {
        var agentHistory = await _agentsService.GetHistory(agentId);
        return Ok(agentHistory);
    }


    [HttpGet]
    [Route("{agentId}/history/{title}")]
    [AdminAuthorize]
    public async Task<IActionResult> GetHistory(int agentId, string title)
    {
        var agentCode = await _agentsService.GetHistoryCode(agentId, title);
        return Ok(agentCode);
    }

}