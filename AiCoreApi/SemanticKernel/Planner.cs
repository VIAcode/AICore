using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Planning.Handlebars;
using AiCoreApi.Common;
using AiCoreApi.Models.ViewModels;
using Microsoft.Extensions.Caching.Distributed;
using AiCoreApi.Data.Processors;
using AiCoreApi.SemanticKernel.Agents;
using Microsoft.SemanticKernel;

namespace AiCoreApi.SemanticKernel
{
    public class Planner : IPlanner
    {
        private const string DebugMessageSenderName = "Planner";

        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IDistributedCache _distributedCache;
        private readonly ILoginProcessor _loginProcessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly IPlannerCallOptions _plannerCallOptions;
        private readonly ILogger<Planner> _logger;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ICompositeAgent _compositeAgent;
        private readonly ICsharpCodeAgent _csharpCodeAgent;
        private readonly IPythonCodeAgent _pythonCodeAgent; 

        public Planner(
            ISemanticKernelProvider semanticKernelProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IDistributedCache distributedCache,
            ILoginProcessor loginProcessor,
            ExtendedConfig extendedConfig,
            IPlannerCallOptions plannerCallOptions,
            ILogger<Planner> logger,
            IPlannerHelpers plannerHelpers,
            ICompositeAgent compositeAgent,
            ICsharpCodeAgent csharpCodeAgent,
            IPythonCodeAgent pythonCodeAgent)
        {
            _semanticKernelProvider = semanticKernelProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _distributedCache = distributedCache;
            _loginProcessor = loginProcessor;
            _extendedConfig = extendedConfig;
            _plannerCallOptions = plannerCallOptions;
            _logger = logger;
            _plannerHelpers = plannerHelpers;
            _compositeAgent = compositeAgent;
            _csharpCodeAgent = csharpCodeAgent;
            _pythonCodeAgent = pythonCodeAgent;
        }
        
        public async Task<MessageDialogViewModel.Message> GetChatResponse()
        {
            var agentsList = await _plannerHelpers.GetAgentsList();
            var agentCallResponse = await GetAgentCallResponse();
            if (agentCallResponse != null)
                return agentCallResponse;
            
            var applyCallOptionsResult = await _plannerCallOptions.Apply(agentsList);
            if (!applyCallOptionsResult.Success)
            {
                _responseAccessor.CurrentMessage.Text = applyCallOptionsResult.ErrorMessage ?? _extendedConfig.NoInformationFoundText;
                return _responseAccessor.CurrentMessage;
            }
            var kernel = await _semanticKernelProvider.GetKernel();
            var useAllPlugins = !string.IsNullOrEmpty(applyCallOptionsResult.Plan);
            var plan = applyCallOptionsResult.Plan;
            var plannerPrompt = await AddPlugins(_extendedConfig.PlannerPrompt, useAllPlugins, kernel);
            if (string.IsNullOrEmpty(plan))
            {
                if (agentsList.Count(agent => agent.IsEnabled) == 1)
                {
                    var agent = agentsList.First(agent => agent.IsEnabled);
                    _responseAccessor.CurrentMessage.Text = await _plannerHelpers.ExecuteAgent(agent.Name, new List<string> { _requestAccessor.MessageDialog?.Messages?.Last().Text ?? "" });
                    return _responseAccessor.CurrentMessage;
                }
                plannerPrompt = _plannerHelpers.ApplyPlaceholders(plannerPrompt);
                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Planner Prompt", plannerPrompt);
                plan = await GetPlan(plannerPrompt, kernel);
            }
            try
            {
                var result = await new HandlebarsPlan(plan).InvokeAsync(kernel);
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
                _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Planner Execution Error", ex.Message);
                _responseAccessor.CurrentMessage.Text = _extendedConfig.NoInformationFoundText;
                _logger.LogError(ex, "Error in planner");
                // probably plan is not valid, so we need to remove it from cache
                _distributedCache.Remove(_plannerHelpers.GetPlannerCacheKey(plannerPrompt, kernel));
            }
            return _responseAccessor.CurrentMessage;
        }

