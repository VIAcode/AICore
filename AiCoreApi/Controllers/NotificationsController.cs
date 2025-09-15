using AiCoreApi.Common;
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
    private readonly RequestAccessor _requestAccessor;

    public NotificationsController(
        INotificationsService notificationsService,
        RequestAccessor requestAccessor)
    {
        _notificationsService = notificationsService;
        _requestAccessor = requestAccessor;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if(_requestAccessor.Login == null) 
            return Unauthorized();
        var notifications = await _notificationsService.List(_requestAccessor.Login);
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