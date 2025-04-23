using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/ingestions")]
public class IngestionController : ControllerBase
{
    private readonly IIngestionService _ingestionService;
    public IngestionController(IIngestionService ingestionService)           
    {
        _ingestionService = ingestionService;
    }

    [HttpGet("{ingestionId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> GetIngestion(int ingestionId)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        var ingestion = await _ingestionService.GetIngestionById(ingestionId);
        return Ok(ingestion);
    }

    [HttpGet]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> List([FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var ingestions = await _ingestionService.ListIngestions(workspaceId);
        return Ok(ingestions);
    }

    [HttpGet("tasks")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> TaskList([FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var ingestionTasks = await _ingestionService.ListIngestionTasks(workspaceId);
        return Ok(ingestionTasks);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] IngestionViewModel ingestionViewModel, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        await _ingestionService.AddIngestion(ingestionViewModel, currentUser, workspaceId);
        return Ok(true);
    }

    [HttpPut("{ingestionId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Update([FromBody] IngestionViewModel ingestionViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        await _ingestionService.UpdateIngestion(ingestionViewModel, currentUser);
        return Ok(true);
    }

    [HttpPost("{ingestionId}/sync")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Sync(int ingestionId)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();
        await _ingestionService.SyncIngestion(ingestionId, currentUser);
        return Ok(true);
    }

    [HttpDelete("{ingestionId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Delete(int ingestionId)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();
        await _ingestionService.DeleteIngestion(ingestionId, currentUser);
        return Ok(true);
    }

    [HttpPost("autocomplete/{parameterName}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> GetAutoComplete(string parameterName, [FromBody] IngestionViewModel ingestionViewModel)
    {
        var result = await _ingestionService.GetAutoComplete(parameterName, ingestionViewModel);
        return Ok(result);
    }
}
