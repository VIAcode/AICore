using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/entra")]
public class EntraController : ControllerBase
{
    private readonly IEntraService _entraService;

    public EntraController(
        IEntraService entraService)
    {
        _entraService = entraService;
    }

    [HttpGet]
    [CombinedAuthorize]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> List()
    {
        var result = await _entraService.ListEntraCredentials();
        return Ok(result);
    }

    [HttpPost]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Add([FromBody] EntraCredentialExtendedItem entraCredentialViewModel)
    {
        entraCredentialViewModel = await _entraService.AddEntraCredential(entraCredentialViewModel);
        return Ok(entraCredentialViewModel);
    }

    [HttpDelete("{entraCredentialId}")]
    [RoleAuthorize(Role.Admin)]
    public async Task<IActionResult> Delete(int entraCredentialId)
    {
        await _entraService.DeleteEntraCredential(entraCredentialId);
        return Ok(true);
    }
}