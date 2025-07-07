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

    [AllowAnonymous, HttpGet("{actionName}")]
    public Task<IActionResult> WebHookGet(string actionName) =>
        HandleWebhookAsync(actionName, "GET", string.Empty);

    [AllowAnonymous, HttpPost("{actionName}")]
    public async Task<IActionResult> WebHookPost(string actionName) =>
        await HandleWebhookAsync(actionName, "POST", await GetBody());

    [AllowAnonymous, HttpPut("{actionName}")]
    public async Task<IActionResult> WebHookPut(string actionName) =>
        await HandleWebhookAsync(actionName, "PUT", await GetBody());

    [AllowAnonymous, HttpDelete("{actionName}")]
    public async Task<IActionResult> WebHookDelete(string actionName) =>
        await HandleWebhookAsync(actionName, "DELETE", await GetBody());

    private async Task<IActionResult> HandleWebhookAsync(string actionName, string method, string body)
    {
        var queryString = HttpContext.Request.QueryString.Value ?? string.Empty;
        var result = await _webhookService.WebHook(actionName, method, queryString, body);
        var contentType = result.IsJson() ? "application/json" : "text/plain";
        return Content(result, contentType);
    }

    private async Task<string> GetBody()
    {
        var request = HttpContext.Request;
        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}