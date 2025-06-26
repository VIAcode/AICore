using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Migrations;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[RoleAuthorize(Role.Admin, Role.Developer)]
[Route("api/v1/logs")]
public class DebugLogController : ControllerBase
{
    private readonly IDebugLogService _debugLogService;
    public DebugLogController(IDebugLogService debugLogService)
    {
        _debugLogService = debugLogService;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> List([FromBody] DebugLogFilterViewModel debugLogFilterViewModel, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var result = await _debugLogService.List(debugLogFilterViewModel, workspaceId);
        return Ok(result);
    }

    [HttpPost("pagesCount")]
    [Authorize]
    public async Task<IActionResult> PagesCount([FromBody] DebugLogFilterViewModel debugLogFilterViewModel, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var result = await _debugLogService.PagesCount(debugLogFilterViewModel, workspaceId);
        return Ok(result);
    }

    [HttpGet("debugMessages/{debugLogId}")]
    [Authorize]
    public async Task<IActionResult> GetDebugMessages(int debugLogId)
    {
        var result = await _debugLogService.GetDebugMessages(debugLogId);
        return Ok(result);
    }


}