using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel.Agents;
using AgentType = AiCoreApi.Models.DbModels.AgentType;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class AgentServiceBase
    {
        private readonly ILoginProcessor _loginProcessor;
        private readonly IDebugLogProcessor _debugLogProcessor;
        private readonly ExtendedConfig _extendedConfig; 
        protected readonly IServiceScopeFactory ScopeFactory;

        public AgentServiceBase(
            ILoginProcessor loginProcessor,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceScopeFactory scopeFactory)
        {
            _loginProcessor = loginProcessor;
            _debugLogProcessor = debugLogProcessor;
            _extendedConfig = extendedConfig;
            ScopeFactory = scopeFactory;
        }

        public async Task RunAgent(string sender, List<AgentModel> allAgents, AgentModel handlerAgent, string agentToCallName, int runAs, Dictionary<string, string> parametersValues)
        {

            var agentToCallModel = allAgents.FirstOrDefault(item => item.Name.ToLower() == agentToCallName.ToLower());
            if (agentToCallModel == null)
            {
                handlerAgent.Content["lastResult"].Value = $"Agent not found: {agentToCallName}";
            }
            else
            {
                var result = await RunAgent(sender, agentToCallModel, runAs, parametersValues);
                handlerAgent.Content["lastResult"].Value = result;
            }
            handlerAgent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
        }

        public async Task<string> RunAgent(string sender, AgentModel agentToCallModel, int runAs, Dictionary<string, string> parametersValues)
        {
            var result = "";
            var login = "";
            var currentMessage = new MessageDialogViewModel.Message();
            try
            {
                var runAsUser = await _loginProcessor.GetById(runAs);
                if (runAsUser == null)
                    return "User not found";
                await using (var scope = ScopeFactory.CreateAsyncScope())
                {
                    var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
                    var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                    requestAccessor.MessageDialog = new MessageDialogViewModel
                    {
                        Messages = new List<MessageDialogViewModel.Message>
                        {
                            new()
                            {
                                Text = $"{sender} task",
                                Sender = sender,
                            }
                        }
                    };
                    // Set user context with all tags for scheduled task
                    requestAccessor.Login = runAsUser.Login;
                    requestAccessor.LoginTypeString = runAsUser.LoginType.ToString();
                    requestAccessor.TagsString = string.Join(",", runAsUser.Tags.Select(tag => tag.TagId));
                    requestAccessor.WorkspaceId = agentToCallModel.WorkspaceId ?? 0;
                    if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                    {
                        requestAccessor.UseDebug = true;
                    }

                    userContextAccessor.SetLoginId(runAs);
                    UserContextAccessor.AsyncScheduledLoginId.Value = runAs;
                    result = agentToCallModel.Type switch
                    {
                        AgentType.Composite => await scope.ServiceProvider.GetRequiredService<ICompositeAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.CsharpCode => await scope.ServiceProvider.GetRequiredService<ICsharpCodeAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.PythonCode => await scope.ServiceProvider.GetRequiredService<IPythonCodeAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.NodeJsCode => await scope.ServiceProvider.GetRequiredService<INodeJsCodeAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.CompositeCSharp => await scope.ServiceProvider.GetRequiredService<ICompositeCSharpAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.CompositePython => await scope.ServiceProvider.GetRequiredService<ICompositePythonAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.CompositeLoop => await scope.ServiceProvider.GetRequiredService<ICompositeLoopAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.CompositeLoopV2 => await scope.ServiceProvider.GetRequiredService<ICompositeLoopV2Agent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        AgentType.Flow => await scope.ServiceProvider.GetRequiredService<IFlowAgent>()
                            .DoCallWrapper(agentToCallModel, parametersValues),
                        _ => throw new NotSupportedException($"Unsupported agent type: {agentToCallModel.Type}")
                    };
                    var responseAccessor = scope.ServiceProvider.GetRequiredService<ResponseAccessor>();
                    login = runAsUser.Login;
                    currentMessage = responseAccessor.CurrentMessage;
                }
            }
            catch (Exception e)
            {
                return $"Error: {e.Message}";
            }
            finally
            {
                if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                {
                    var parametersString = string.Join(Environment.NewLine,
                        parametersValues.Select(x => $" - {x.Key}: {x.Value}"));
                    await _debugLogProcessor.Add(
                        login,
                        $"Agent ({sender}): {agentToCallModel.Name}{Environment.NewLine}Parameters:{Environment.NewLine}{parametersString}",
                        new MessageDialogViewModel
                        {
                            Messages = new List<MessageDialogViewModel.Message>
                            {
                                new()
                                {
                                    Text = result,
                                    SpentTokens = currentMessage.SpentTokens,
                                    DebugMessages = currentMessage.DebugMessages
                                }
                            }
                        }, agentToCallModel.WorkspaceId ?? 0);
                }

            }
            return result;
        }
    }
}