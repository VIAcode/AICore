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
        private readonly IServiceScopeFactory _scopeFactory;

        public BackgroundWorkerAgentService(
            ISchedulerAgentTaskProcessor schedulerAgentTaskProcessor,
            IAgentsProcessor agentsProcessor,
            IDebugLogProcessor debugLogProcessor,
            ILoginProcessor loginProcessor,
            ExtendedConfig extendedConfig,
            IServiceScopeFactory scopeFactory)
        {
            _schedulerAgentTaskProcessor = schedulerAgentTaskProcessor;
            _agentsProcessor = agentsProcessor;
            _debugLogProcessor = debugLogProcessor;
            _loginProcessor = loginProcessor;
            _extendedConfig = extendedConfig;
            _scopeFactory = scopeFactory;
        }

        public async Task ProcessTask()
        {
            var schedulerAgentTaskModel = await _schedulerAgentTaskProcessor.GetNext();
            while (schedulerAgentTaskModel != null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var sp = scope.ServiceProvider;

                var requestAccessor = sp.GetRequiredService<RequestAccessor>();
                var responseAccessor = sp.GetRequiredService<ResponseAccessor>();
                var userContextAccessor = sp.GetRequiredService<UserContextAccessor>();

                var workspaceId = 0;
                var result = string.Empty;
                var parametersValues = new Dictionary<string, string>();

                try
                {
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

                    var agentToCall = await _agentsProcessor.GetByName(
                        schedulerAgentTaskModel.CompositeAgentName,
                        requestAccessor.WorkspaceId);

                    if (agentToCall == null)
                    {
                        schedulerAgentTaskModel.Result = "Agent to call not found";
                        schedulerAgentTaskModel.SchedulerAgentTaskState = SchedulerAgentTaskState.Failed;
                        await _schedulerAgentTaskProcessor.Update(schedulerAgentTaskModel);
                        return;
                    }

                    parametersValues = schedulerAgentTaskModel.Parameters
                        .JsonGet<Dictionary<string, string>>() ?? new Dictionary<string, string>();

                    workspaceId = agentToCall.WorkspaceId ?? 0;

                    result = agentToCall.Type switch
                    {
                        AgentType.Composite => await sp.GetRequiredService<ICompositeAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.CsharpCode => await sp.GetRequiredService<ICsharpCodeAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.PythonCode => await sp.GetRequiredService<IPythonCodeAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.NodeJsCode => await sp.GetRequiredService<INodeJsCodeAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.CompositeCSharp => await sp.GetRequiredService<ICompositeCSharpAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.CompositePython => await sp.GetRequiredService<ICompositePythonAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.CompositeLoop => await sp.GetRequiredService<ICompositeLoopAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.CompositeLoopV2 => await sp.GetRequiredService<ICompositeLoopV2Agent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        AgentType.Flow => await sp.GetRequiredService<IFlowAgent>()
                            .DoCallWrapper(agentToCall, parametersValues),
                        _ => throw new NotSupportedException($"Unsupported agent type: {agentToCall.Type}")
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
                        var parametersString = string.Join(Environment.NewLine,
                            parametersValues.Select(x => $" - {x.Key}: {x.Value}"));

                        await _debugLogProcessor.Add(
                            login?.Login ?? "",
                            $"Agent (Background): {schedulerAgentTaskModel.CompositeAgentName}{Environment.NewLine}" +
                            $"Parameters:{Environment.NewLine}{parametersString}",
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
                            },
                            workspaceId);
                    }
                }

                schedulerAgentTaskModel = await _schedulerAgentTaskProcessor.GetNext();
            }

            await _schedulerAgentTaskProcessor.RemoveExpired();
        }
    }

    public interface IBackgroundWorkerAgentService
    {
        Task ProcessTask();
    }
}
