using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/groups")]
public class GroupsController : ControllerBase
{
    private readonly IGroupsService _groupsService;
    public GroupsController(IGroupsService groupsService)
    {
        _groupsService = groupsService;
    }

    [Authorize]
    [HttpGet("{groupId}")]
    public async Task<IActionResult> GetGroup(int groupId)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        var group = await _groupsService.GetGroupById(groupId);
        return Ok(group);
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List()
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        var result = await _groupsService.ListGroups();
        return Ok(result);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] GroupViewModel groupViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        groupViewModel.CreatedBy = currentUser;
        if (groupViewModel.GroupId != 0) throw new ArgumentException("Group identifier must be zero");

        await _groupsService.AddOrUpdateGroup(groupViewModel);
        return Ok(true);
    }

    [HttpPut("{groupId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Update([FromBody] GroupViewModel groupViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        if (groupViewModel.GroupId == 0) throw new ArgumentException("Group identifier mustn't be zero");

        await _groupsService.AddOrUpdateGroup(groupViewModel);
        return Ok(true);
    }
}