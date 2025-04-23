using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/workspaces")]
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspacesService _workspacesService;
    private readonly RequestAccessor _requestAccessor;

    public WorkspacesController(
        IWorkspacesService workspacesService,
        RequestAccessor requestAccessor)
    {
        _workspacesService = workspacesService;
        _requestAccessor = requestAccessor;
    }

    [Authorize]
    [HttpGet("{workspaceId}")]
    public async Task<IActionResult> GetWorkspace(int workspaceId)
    {
        return Ok(await _workspacesService.GetWorkspace(workspaceId));
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List()
    {
        return Ok(await _workspacesService.ListWorkspaces());
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] WorkspaceViewModel workspaceViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        workspaceViewModel.CreatedBy = currentUser;
        if (workspaceViewModel.WorkspaceId != 0) throw new ArgumentException("Id must be zero");

        await _workspacesService.AddOrUpdateWorkspace(workspaceViewModel);
        return Ok(true);
    }

    [HttpPut("{workspaceId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Update([FromBody] WorkspaceViewModel workspaceViewModel)
    {
        if (workspaceViewModel.WorkspaceId == 0) throw new ArgumentException("Id mustn't be zero");

        await _workspacesService.AddOrUpdateWorkspace(workspaceViewModel);
        return Ok(true);
    }

    [HttpDelete("{workspaceId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Remove(int workspaceId)
    {
        var result = await _workspacesService.RemoveWorkspace(workspaceId);
        return Ok(result);
    }

    [HttpGet("my")]
    [CombinedAuthorize]
    [Produces("application/json")]
    [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(List<WorkspaceViewModel>))]
    [SwaggerOperation(Summary = "List Workspaces available for specific user.")]
    public async Task<IActionResult> ListMyWorkspaces()
    {
        var workspaces = await _workspacesService.ListUserWorkspaces(_requestAccessor.Login, _requestAccessor.LoginType);
        return Ok(workspaces);
    }
}