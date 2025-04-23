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
                    try
                    {
                        var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
                        var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                        var responseAccessor = scope.ServiceProvider.GetRequiredService<ResponseAccessor>();
                        requestAccessor.SetRequestAccessor(schedulerAgentTaskModel.RequestAccessor);
                        if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
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
                        var parameters = schedulerAgentTaskModel.Parameters.JsonGet<Dictionary<string, string>>() ?? new Dictionary<string, string>();
                        var result = string.Empty;
                        if (agentToCallModel.Type == AgentType.Composite)
                        {
                            var compositeAgent = scope.ServiceProvider.GetRequiredService<ICompositeAgent>();
                            result = await compositeAgent.DoCallWrapper(agentToCallModel, parameters);
                        }
                        else if (agentToCallModel.Type == AgentType.CsharpCode)
                        {
                            var csharpCodeAgent = scope.ServiceProvider.GetRequiredService<ICsharpCodeAgent>();
                            result = await csharpCodeAgent.DoCallWrapper(agentToCallModel, parameters);
                        }
                        else if (agentToCallModel.Type == AgentType.PythonCode)
                        {
                            var pythonCodeAgent = scope.ServiceProvider.GetRequiredService<IPythonCodeAgent>();
                            result = await pythonCodeAgent.DoCallWrapper(agentToCallModel, parameters);
                        }
                        schedulerAgentTaskModel.Result = HttpUtility.HtmlDecode(result);
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Completed;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
                        var login = await _loginProcessor.GetById(schedulerAgentTaskModel.LoginId);
                        if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                        {
                            var parametersString = string.Join(Environment.NewLine, parameters.Select(x => $" - {x.Key}: {x.Value}"));
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
                                }, agentToCallModel.WorkspaceId ?? 0);
                        }
                    }
                    catch (Exception e)
                    {
                        schedulerAgentTaskModel.Result = e.Message;
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Failed;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
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