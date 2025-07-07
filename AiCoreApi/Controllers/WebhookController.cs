using AiCoreApi.Common.Extensions;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AiCoreApi.Controllers;

[ApiController]
[Route("api/v1/webhooks")]
public class WebhookController : ControllerBase
{
    private readonly IWebhookService _webhookService;

    public WebhookController(
        IWebhookService webhookService)
    {
        _webhookService = webhookService;
    }

    [AllowAnonymous]
    [HttpGet("{actionName}")]
    public async Task<IActionResult> WebHookGet(string actionName)
    {
        var queryString = HttpContext.Request.QueryString.Value ?? string.Empty;
        var text = await _webhookService.WebHook(actionName, "GET", queryString, string.Empty);
        return Content(text, text.IsJson() ? "application/json" : "text/plain");
    }

    [AllowAnonymous]
    [HttpPost("{actionName}")]
    public async Task<IActionResult> WebHookPost(string actionName)
    {
        var queryString = HttpContext.Request.QueryString.Value ?? string.Empty;
        var text = await _webhookService.WebHook(actionName, "POST", queryString, GetBody());
        return Content(text, text.IsJson() ? "application/json" : "text/plain");
    }

    [AllowAnonymous]
    [HttpPut("{actionName}")]
    public async Task<IActionResult> WebHookPut(string actionName)
    {
        var queryString = HttpContext.Request.QueryString.Value ?? string.Empty;
        var text = await _webhookService.WebHook(actionName, "PUT", queryString, GetBody());
        return Content(text, text.IsJson() ? "application/json" : "text/plain");
    }

    [AllowAnonymous]
    [HttpDelete("{actionName}")]
    public async Task<IActionResult> WebHookDelete(string actionName)
    {
        var queryString = HttpContext.Request.QueryString.Value ?? string.Empty;
        var text = await _webhookService.WebHook(actionName, "DELETE", queryString, GetBody());
        return Content(text, text.IsJson() ? "application/json" : "text/plain");
    }

    private string GetBody()
    {
        var request = HttpContext.Request;
        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = reader.ReadToEndAsync().Result;
        request.Body.Position = 0;
        return body;
    }
}