using AiCoreApi.Authorization;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/copilot")]
    public class CopilotController : ControllerBase
    {
        private readonly ICopilotService _copilotService;
        public CopilotController(
            ICopilotService copilotService)           
        {
            _copilotService = copilotService;
        }

        [HttpPost("chat")]
        [CombinedAuthorize]
        [Consumes("application/json")]
        [Produces("application/json", "text/plain")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(MessageDialogViewModel))]
        [SwaggerOperation(Summary = "Chat endpoint, main entrypoint for communication with AI Core.")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> Chat(
            [FromBody]
            [SwaggerRequestBody("Previous dialog with the question as last element", Required = true)] 
            MessageDialogViewModel? messageDialog,
            [FromQuery(Name = "tags")]
            [SwaggerParameter("Tag Ids, comma separated. To get Tags, available for the user, use /api/v1/tags/my. If no tags specified and TAGGING feature is enabled, user get nothing.", Required = false)] 
            string tags = "0",
            [FromQuery(Name = "connection_name")]
            [SwaggerParameter("Connection Name from 'Connections' tab. Used as Default LLM for all agents. Optional. In not specified, then the first one will be used.", Required = false)]
            string connectionName = "",
            [FromQuery(Name = "use_markdown")]
            [SwaggerParameter("Flag to specify if we can use markdown in output or it must be plain text.", Required = false)]
            bool useMarkdown = true,
            [FromQuery(Name = "use_bing")]
            [SwaggerParameter("Allow to use Bing Agent or not. In case if Bing Agent disabled, flag ignored.", Required = false)]
            bool useBing = false,
            [FromQuery(Name = "use_cached_plan")]
            [SwaggerParameter("Allow Administrator to execute request without using Plan from Cache.", Required = false)]
            bool useCachedPlan = true,
            [FromQuery(Name = "use_debug")]
            [SwaggerParameter("Allow Administrator to get debug information while executing Plan.", Required = false)]
            bool useDebug = false,
            [FromQuery(Name = "simple_output")]
            [SwaggerParameter("Flag to specify if we need structured output with history or just result.", Required = false)]
            bool simpleOutput = false, 
            [FromQuery(Name = "workspace_id")]
            [SwaggerParameter("Workspace Id, use 0 for default.", Required = false)]
            int workspaceId = 0)
        {
            var result = await _copilotService.Chat();
            if (simpleOutput)
            {
                var text = result.Messages?.LastOrDefault()?.Text ?? "";
                return Content(text, text.IsJson() ? "application/json" : "text/plain");
            }
            return Ok(result);
        }

        [HttpPost("agent/{agentName}")]
        [CombinedAuthorize]
        [Consumes("application/json")]
        [Produces("application/json", "text/plain")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(MessageDialogViewModel))]
        [SwaggerOperation(Summary = "Agents call endpoint. Entrypoint for direct calling Agents. Wrapper for chat endpoint.")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> Agent(
            string agentName,
            [FromBody]
            [SwaggerRequestBody("Agents Parameters values")]
            Dictionary<string, string>? parameters = null,
            [FromQuery(Name = "tags")]
            [SwaggerParameter("Tag Ids, comma separated. To get Tags, available for the user, use /api/v1/tags/my. If no tags specified and TAGGING feature is enabled, user get nothing.", Required = false)] 
            string tags = "0",
            [FromQuery(Name = "connection_name")]
            [SwaggerParameter("Connection Name from 'Connections' tab. Used as Default LLM for all agents. Optional. In not specified, then the first one will be used.", Required = false)]
            string connectionName = "",
            [FromQuery(Name = "use_debug")]
            [SwaggerParameter("Allow Administrator to get debug information while executing Plan.", Required = false)]
            bool useDebug = false,
            [FromQuery(Name = "simple_output")]
            [SwaggerParameter("Flag to specify if we need structured output with history or just result.", Required = false)]
            bool simpleOutput = false,
            [FromQuery(Name = "workspace_id")]
            [SwaggerParameter("Workspace Id, use 0 for default.", Required = false)]
            int workspaceId = 0)
        {
            var result = await _copilotService.Agent(agentName, parameters);
            if (simpleOutput)
            {
                var text = result.Messages?.LastOrDefault()?.Text ?? "";
                return Content(text, text.IsJson() ? "application/json" : "text/plain");
            }
            return Ok(result);
        }

        [HttpPost("prompt")]
        [CombinedAuthorize]
        [Consumes("application/json")]
        [Produces("text/plain")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(string))]
        [SwaggerOperation(Summary = "Prompt endpoint. LLM usage without agents.")]
        public async Task<IActionResult> Prompt(
            [FromBody]
            [SwaggerRequestBody("Prompt for LLM", Required = true)]
            string prompt,
            [FromQuery(Name = "temperature")]
            [SwaggerParameter("Temperature", Required = false)]
            double temperature = 0,
            [FromQuery(Name = "connection_name")]
            [SwaggerParameter("Connection Name from 'Connections' tab. Used as Default LLM for all agents. Optional. In not specified, then the first one will be used.", Required = false)]
            string connectionName = "")
        {
            var result = await _copilotService.Prompt(prompt, temperature, connectionName);
            return Ok(result);
        }

        [HttpGet("search")]
        [CombinedAuthorize]
        [Consumes("application/json")]
        [Produces("application/json")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(List<SearchItemModel>))]
        [SwaggerOperation(Summary = "Search endpoint. Not needed to use ChatGPT for search, its just Vector Search. Its usage cost is low.")]
        public async Task<IActionResult> Search(
            [FromQuery(Name = "q")]
            [SwaggerRequestBody("Search Query", Required = true)]
            string query,
            [FromQuery(Name = "tags")]
            [SwaggerParameter("Tag Ids, comma separated. To get Tags, available for the user, use /api/v1/tags/my. If no tags specified and TAGGING feature is enabled, user get nothing.", Required = false)]
            string tags = "0")
        {
            var result = await _copilotService.Search();
            return Ok(result);
        }

        [HttpPost("transcript")]
        [CombinedAuthorize]
        [Produces("text/plain")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(string))]
        [SwaggerOperation(Summary = "Transcribe the fle. Input - audio file, output - text from this file.")]
        public async Task<IActionResult> Transcribe(IFormFile file)
        {
            var result = await _copilotService.Transcribe(file);
            return Ok(result);
        }

        [HttpPost("proxy")]
        [CombinedAuthorize]
        [Consumes("application/json")]
        [SwaggerResponse(StatusCodes.Status200OK, Type = typeof(string))]
        [SwaggerOperation(Summary = "Proxy the request to some external service if the service is not accessible from UI because of CORS or other browser UI limitations.")]
        public async Task<IActionResult> Proxy([FromBody] ProxyRequestModel proxyRequest)
        {
            var result = await _copilotService.Proxy(proxyRequest);
            return Ok(result);
        }
    }
}