        private async Task<MessageDialogViewModel.Message?> GetAgentCallResponse()
        {
            var currentMessage = _requestAccessor.MessageDialog?.Messages?.Last();
            if (currentMessage?.Options != null && currentMessage.Options.Length > 0 && currentMessage.Options[0].Type == MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall)
            {
                try
                {
                    var agentName = currentMessage.Options[0].Name;
                    var parameters = currentMessage.Options[0].Parameters.Select(item => item.Value).ToList();
                    _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution", $"Agent {agentName}, Parameters: {string.Join(",", parameters)}");
                    var result = await _plannerHelpers.ExecuteAgent(agentName, parameters);
                    if (!string.IsNullOrEmpty(result))
                    {
                        _responseAccessor.CurrentMessage.Text = result;
                    }
                    else if (string.IsNullOrEmpty(_responseAccessor.CurrentMessage.Text))
                    {
                        _responseAccessor.CurrentMessage.Text = _extendedConfig.NoInformationFoundText;
                    }
                    _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution Result", _responseAccessor.CurrentMessage.Text);
                    return _responseAccessor.CurrentMessage;
                }
                catch (Exception ex)
                {
                    _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Agent Execution Error", ex.Message);
                    _responseAccessor.CurrentMessage.Text = _extendedConfig.NoInformationFoundText;
                    _logger.LogError(ex, "Error in agent execution");
                }
                return _responseAccessor.CurrentMessage;
            }
            return null;
        }

        private async Task<string> AddPlugins(string plannerPrompt, bool useAllPlugins, Kernel kernel)
        {
            var pluginsInstructions = new List<string>();
            var agentsList = await _plannerHelpers.GetAgentsList();
            _plannerHelpers.CompositeAgent = _compositeAgent;
            _plannerHelpers.CsharpCodeAgent = _csharpCodeAgent;
            _plannerHelpers.PythonCodeAgent = _pythonCodeAgent;
            var allUserTags = await _loginProcessor.GetTagsByLogin(_requestAccessor.Login, _requestAccessor.LoginType);
            foreach (var agent in agentsList)
            {
                if (useAllPlugins ||
                    (agent.IsEnabled && (agent.Tags.Count == 0 || agent.Tags.Select(x => x.TagId).Any(allUserTags.Select(x => x.TagId).Contains))))
                {
                    await _plannerHelpers.AddPlugin(agent, kernel, pluginsInstructions);
                }
            }
            return plannerPrompt.Replace( PlannerHelpers.PlannerPromptPlaceholders.PluginsInstructionsPlaceholder, string.Join(" ", pluginsInstructions));
        }

        private async Task<string> GetPlan(string plannerPrompt, Kernel kernel)
        {
            // Cache plan for 1 hour. Plan should be cached per prompt and per enabled plugins.
            var cacheKey = _plannerHelpers.GetPlannerCacheKey(plannerPrompt, kernel);
            if (_requestAccessor.UseCachedPlan)
            {
                var cachedContent = await _distributedCache.GetStringAsync(cacheKey);
                if (cachedContent != null)
                {
                    _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Execution Plan (cached)", cachedContent);
                    return cachedContent;
                }
            }

            var planner = new HandlebarsPlanner(new HandlebarsPlannerOptions
            {
                AllowLoops = true,
                ExecutionSettings = new OpenAIPromptExecutionSettings
                {
                    Temperature = 0.0,
                    TopP = 0.0,
                }
            });
            var plan = (await planner.CreatePlanAsync(kernel, plannerPrompt)).ToString();
            await _distributedCache.SetStringAsync(cacheKey, plan, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(60) });
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "Execution Plan", plan);
            return plan;
        }
    }

    public interface IPlanner
    {
        Task<MessageDialogViewModel.Message> GetChatResponse();
    }
}
