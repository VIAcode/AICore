using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Microsoft.SemanticKernel.Planning.Handlebars;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeAgent: BaseAgent, ICompositeAgent
    {
        private string _debugMessageSenderName = "CompositeAgent";
        public static class AgentContentParameters
        {
            public const string AgentsList = "agentsList";
            public const string ExecutionPlan = "executionPlan";
            public const string PlannerPrompt = "plannerPrompt";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        public CompositeAgent(
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            IPlannerHelpers plannerHelpers,
            ILogger<CompositeAgent> logger,
            ISemanticKernelProvider semanticKernelProvider) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _extendedConfig = extendedConfig;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
            _plannerHelpers = plannerHelpers;
            _semanticKernelProvider = semanticKernelProvider;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters) => await base.DoCallWrapper(agent, parameters);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections, 
                new [] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName, agent.LlmType);
            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var agents = agent.Content[AgentContentParameters.AgentsList].Value.JsonGet<Dictionary<string, bool>>();
            var plan = agent.Content.ContainsKey(AgentContentParameters.ExecutionPlan) 
                ? agent.Content[AgentContentParameters.ExecutionPlan].Value 
                : string.Empty;
            var plannerPrompt = agent.Content.ContainsKey(AgentContentParameters.PlannerPrompt)
                ? agent.Content[AgentContentParameters.PlannerPrompt].Value
                : string.Empty;
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"{agent.Name}:\n\n{parameters.ToJson()}\n\n{plan}\n\n{plannerPrompt}");
            plannerPrompt = await AddPlugins(kernel, plannerPrompt, agents);
            if (string.IsNullOrWhiteSpace(plan))
            {
                plannerPrompt = ApplyParameters(plannerPrompt, parameters);
                plan = await GetPlan(kernel, agent, plannerPrompt);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Generated Plan", $"{plan}");
            }
            else
            {
                plan = ApplyParameters(plan, parameters);
            }
            try
            {
                var result = await new HandlebarsPlan(plan).InvokeAsync(kernel);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
                if (string.IsNullOrEmpty(_responseAccessor.CurrentMessage.Text))
                {
                    _responseAccessor.CurrentMessage.Text = string.IsNullOrEmpty(result) || result == "null"
                        ? _extendedConfig.NoInformationFoundText
                        : result;
                }
            }
            catch (TokensLimitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Planner Execution Error", $"{ex.Message} {ex.InnerException?.Message}");
                _responseAccessor.CurrentMessage.Text = _extendedConfig.NoInformationFoundText;
            }
            return _responseAccessor.CurrentMessage.Text;
        }

        private async Task<string> AddPlugins(Kernel kernel, string plannerPrompt, Dictionary<string, bool> agents)
        {
            var pluginsInstructions = new List<string>();
            var agentsList = await _plannerHelpers.GetAgentsList();
            _plannerHelpers.CompositeAgent = this;
            foreach (var agent in agentsList)
            {
                var agentId = agent.AgentId.ToString();
                if (agents.ContainsKey(agentId) && agents[agentId])
                {
                    await _plannerHelpers.AddPlugin(agent, kernel, pluginsInstructions);
                }
            }
            return plannerPrompt.Replace(PlannerHelpers.PlannerPromptPlaceholders.PluginsInstructionsPlaceholder, string.Join(" ", pluginsInstructions));
        }

        private async Task<string> GetPlan(Kernel kernel, AgentModel agent, string plannerPrompt)
        {
            var planner = new HandlebarsPlanner(new HandlebarsPlannerOptions
            {
                AllowLoops = true,
                ExecutionSettings = new OpenAIPromptExecutionSettings
                {
                    Temperature = 0.0,
                    TopP = 0.0,
                },
            });
            var plan = (await planner.CreatePlanAsync(kernel, plannerPrompt)).ToString();
            return plan;
        }
    }

    public interface ICompositeAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
