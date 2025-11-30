using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/secrets")]
public class SecretsController : ControllerBase
{
    private readonly ISecretsService _secretsService;

    public SecretsController(
        ISecretsService secretsService)
    {
        _secretsService = secretsService;
    }

    [HttpGet]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin, Role.Developer)]
    public async Task<IActionResult> List()
    {
        var result = await _secretsService.List();
        return Ok(result);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] SecretExtendedItem secretViewModel)
    {
        secretViewModel = await _secretsService.Add(secretViewModel);
        return Ok(secretViewModel);
    }

    [HttpDelete("{secretId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Delete(int secretId)
    {
        await _secretsService.Delete(secretId);
        return Ok(true);
    }

    [HttpPost("dbAdd")]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> AddToDatabase([FromBody] SecretViewModel secretViewModel)
    {
        secretViewModel = await _secretsService.AddToDatabase(secretViewModel);
        return Ok(secretViewModel);
    }
}