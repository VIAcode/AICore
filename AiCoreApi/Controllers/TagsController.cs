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
[Route("api/v1/tags")]
public class TagsController : ControllerBase
{
    private readonly ITagsService _tagsService;
    private readonly RequestAccessor _requestAccessor;
    public TagsController(
        ITagsService tagsService, 
        RequestAccessor requestAccessor)
    {
        _tagsService = tagsService;
        _requestAccessor = requestAccessor;
    }

    [Authorize]
    [HttpGet("{tagId}")]
    public IActionResult GetTag(int tagId)
    {
        return Ok(_tagsService.GetTag(tagId));
    }

    [HttpGet]
    [Authorize]
    public IActionResult List()
    {
        return Ok(_tagsService.ListTags());
    }

    [HttpGet("my")]
    [CombinedAuthorize]
    [Produces("application/json")]
    [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(List<TagViewModel>))]
    [SwaggerOperation(Summary = "List tags available for specific user. If TAGGING feature is enabled, user can not do chat/search without tag.")]
    public async Task<IActionResult> ListMyTags()
    {
        var tags = await _tagsService.ListUserTags(_requestAccessor.Login, _requestAccessor.LoginType);
        return Ok(tags);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] TagViewModel tagViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();

        tagViewModel.CreatedBy = currentUser;
        if (tagViewModel.TagId != 0) throw new ArgumentException("Tag identifier must be zero");

        await _tagsService.AddOrUpdateTag(tagViewModel);
        return Ok(true);
    }

    [HttpPut("{tagId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Update([FromBody] TagViewModel tagViewModel)
    {
        var currentUser = this.GetLogin();
        if (currentUser == null) return Unauthorized();
        
        if (tagViewModel.TagId == 0) throw new ArgumentException("Tag identifier mustn't be zero");

        await _tagsService.AddOrUpdateTag(tagViewModel);
        return Ok(true);
    }

    [HttpDelete("{tagId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Remove(int tagId)
    {
        var result = await _tagsService.RemoveTag(tagId);
        return Ok(result);
    }
}