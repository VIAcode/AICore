using AiCoreApi.Authorization;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.SemanticKernel;
using AiCoreApi.Models.DbModels;
namespace AiCoreApi.Services.ControllersServices
{
    public class WebhookService : IWebhookService
    {
        private readonly ExtendedConfig _extendedConfig;
        private readonly IAgentExecutor _agentExecutor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IDebugLogProcessor _debugLogProcessor;
        private readonly ILoginProcessor _loginProcessor;

        public WebhookService(IPlannerHelpers plannerHelpers,
            ExtendedConfig extendedConfig,
            IAgentExecutor agentExecutor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IDebugLogProcessor debugLogProcessor,
            ILoginProcessor loginProcessor)
        {
            _extendedConfig = extendedConfig;
            _agentExecutor = agentExecutor;
            _plannerHelpers = plannerHelpers;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _debugLogProcessor = debugLogProcessor;
            _loginProcessor = loginProcessor;
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
            var result = string.Empty;
            try
            {
                result = await RunAgent(agent.Name, new List<string> { method, query, body });
            }
            finally
            {
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
            }
            return result;
        }

        private async Task<string> RunAgent(string agentName, List<string> parameters)
        {
            if (_responseAccessor.CurrentMessage.DebugMessages != null)
                _responseAccessor.CurrentMessage.DebugMessages.Clear();
            _requestAccessor.UseDebug = _extendedConfig.UseDebugModeForWebHooks;
            
            var webHookCallsUser = _extendedConfig.WebHooksCallsUser;

            if (string.IsNullOrWhiteSpace(webHookCallsUser))
                throw new ExceptionHandlingMiddleware.AiCoreAuthException("WebHooksCallsUser configuration is missing or empty.");

            var webHookLogin = await _loginProcessor.GetByLogin(webHookCallsUser, LoginTypeEnum.Password);
            if (webHookLogin == null)
                throw new ExceptionHandlingMiddleware.AiCoreAuthException($"WebHook user login '{webHookCallsUser}' not found.");

            _requestAccessor.IsWebHookCall = true;
            _requestAccessor.Login = webHookLogin.Login;
            _requestAccessor.LoginTypeString = LoginTypeEnum.Password.ToString();
            _requestAccessor.UserContext.SetLoginId(webHookLogin.LoginId);
            _requestAccessor.UserContext.SetTags(webHookLogin.Tags);
            _requestAccessor.Tags = webHookLogin.Tags
                .Select(tag => tag.TagId)
                .ToList();

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
            var result = await _agentExecutor.ExecuteAsync(agentName, parameters);
            return result;
        }
    }

    public interface IWebhookService
    {
        Task<string> WebHook(string action, string method, string query, string body);
    }
}

