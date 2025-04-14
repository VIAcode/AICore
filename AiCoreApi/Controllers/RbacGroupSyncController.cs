using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/rbac/groups")]
    public class RbacGroupSyncController : ControllerBase
    {
        private readonly IRbacGroupSyncService _rbacGroupSyncService;
        public RbacGroupSyncController(IRbacGroupSyncService rbacGroupSyncService)           
        {
            _rbacGroupSyncService = rbacGroupSyncService;
        }

        [HttpGet]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> List()
        {
            return Ok(await _rbacGroupSyncService.ListRbacGroupSyncs());
        }

        [HttpDelete("{rbacGroupSyncId}")]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> DeleteRbacGroupSync(int rbacGroupSyncId)
        {
            await _rbacGroupSyncService.DeleteRbacGroupSync(rbacGroupSyncId);
            return Ok();
        }

        [HttpPost]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> Add([FromBody] RbacGroupSyncViewModel rbacGroupSyncViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null)
                return Unauthorized();
            rbacGroupSyncViewModel.CreatedBy = currentUser;
            await _rbacGroupSyncService.AddRbacGroupSync(rbacGroupSyncViewModel);
            return Ok(true);
        }

        [HttpPut]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> Update([FromBody] RbacGroupSyncViewModel rbacGroupSyncViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null)
                return Unauthorized();
            rbacGroupSyncViewModel.UpdatedBy = currentUser;
            await _rbacGroupSyncService.UpdateRbacGroupSync(rbacGroupSyncViewModel);
            return Ok(true);
        }
    }
}
