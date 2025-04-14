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
    [Route("api/v1/rbac/roles")]
    public class RbacRoleSyncController : ControllerBase
    {
        private readonly IRbacRoleSyncService _rbacRoleSyncService;
        public RbacRoleSyncController(IRbacRoleSyncService rbacRoleSyncService)           
        {
            _rbacRoleSyncService = rbacRoleSyncService;
        }

        [HttpGet]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> List()
        {
            return Ok(await _rbacRoleSyncService.ListRbacRoleSyncs());
        }

        [HttpDelete("{rbacRoleSyncId}")]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> DeleteRbacRoleSync(int rbacRoleSyncId)
        {
            await _rbacRoleSyncService.DeleteRbacRoleSync(rbacRoleSyncId);
            return Ok();
        }

        [HttpPost]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> Add([FromBody] RbacRoleSyncViewModel rbacRoleSyncViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null)
                return Unauthorized();
            rbacRoleSyncViewModel.CreatedBy = currentUser;
            await _rbacRoleSyncService.AddRbacRoleSync(rbacRoleSyncViewModel);
            return Ok(true);
        }

        [HttpPut]
        [RoleAuthorize(Role.Admin)]
        public async Task<IActionResult> Update([FromBody] RbacRoleSyncViewModel rbacRoleSyncViewModel)
        {
            var currentUser = this.GetLogin();
            if (currentUser == null)
                return Unauthorized();
            rbacRoleSyncViewModel.UpdatedBy = currentUser;
            await _rbacRoleSyncService.UpdateRbacRoleSync(rbacRoleSyncViewModel);
            return Ok(true);
        }
    }
}
