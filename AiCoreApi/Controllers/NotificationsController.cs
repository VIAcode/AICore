using AiCoreApi.Common.Extensions;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCoreApi.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationsService _notificationsService;
    public NotificationsController(INotificationsService notificationsService)
    {
        _notificationsService = notificationsService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var notifications = await _notificationsService.List();
        return Ok(notifications);
    }

    [Authorize]
    [HttpPut("{notificationId}/read")]
    public async Task<IActionResult> MarkAsRead(int notificationId)
    {

        await _notificationsService.MarkAsRead(notificationId);
        return Ok();
    }
}