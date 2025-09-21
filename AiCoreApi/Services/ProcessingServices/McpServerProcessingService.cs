using System.Text.Json;
using ModelContextProtocol.Server;
using AiCoreApi.SemanticKernel;
using AiCoreApi.Authorization;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using ModelContextProtocol.Protocol;

namespace AiCoreApi.Services.ProcessingServices
{
    public class McpServerProcessingService: IMcpServerProcessingService
    {
        private readonly IDebugLogProcessor _debugLogProcessor;
        private readonly ILoginProcessor _loginProcessor; 
        private readonly ExtendedConfig _extendedConfig;
        private readonly IPlannerHelpers _plannerHelpers;
        public const string ParameterDescription = "parameterDescription";

        public McpServerProcessingService(
            IDebugLogProcessor debugLogProcessor,
            ILoginProcessor loginProcessor,
            ExtendedConfig extendedConfig,
            IPlannerHelpers plannerHelpers)
        {
            _debugLogProcessor = debugLogProcessor;
            _loginProcessor = loginProcessor;
            _extendedConfig = extendedConfig;
            _plannerHelpers = plannerHelpers;
        }

        public async Task<ValueTask<ListToolsResult>> ListTools(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
        {
            var agentsList = (await _plannerHelpers.GetAgentsList())
                .Where(agent =>
                    agent.Content.ContainsKey(AgentTypeCalls.AgentCallTypeFieldName) &&
                    agent.Content[AgentTypeCalls.AgentCallTypeFieldName].Value.Contains(AgentTypeCalls.McpCall))
                .Select(agent =>
                {
                    var parameters = new List<string>();
                    if (agent.Content.ContainsKey(ParameterDescription) && !string.IsNullOrWhiteSpace(agent.Content[ParameterDescription].Value))
                    {
                        parameters = agent.Content[ParameterDescription].Value
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .ToList();
                    }
                    return new Tool
                    {
                        Name = GetAlias(agent.Name),
                        Description = agent.Description,
                        InputSchema = JsonDocument.Parse($@"
                        {{
                            ""type"": ""object"",
                            ""properties"": {{
                                {string.Join(",", parameters.Select(parameter => $@"
                                    ""{GetAlias(parameter)}"": {{
                                        ""type"": ""string"",
                                        ""description"": ""{parameter}""
                                    }}"
                                ))}
                            }},
                            ""required"": [{string.Join(",", parameters.Select(param => $@"""{GetAlias(param)}"""))}]
                        }}").RootElement
                    };
                }).ToList();

            var result = new ListToolsResult { Tools = agentsList };
            return ValueTask.FromResult(result);
        }

        public async Task<ValueTask<CallToolResult>> CallTool(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        {
            await using (var scope = context.Services.CreateAsyncScope())
            {
                var isError = false;
                if (!_extendedConfig.UseMcpServer)
                    return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "MCP Server is not configured in the system." } } });
                var mcpServerUser = _extendedConfig.McpServerUser;
                var publicLogin = await _loginProcessor.GetByLogin(mcpServerUser, LoginTypeEnum.Password);
                if (publicLogin == null)
                    return ValueTask.FromResult(new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = $"Public login '{mcpServerUser}' not found." } } });

                var httpContextAccessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                SetContext(scope.ServiceProvider, publicLogin, httpContextAccessor.HttpContext.Request.Headers);
                SetContext(httpContextAccessor.HttpContext!.RequestServices, publicLogin, httpContextAccessor.HttpContext.Request.Headers);

                var result = string.Empty;
                var agentName = context.Params.Name;
                var parameters = new List<string>();

                var plannerHelpers = scope.ServiceProvider.GetRequiredService<IPlannerHelpers>();
                var agents = await plannerHelpers.GetAgentsList();
                var agent = agents.FirstOrDefault(agentItem => GetAlias(agentItem.Name) == agentName);
                var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                if (agent == null)
                {
                    isError = true;
                    result = $"Agent {agentName} not found.";
                }
                else if (!agent.Content.ContainsKey(AgentTypeCalls.AgentCallTypeFieldName) ||
                         !agent.Content[AgentTypeCalls.AgentCallTypeFieldName].Value.Contains(AgentTypeCalls.McpCall))
                {
                    isError = true;
                    result = $"Agent {agentName} can not ba called via MCP.";
                }
                else
                {
                    if (agent.Content.ContainsKey(ParameterDescription) &&
                        !string.IsNullOrWhiteSpace(agent.Content[ParameterDescription].Value) &&
                        context.Params.Arguments != null)
                    {
                        parameters = agent.Content[ParameterDescription].Value
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(GetAlias)
                            .Select(param =>
                                context.Params.Arguments.ContainsKey(param)
                                    ? $"{context.Params.Arguments[param].GetString()}"
                                    : string.Empty)
                            .ToList();
                    }

                    requestAccessor.WorkspaceId = agent.WorkspaceId;
                    try
                    {
                        result = await plannerHelpers.ExecuteAgent(agent.Name, parameters);
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        result = ex.Message;
                    }
                }
                var message = $"MCP Server call, Agent: {agentName}, Parameters: {string.Join(", ", parameters)}.";
                await _debugLogProcessor.Add(requestAccessor.Login, message, requestAccessor.MessageDialog, agent.WorkspaceId ?? 0);

                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = isError,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = result } }
                });
            }
        }

        private void SetContext(IServiceProvider serviceProvider, LoginModel login, IHeaderDictionary headerDictionary)
        {
            var requestAccessor = serviceProvider.GetRequiredService<RequestAccessor>();
            var userContextAccessor = serviceProvider.GetRequiredService<UserContextAccessor>();
            requestAccessor.MessageDialog = new MessageDialogViewModel
            {
                Messages = new List<MessageDialogViewModel.Message>
                {
                    new MessageDialogViewModel.Message
                    {
                        Text = "",
                        Sender = "",
                    }
                }
            };
            userContextAccessor.SetLoginId(login.LoginId);
            userContextAccessor.SetTags(login.Tags);
            UserContextAccessor.AsyncScheduledLoginId.Value = login.LoginId;
            if (!string.IsNullOrEmpty(_extendedConfig.McpAuthHeaderName) && headerDictionary.ContainsKey(_extendedConfig.McpAuthHeaderName))
            {
                requestAccessor.McpAuthHeader = headerDictionary[_extendedConfig.McpAuthHeaderName];
            }
            requestAccessor.IsMcpCall = true;
            requestAccessor.Login = login.Login;
            requestAccessor.LoginTypeString = LoginTypeEnum.Password.ToString();
        }

        private static string GetAlias(string name) => name.ToLower().Trim().Replace(" ", "_");
    }

    public interface IMcpServerProcessingService
    {
        Task<ValueTask<ListToolsResult>> ListTools(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken);
        Task<ValueTask<CallToolResult>> CallTool(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken);
    }
}