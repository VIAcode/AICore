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
        protected readonly IServiceProvider ServiceProvider;

        public AgentServiceBase(
            ILoginProcessor loginProcessor,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceProvider serviceProvider)
        {
            _loginProcessor = loginProcessor;
            _debugLogProcessor = debugLogProcessor;
            _extendedConfig = extendedConfig;
            ServiceProvider = serviceProvider;
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
            try
            {
                var runAsUser = await _loginProcessor.GetById(runAs);
                if (runAsUser == null)
                    return "User not found";
                await using (var scope = ServiceProvider.CreateAsyncScope())
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
                    var result = "";
                    if (agentToCallModel.Type == AgentType.Composite)
                    {
                        var compositeAgent = scope.ServiceProvider.GetRequiredService<ICompositeAgent>();
                        result = await compositeAgent.DoCallWrapper(agentToCallModel, parametersValues);
                    }
                    else if (agentToCallModel.Type == AgentType.CsharpCode)
                    {
                        var csharpCodeAgent = scope.ServiceProvider.GetRequiredService<ICsharpCodeAgent>();
                        result = await csharpCodeAgent.DoCallWrapper(agentToCallModel, parametersValues);
                    }
                    else if (agentToCallModel.Type == AgentType.PythonCode)
                    {
                        var pythonCodeAgent = scope.ServiceProvider.GetRequiredService<IPythonCodeAgent>();
                        result = await pythonCodeAgent.DoCallWrapper(agentToCallModel, parametersValues);
                    }

                    if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                    {
                        var parametersString = string.Join(Environment.NewLine, parametersValues.Select(x => $" - {x.Key}: {x.Value}"));
                        var responseAccessor = scope.ServiceProvider.GetRequiredService<ResponseAccessor>();
                        await _debugLogProcessor.Add(
                            runAsUser.Login,
                            $"Agent ({sender}): {agentToCallModel.Name}{Environment.NewLine}Parameters:{Environment.NewLine}{parametersString}",
                            new MessageDialogViewModel
                            {
                                Messages = new List<MessageDialogViewModel.Message>
                                {
                                    new()
                                    {
                                        Text = result,
                                        SpentTokens = responseAccessor.CurrentMessage.SpentTokens,
                                        DebugMessages = responseAccessor.CurrentMessage.DebugMessages
                                    }
                                }
                            }, agentToCallModel.WorkspaceId ?? 0);
                    }
                    return result;
                }
            }
            catch (Exception e)
            {
                return $"Error: {e.Message}";
            }
        }
    }
}