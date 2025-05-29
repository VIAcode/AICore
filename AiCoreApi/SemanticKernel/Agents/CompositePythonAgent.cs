using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Web;
using AiCoreApi.Data.Processors;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Collections.Concurrent;
using AiCoreApi.Common.Extensions;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositePythonAgent : BaseEnabledAgentsAgent, ICompositePythonAgent
    {
        private static ConcurrentDictionary<string, string> CodeCache = new();
        private string _debugMessageSenderName = "CompositePythonAgent";

        private static class AgentContentParameters
        {
            public const string EnabledAgents = "enabledAgents";
            public const string Prompt = "prompt";
            public const string Temperature = "temperature";
            public const string TopP = "top_p";
        }

        private const string SystemMessage = "You are an expert Python developer. You generate Python agents with a 'run' function.";
        private const int RegenerationAttempts = 3;

        private readonly IPythonCodeAgent _pythonCodeAgent;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        public CompositePythonAgent(
            IAgentsProcessor agentsProcessor,
            MonitoringConfig monitoringConfig,
            IPythonCodeAgent pythonCodeAgent,
            ILogger<CompositePythonAgent> logger,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider
        )
        : base(agentsProcessor, responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _pythonCodeAgent = pythonCodeAgent;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
            _plannerHelpers = plannerHelpers;
            _semanticKernelProvider = semanticKernelProvider;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName, agent.LlmType);
            var temperature = GetTemperature(llmConnection, agent);
            var topP = GetTopP(agent);
            var prompt = ApplyParameters(agent.Content[AgentContentParameters.Prompt].Value, parameters);

            var parameterDescription = agent.Content["parameterDescription"].Value
                .Split(',')
                .Select((p, i) => $@"# Parameter{i + 1}: {p}
parameter{i + 1} = Parameters['parameter{i + 1}']")
                .ToList();
            var parametersDescription = string.Join(Environment.NewLine, parameterDescription);

            string agentsDescription = await GetAgentsDescriptions(agent);

            string promptTemplate = $@"
# Introduction
You are an expert Python developer. Complete the code based on the given task.
- Output only Python code. No explanation or comments.
- The result must be a complete and executable script.
- result must be set to 'result' variable
- Use existing Agents where applicable. Do not re-implement Agent functionality.
- Do not include any pip install lines unless the imported module is directly used in the code.
- Do not include # cmd:pip install package_name for 'openai' or any LLM-related packages unless they are directly imported and used (not just mentioned in prompt).
- No ""def run(Parameters)"", just continue the code from ""Code to finish"".
- No ""return"", just set the output to ""result"" variable

{agentsDescription}

# Code to finish
```python
# include only required imports
import json  

{parametersDescription}

# your code here

# Output: {agent.Content["outputDescription"].Value}
return result
```

# Task Description:
{prompt}".Trim();

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt", promptTemplate);
            var cachekey = promptTemplate.GetHash();
            if (!CodeCache.TryGetValue(cachekey, out var code) && _requestAccessor.UseCachedPlan)
            {
                code = await ExecutePrompt(llmConnection, promptTemplate, temperature, topP);
            }

            for (var i = 0; i < RegenerationAttempts; i++)
            {
                code = TrimCode(code);
                var pythonAgent = new AgentModel
                {
                    Name = $"{agent.Name}-Generated",
                    Type = AgentType.PythonCode,
                    Content = new Dictionary<string, ConfigurableSetting>
                    {
                        { "pythonCode", new ConfigurableSetting { Value = code } }
                    }
                };
                var validationResult = _pythonCodeAgent.Validate(code);
                if (string.IsNullOrEmpty(validationResult))
                {
                    var result = await _pythonCodeAgent.DoCallWrapper(pythonAgent, parameters);
                    CodeCache.TryAdd(cachekey, code);
                    return result;
                }
                var fixErrorPrompt = $@"{code}

You are an expert Python developer. Fix the code based on the Error Text below.
- Output only Python code. No explanation or comments.

{validationResult}
";
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt Fix", fixErrorPrompt);
                code = await ExecutePrompt(llmConnection, fixErrorPrompt, temperature, topP);

            }
            throw new AiCoreUiException("Code generation failed after multiple attempts.");
        }

        private async Task<string> GetAgentsDescriptions(AgentModel agent)
        {
            var enabledAgents = agent.Content[AgentContentParameters.EnabledAgents].Value.JsonGet<Dictionary<string, bool>>();
            if (enabledAgents == null || !enabledAgents.Any())
                return string.Empty;
            var agentsList = await _plannerHelpers.GetAgentsList();
            _plannerHelpers.CompositePythonAgent = this;
            var result = @"
# Agents
Use `ExecuteAgent('AgentName', ['param1', 'param2'])` to call.
";
            foreach (var agentItem in agentsList)
            {
                var agentId = agentItem.AgentId.ToString();
                if (enabledAgents.ContainsKey(agentId) && enabledAgents[agentId])
                {
                    result += $@"
## AgentName: {agentItem.Name}
- Description: {agentItem.Description}
- Parameters: {agentItem.Content.GetValueOrDefault("parameterDescription")?.Value ?? ""}
- Output: {agentItem.Content["outputDescription"].Value}
";
                }
            }
            return result;
        }

        private double GetTemperature(ConnectionModel llmConnection, AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.Temperature, out var tempSetting)
                && double.TryParse(tempSetting.Value, out var agentTemperature))
            {
                return agentTemperature;
            }
            return llmConnection.Content.TryGetValue("temperature", out var val) ? Convert.ToDouble(val) : 0;
        }

        private double GetTopP(AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.TopP, out var topPSetting)
                && double.TryParse(topPSetting.Value, out var agentTopP))
            {
                return agentTopP;
            }
            return 0;
        }

        private string TrimCode(string code)
        {
            var startIndex = code.IndexOf("```", StringComparison.Ordinal);
            if (startIndex == -1) return code.Trim();
            startIndex = code.IndexOf('\n', startIndex);
            if (startIndex == -1) return code.Trim();
            startIndex += 1;
            var endIndex = code.IndexOf("```", startIndex, StringComparison.Ordinal);
            return endIndex == -1 ? code.Trim() : code.Substring(startIndex, endIndex - startIndex).Trim();
        }

        private async Task<string> ExecutePrompt(ConnectionModel llmConnection, string templateText, double temperature, double topP)
        {
            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            history.AddSystemMessage(SystemMessage);
            history.AddUserMessage(new ChatMessageContentItemCollection { new TextContent(templateText) });
            var result = await chat.GetChatMessageContentAsync(history, new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
                TopP = topP,
            });
            return result.Content ?? string.Empty;
        }
    }

    public interface ICompositePythonAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
