using AiCoreApi.Authorization;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AiCoreApi.Authorization.Attributes;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/connections")]
public class ConnectionsController : ControllerBase
{
    private readonly IConnectionService _connectionService;
    public ConnectionsController(IConnectionService connectionService)           
    {
        _connectionService = connectionService;
    }

    [HttpGet]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    [Authorize]
    public async Task<IActionResult> ConnectionsList([FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        var connections = await _connectionService.ListConnections(workspaceId);

        // Non-admin users should not see connection content as we have credentials in it
        if (Request.HttpContext.User.FindFirstValue(ClaimTypes.Role) == nameof(Models.DbModels.RoleEnum.User))
            connections.ForEach(c => c.Content = new Dictionary<string, string>());
        return Ok(connections);
    }

    [HttpPost]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> ConnectionsAdd([FromBody] ConnectionViewModel connectionViewModel, [FromQuery(Name = "workspace_id")] int workspaceId = 0)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        await _connectionService.AddConnection(connectionViewModel, currentUser, workspaceId);
        return Ok(true);
    }

    [HttpPut("{connectionId}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> ConnectionsUpdate([FromBody] ConnectionViewModel connectionViewModel)
    {
        await _connectionService.UpdateConnection(connectionViewModel);
        return Ok(true);
    }

    [HttpDelete("{connectionId}")]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> ConnectionsDelete(int connectionId)
    {
        await _connectionService.DeleteConnection(connectionId);
        return Ok(true);
    }

    [HttpGet("{connectionId}")]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> GetConnection(int connectionId)
    {
        var connection = await _connectionService.GetConnectionById(connectionId);
        return Ok(connection);
    }
}
