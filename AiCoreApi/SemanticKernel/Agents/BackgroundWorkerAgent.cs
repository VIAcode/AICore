using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using System.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class BackgroundWorkerAgent : BaseAgent, IBackgroundWorkerAgent
    {
        private string _debugMessageSenderName = "BackgroundWorkerAgent";
        public static class AgentContentParameters
        {
            public const string CompositeAgentName = "compositeAgentName";
            public const string LifeTimeMinutes = "lifeTimeMinutes";
        }

        private readonly ISchedulerAgentTaskProcessor _schedulerAgentTaskProcessor;
        private readonly UserContextAccessor _userContextAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;

        public BackgroundWorkerAgent(
            ISchedulerAgentTaskProcessor schedulerAgentTaskProcessor,
            UserContextAccessor userContextAccessor,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            ExtendedConfig extendedConfig,
            ILogger<BackgroundWorkerAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _schedulerAgentTaskProcessor = schedulerAgentTaskProcessor;
            _userContextAccessor = userContextAccessor;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            // Handle Return value
            if (parameters.Count > 0)
            {
                var parameter1 = parameters["parameter1"].ToLower();
                if (Guid.TryParse(parameter1, out _))
                {
                    var schedulerAgentTaskModel = await _schedulerAgentTaskProcessor.GetByGuid(parameter1);
                    if (schedulerAgentTaskModel != null)
                    {
                        var result = string.Empty;
                        if (schedulerAgentTaskModel.SchedulerAgentTaskState == SchedulerAgentTaskState.New)
                            result = "[Not Started]";
                        else if (schedulerAgentTaskModel.SchedulerAgentTaskState == SchedulerAgentTaskState.InProgress)
                            result = "[In progress]";
                        else if (schedulerAgentTaskModel.SchedulerAgentTaskState == SchedulerAgentTaskState.Failed)
                            result = "[Failed]";
                        else if(schedulerAgentTaskModel.SchedulerAgentTaskState == SchedulerAgentTaskState.Completed)
                            result = schedulerAgentTaskModel.Result;
                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall", $"Task found ({parameter1}):\r\n{result}");
                        return result;
                    }
                }
            }

            var compositeAgentName = agent.Content[AgentContentParameters.CompositeAgentName].Value;
            var lifeTimeMinutes = agent.Content.ContainsKey(AgentContentParameters.LifeTimeMinutes) ? int.Parse(agent.Content[AgentContentParameters.LifeTimeMinutes].Value) : 60;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request",
                $"Parameters: {string.Join(", ", parameters.Select(item => $"{item.Key}: {item.Value}"))}\r\nCompositeAgent:\r\n{compositeAgentName}");
            var schedulerAgentTaskGuid = Guid.NewGuid().ToString().ToLower();

            await _schedulerAgentTaskProcessor.Add(new SchedulerAgentTaskModel
            {
                SchedulerAgentTaskGuid = schedulerAgentTaskGuid,
                CompositeAgentName = compositeAgentName,
                Parameters = parameters.ToJson()!,
                RequestAccessor = _requestAccessor.ToJson()!,
                LoginId = await _userContextAccessor.GetLoginIdAsync() ?? 0,
                ValidTill = DateTime.UtcNow.AddMinutes(lifeTimeMinutes),
                CreatedAt = DateTime.UtcNow,
                SchedulerAgentTaskState = SchedulerAgentTaskState.New
            });

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", schedulerAgentTaskGuid);
            return schedulerAgentTaskGuid;
        }
    }

    public interface IBackgroundWorkerAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
