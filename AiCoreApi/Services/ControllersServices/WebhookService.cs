using AiCoreApi.Authorization;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.SemanticKernel;
namespace AiCoreApi.Services.ControllersServices
{
    public class WebhookService : IWebhookService
    {
        private readonly ExtendedConfig _extendedConfig;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IDebugLogProcessor _debugLogProcessor;

        public WebhookService(IPlannerHelpers plannerHelpers,
            ExtendedConfig extendedConfig,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IDebugLogProcessor debugLogProcessor)
        {
            _extendedConfig = extendedConfig;
            _plannerHelpers = plannerHelpers;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _debugLogProcessor = debugLogProcessor;
        }

        public async Task<string> WebHook(string action, string method, string query, string body)
        {
            if(!_extendedConfig.UseWebHooks)
                throw new ExceptionHandlingMiddleware.AiCoreUiException("WebHooks are not enabled in the configuration.");
            var agentsList = await _plannerHelpers.GetAgentsList();
            var agent = agentsList.FirstOrDefault(e => e.Name.Equals(action, StringComparison.OrdinalIgnoreCase));
            if (agent == null)
                throw new ExceptionHandlingMiddleware.AiCoreUiException($"No agent found for action: {action}");
            if (!agent.Content.ContainsKey(AgentTypeCalls.AgentCallTypeFieldName) || !agent.Content[AgentTypeCalls.AgentCallTypeFieldName].Value.Contains(AgentTypeCalls.WebHook))
                throw new ExceptionHandlingMiddleware.AiCoreUiException($"Agent {agent.Name} cannot be called via WebHook.");
            var result = await RunAgent(agent.Name, new List<string> { method, query, body});
            if (_extendedConfig.AllowDebugMode && _extendedConfig.UseDebugModeForWebHooks)
            {
                var parametersString = $"Method: {method}{Environment.NewLine}Action: {action}{Environment.NewLine}Query: {query}{Environment.NewLine}Body: {body}";
                await _debugLogProcessor.Add(
                    "WebHook",
                    $"Agent: {agent.Name}{Environment.NewLine}{Environment.NewLine}{parametersString}",
                    new MessageDialogViewModel
                    {
                        Messages = new List<MessageDialogViewModel.Message>
                        {
                            new()
                            {
                                Text = result,
                                SpentTokens = _responseAccessor.CurrentMessage.SpentTokens,
                                DebugMessages = _responseAccessor.CurrentMessage.DebugMessages
                            }
                        }
                    }, agent.WorkspaceId ?? 0);
            }
            return result;
        }

        private async Task<string> RunAgent(string agentName, List<string> parameters)
        {
            if (_responseAccessor.CurrentMessage.DebugMessages != null)
                _responseAccessor.CurrentMessage.DebugMessages.Clear();
            _requestAccessor.UseDebug = _extendedConfig.UseDebugModeForWebHooks;
            _requestAccessor.IsWebHookCall = true;
            _requestAccessor.MessageDialog = new MessageDialogViewModel
            {
                Messages = new List<MessageDialogViewModel.Message>
                {
                    new()
                    {
                        Sender = "User",
                        Text = string.Empty,
                        Options = new MessageDialogViewModel.CallOptions[]
                        {
                            new()
                            {
                                Type = MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall,
                                Name = agentName,
                                Parameters = parameters
                                    .Select((p, i) => new {name = $"parameter{i + 1}", value = p})
                                    .ToDictionary(p => p.name, p => p.value),
                            }
                        }
                    }
                }
            };
            var result = await _plannerHelpers.ExecuteAgent(agentName, parameters);
            return result;
        }
    }

    public interface IWebhookService
    {
        Task<string> WebHook(string action, string method, string query, string body);
    }
}

