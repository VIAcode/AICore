using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Planning.Handlebars;
using Microsoft.Extensions.Caching.Distributed;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.ViewModels;

namespace AiCoreApi.SemanticKernel
{
    public class Planner : IPlanner
    {
        private const string DebugMessageSenderName = "Planner";

        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IDistributedCache _cache;
        private readonly ILoginProcessor _loginProcessor;
        private readonly ExtendedConfig _config;
        private readonly IPlannerCallOptions _plannerCallOptions;
        private readonly ILogger<Planner> _logger;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly IAgentExecutor _agentExecutor;
        private readonly IAgentRegistry _registry;

        public Planner(
            ISemanticKernelProvider semanticKernelProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IDistributedCache cache,
            ILoginProcessor loginProcessor,
            ExtendedConfig config,
            IPlannerCallOptions plannerCallOptions,
            ILogger<Planner> logger,
            IPlannerHelpers plannerHelpers,
            IAgentExecutor agentExecutor,
            IAgentRegistry registry)
        {
            _semanticKernelProvider = semanticKernelProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _cache = cache;
            _loginProcessor = loginProcessor;
            _config = config;
            _plannerCallOptions = plannerCallOptions;
            _logger = logger;
            _plannerHelpers = plannerHelpers;
            _agentExecutor = agentExecutor;
            _registry = registry;
        }

        public async Task<MessageDialogViewModel.Message> GetChatResponse()
        {
            var agentsList = await _plannerHelpers.GetAgentsList();
            var agentCallResponse = await TryExecuteAgentCall();

            if (agentCallResponse != null)
                return agentCallResponse;

            var applyResult = await _plannerCallOptions.Apply(agentsList);
            if (!applyResult.Success)
            {
                _responseAccessor.CurrentMessage.Text =
                    applyResult.ErrorMessage ?? _config.NoInformationFoundText;
                return _responseAccessor.CurrentMessage;
            }

            var kernel = await _semanticKernelProvider.GetKernel();
            var useAllPlugins = !string.IsNullOrEmpty(applyResult.Plan);
            var plannerPrompt = await AddPlugins(_config.PlannerPrompt, useAllPlugins, kernel);
            var plan = applyResult.Plan;

            if (string.IsNullOrEmpty(plan))
            {
                // Shortcut: if only one enabled agent (no flow)
                var enabledNoFlowAgents = agentsList.Where(a => a.IsEnabled && string.IsNullOrEmpty(a.FlowName)).ToList();
                if (enabledNoFlowAgents.Count == 1)
                {
                    var singleAgent = enabledNoFlowAgents[0];
                    _responseAccessor.CurrentMessage.Text =
                        await _agentExecutor.ExecuteAsync(singleAgent.Name,
                            new List<string> { _requestAccessor.MessageDialog?.Messages?.Last().Text ?? "" },
                            checkAgentCallType: true);
                    return _responseAccessor.CurrentMessage;
                }

                plannerPrompt = _plannerHelpers.ApplyPlaceholders(plannerPrompt);
                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Planner Prompt", plannerPrompt);
                plan = await GetOrCreatePlan(plannerPrompt, kernel);
            }

            try
            {
                var result = await new HandlebarsPlan(plan).InvokeAsync(kernel);
                _responseAccessor.CurrentMessage.Text = string.IsNullOrWhiteSpace(result) || result == "null"
                    ? _config.NoInformationFoundText
                    : result;
            }
            catch (TokensLimitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Planner Execution Error", ex.Message);
                _responseAccessor.CurrentMessage.Text = _config.NoInformationFoundText;
                _logger.LogError(ex, "Planner execution failed. Removing cached plan...");
                await _cache.RemoveAsync(_plannerHelpers.GetPlannerCacheKey(plannerPrompt, kernel));
            }

            return _responseAccessor.CurrentMessage;
        }

        private async Task<MessageDialogViewModel.Message?> TryExecuteAgentCall()
        {
            var currentMessage = _requestAccessor.MessageDialog?.Messages?.LastOrDefault();
            var option = currentMessage?.Options?.FirstOrDefault();

            if (option == null || option.Type != MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall)
                return null;

            try
            {
                var agentName = option.Name;
                var parameters = option.Parameters.Select(p => p.Value).ToList();

                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution",
                    $"Agent {agentName}, Parameters: {string.Join(",", parameters)}");

                var result = await _agentExecutor.ExecuteAsync(agentName, parameters, checkAgentCallType: true);

                _responseAccessor.CurrentMessage.Text =
                    string.IsNullOrWhiteSpace(result)
                        ? _config.NoInformationFoundText
                        : result;

                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution Result",
                    _responseAccessor.CurrentMessage.Text);
            }
            catch (TokensLimitException) { throw; }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution Error", ex.Message);
                _responseAccessor.CurrentMessage.Text = _config.NoInformationFoundText;
                _logger.LogError(ex, "Error executing agent.");
            }

            return _responseAccessor.CurrentMessage;
        }

        private async Task<string> AddPlugins(string plannerPrompt, bool useAllPlugins, Kernel kernel)
        {
            var pluginsInstructions = new List<string>();
            var agents = await _plannerHelpers.GetAgentsList();
            var allUserTags = await _loginProcessor.GetTagsByLogin(_requestAccessor.Login, _requestAccessor.LoginType);

            foreach (var agent in agents)
            {
                if (!agent.IsEnabled)
                    continue;

                var accessible = useAllPlugins ||
                                 agent.Tags.Count == 0 ||
                                 agent.Tags.Select(t => t.TagId)
                                     .Any(allUserTags.Select(t => t.TagId).Contains);

                if (!accessible)
                    continue;

                var agentInstance = _registry.Resolve(agent.Type);
                await agentInstance.AddAgent(agent, kernel, pluginsInstructions);
            }

            return plannerPrompt.Replace(
                PlannerHelpers.PlannerPromptPlaceholders.PluginsInstructionsPlaceholder,
                string.Join(" ", pluginsInstructions));
        }

        private async Task<string> GetOrCreatePlan(string plannerPrompt, Kernel kernel)
        {
            var cacheKey = _plannerHelpers.GetPlannerCacheKey(plannerPrompt, kernel);

            if (_requestAccessor.UseCachedPlan)
            {
                var cached = await _cache.GetStringAsync(cacheKey);
                if (cached != null)
                {
                    _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Execution Plan (cached)", cached);
                    return cached;
                }
            }

            var planner = new HandlebarsPlanner(new HandlebarsPlannerOptions
            {
                AllowLoops = true,
                ExecutionSettings = new OpenAIPromptExecutionSettings
                {
                    Temperature = 0.0,
                    TopP = 0.0
                }
            });

            var plan = (await planner.CreatePlanAsync(kernel, plannerPrompt)).ToString();
            await _cache.SetStringAsync(cacheKey, plan, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
            });

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Execution Plan", plan);
            return plan;
        }
    }

    public interface IPlanner
    {
        Task<MessageDialogViewModel.Message> GetChatResponse();
    }
}
