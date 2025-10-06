using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel.Agents;
using AgentType = AiCoreApi.Models.DbModels.AgentType;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class BackgroundWorkerAgentService : IBackgroundWorkerAgentService
    {
        private readonly ISchedulerAgentTaskProcessor _schedulerAgentTaskProcessor;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IDebugLogProcessor _debugLogProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly IServiceProvider _serviceProvider; 

        public BackgroundWorkerAgentService(
            ISchedulerAgentTaskProcessor schedulerAgentTaskProcessor,
            IAgentsProcessor agentsProcessor,
            IDebugLogProcessor debugLogProcessor,
            ILoginProcessor loginProcessor,
            ExtendedConfig extendedConfig,
            IServiceProvider serviceProvider)
        {
            _schedulerAgentTaskProcessor = schedulerAgentTaskProcessor;
            _agentsProcessor = agentsProcessor;
            _debugLogProcessor = debugLogProcessor;
            _loginProcessor = loginProcessor;
            _extendedConfig = extendedConfig;
            _serviceProvider = serviceProvider;
        }


        public async Task ProcessTask()
        {
            var schedulerAgentTaskModel = await _schedulerAgentTaskProcessor.GetNext();
            while (schedulerAgentTaskModel != null)
            {
                await using (var scope = _serviceProvider.CreateAsyncScope())
                {
                    var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                    var responseAccessor = scope.ServiceProvider.GetRequiredService<ResponseAccessor>();
                    var workspaceId = 0;
                    var result = "";
                    Dictionary<string, string> parametersValues = new();
                    try
                    {
                        var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
                        requestAccessor.SetRequestAccessor(schedulerAgentTaskModel.RequestAccessor);
                        if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled &&
                            _extendedConfig.UseDebugLogForEachCall)
                        {
                            requestAccessor.UseDebug = true;
                        }

                        userContextAccessor.SetLoginId(schedulerAgentTaskModel.LoginId);
                        UserContextAccessor.AsyncScheduledLoginId.Value = schedulerAgentTaskModel.LoginId;
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.InProgress;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
                        var agentToCallModel = await _agentsProcessor.GetByName(schedulerAgentTaskModel.CompositeAgentName, requestAccessor.WorkspaceId);
                        if (agentToCallModel == null)
                        {
                            schedulerAgentTaskModel.Result = "Agent to call not found";
                            schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Failed;
                            await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
                            return;
                        }
                        parametersValues = schedulerAgentTaskModel.Parameters.JsonGet<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                        workspaceId = agentToCallModel.WorkspaceId ?? 0;
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
                            AgentType.CompositeCSharp => await scope.ServiceProvider
                                .GetRequiredService<ICompositeCSharpAgent>()
                                .DoCallWrapper(agentToCallModel, parametersValues),
                            AgentType.CompositePython => await scope.ServiceProvider
                                .GetRequiredService<ICompositePythonAgent>()
                                .DoCallWrapper(agentToCallModel, parametersValues),
                            AgentType.CompositeLoop => await scope.ServiceProvider
                                .GetRequiredService<ICompositeLoopAgent>()
                                .DoCallWrapper(agentToCallModel, parametersValues),
                            AgentType.Flow => await scope.ServiceProvider.GetRequiredService<IFlowAgent>()
                                .DoCallWrapper(agentToCallModel, parametersValues),
                            _ => throw new NotSupportedException($"Unsupported agent type: {agentToCallModel.Type}")
                        };
                        schedulerAgentTaskModel.Result = HttpUtility.HtmlDecode(result);
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Completed;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);

                    }
                    catch (Exception e)
                    {
                        schedulerAgentTaskModel.Result = e.Message;
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Failed;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
                    }
                    finally
                    {
                        var login = await _loginProcessor.GetById(schedulerAgentTaskModel.LoginId);
                        if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                        {
                            var parametersString = string.Join(Environment.NewLine, parametersValues.Select(x => $" - {x.Key}: {x.Value}"));
                            await _debugLogProcessor.Add(
                                login?.Login ?? "",
                                $"Agent (Background): {schedulerAgentTaskModel.CompositeAgentName}{Environment.NewLine}Parameters:{Environment.NewLine}{parametersString}",
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
                                }, workspaceId);
                        }
                    }
                }
                schedulerAgentTaskModel = await _schedulerAgentTaskProcessor.GetNext();
            }
            await _schedulerAgentTaskProcessor.RemoveExpired();
        }
    }

    public interface IBackgroundWorkerAgentService
    {
        public Task ProcessTask();
    }
}