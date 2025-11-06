using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Planning.Handlebars;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeAgent : BaseEnabledAgentsAgent, ICompositeAgent
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
        private readonly IAgentRegistry _agentRegistry;
        private readonly ILogger<CompositeAgent> _logger;

        public CompositeAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider,
            IAgentRegistry agentRegistry,
            ILogger<CompositeAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _extendedConfig = extendedConfig;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
            _plannerHelpers = plannerHelpers;
            _semanticKernelProvider = semanticKernelProvider;
            _agentRegistry = agentRegistry;
            _logger = logger;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters) =>
            await base.DoCallWrapper(agent, parameters);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = await GetConnectionAsync(
                _requestAccessor,
                _responseAccessor,
                connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm },
                _debugMessageSenderName,
                agent.LlmType);

            var kernel = _semanticKernelProvider.GetKernel(llmConnection);

            var agents = agent.Content[AgentContentParameters.AgentsList].Value.JsonGet<Dictionary<string, bool>>();
            var plan = agent.Content.TryGetValue(AgentContentParameters.ExecutionPlan, out var execPlan)
                ? execPlan.Value
                : string.Empty;
            var plannerPrompt = agent.Content.TryGetValue(AgentContentParameters.PlannerPrompt, out var plannerPromptValue)
                ? plannerPromptValue.Value
                : string.Empty;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request",
                $"{agent.Name}:\n\n{parameters.ToJson()}\n\n{plan}\n\n{plannerPrompt}");

            plannerPrompt = await AddPlugins(kernel, plannerPrompt, agents);

            if (string.IsNullOrWhiteSpace(plan))
            {
                plannerPrompt = await ApplyParametersAsync(plannerPrompt);
                plan = await GetPlan(kernel, agent, plannerPrompt);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Generated Plan", plan);
            }
            else
            {
                plan = await ApplyParametersAsync(plan);
            }
            try
            {
                var result = await new HandlebarsPlan(plan).InvokeAsync(kernel);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);

                if (string.IsNullOrEmpty(_responseAccessor.CurrentMessage.Text))
                {
                    _responseAccessor.CurrentMessage.Text =
                        string.IsNullOrEmpty(result) || result == "null"
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
                _responseAccessor.AddDebugMessage(
                    _debugMessageSenderName,
                    "Planner Execution Error",
                    $"{ex.Message} {ex.InnerException?.Message}");
                _responseAccessor.CurrentMessage.Text = _extendedConfig.NoInformationFoundText;
            }

            return _responseAccessor.CurrentMessage.Text;
        }

        private async Task<string> AddPlugins(Kernel kernel, string plannerPrompt, Dictionary<string, bool> enabledAgents)
        {
            var pluginsInstructions = new List<string>();
            var agentsList = await _plannerHelpers.GetAgentsList();

            foreach (var agent in agentsList)
            {
                var agentId = agent.AgentId.ToString();
                if (enabledAgents.TryGetValue(agentId, out var isEnabled) && isEnabled)
                {
                    var agentInstance = _agentRegistry.Resolve(agent.Type);
                    await agentInstance.AddAgent(agent, kernel, pluginsInstructions);
                }
            }

            return plannerPrompt.Replace(
                PlannerHelpers.PlannerPromptPlaceholders.PluginsInstructionsPlaceholder,
                string.Join(" ", pluginsInstructions));
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

    public interface ICompositeAgent : IDoCallWrapperAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
