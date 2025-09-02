using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using NCrontab;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class SchedulerAgentService : AgentServiceBase, ISchedulerAgentService
    {
        private readonly IAgentsProcessor _agentsProcessor; 

        public SchedulerAgentService(
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor, 
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceScopeFactory scopeFactory)
            :base(loginProcessor, debugLogProcessor, extendedConfig, scopeFactory)
        {
            _agentsProcessor = agentsProcessor;
        }

        public async Task ProcessTask(List<AgentModel> agents)
        {
            var schedulerAgents = agents.Where(agent => agent.Type == AgentType.Scheduler && agent.IsEnabled).ToList();
            foreach (var agent in schedulerAgents)
            {
                var cronScheduleStartTime = agent.Content["schedule"].Value;
                var runAs = Convert.ToInt32(agent.Content["runAs"].Value);
                var agentToCallName = agent.Content["compositeAgentName"].Value;
                var parametersValues = (agent.Content.ContainsKey("parametersValues") ? agent.Content["parametersValues"].Value : "")
                    .Split(',')
                    .Select((value, index) => new { Key = "parameter" + (index + 1), Value = value })
                    .ToDictionary(item => item.Key, item => item.Value);
                var lastRun = DateTime.TryParse(agent.Content.ContainsKey("lastRun") ? agent.Content["lastRun"].Value : "", out var lastRunDateTime);
                if(!lastRun)
                    lastRunDateTime = DateTime.MinValue;

                var cronExpression = CrontabSchedule.Parse(cronScheduleStartTime);
                var nextRunTime = cronExpression.GetNextOccurrence(lastRunDateTime);
                if (DateTime.UtcNow >= nextRunTime)
                {
                    var availableAgents = await _agentsProcessor.List(agent.WorkspaceId);
                    await RunAgent("Scheduler", availableAgents, agent, agentToCallName, runAs, parametersValues);
                    await _agentsProcessor.Update(agent);
                }
            }
        }
    }

    public interface ISchedulerAgentService
    {
        public Task ProcessTask(List<AgentModel> agents);
    }
}