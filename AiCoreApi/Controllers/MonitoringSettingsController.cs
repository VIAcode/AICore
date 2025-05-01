using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/monitoring")]
    public class MonitoringSettingsController : ControllerBase
    {
        private readonly IMonitoringSettingsService _monitoringSettingsService;
        public MonitoringSettingsController(IMonitoringSettingsService monitoringSettingsService)           
        {
            _monitoringSettingsService = monitoringSettingsService;
        }

        [HttpGet]
        [RoleAuthorize(Role.Admin)]
        [CombinedAuthorize]
        public IActionResult Get()
        {
            return Ok(_monitoringSettingsService.Get());
        }

        [HttpPost]
        [RoleAuthorize(Role.Admin)]
        [CombinedAuthorize]
        public IActionResult Save([FromBody] MonitoringSettingsViewModel settings)
        {
            _monitoringSettingsService.Set(settings);
            return Ok();
        }

        [HttpPost("reboot")]
        [RoleAuthorize(Role.Admin)]
        [CombinedAuthorize]
        public IActionResult Reboot()
        {
            _monitoringSettingsService.Reboot();
            return Ok();
        }
    }
}
