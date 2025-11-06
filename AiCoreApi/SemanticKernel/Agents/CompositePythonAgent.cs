using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using System.Collections.Concurrent;
using AiCoreApi.Common.Extensions;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

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
            public const string CodeGenerationPrompt = "codeGenerationPrompt";
            public const string Temperature = "temperature";
            public const string TopP = "top_p";
        }

        private const string SystemMessage = "You are an expert Python developer.";
        private const string CodeGenerationPromptText = $@"# Introduction
You are an expert Python developer. Complete the code based on the given task.
- Output only Python code. No explanation or comments.
- The result must be a complete and executable script from ""Code to finish"" section, not just your part.
- result must be set to 'result' variable
- Use existing Agents where applicable. Do not re-implement Agent functionality.
- Do not include any pip install lines unless the imported module is directly used in the code.
- Do not include # cmd:pip install package_name for 'openai' or any LLM-related packages unless they are directly imported and used (not just mentioned in prompt).
- No ""def run(Parameters)"", just continue the code from ""Code to finish"".
- No ""return"", just set the output to ""result"" variable (string)

{{{{agentsDescription}}}}

# Code to finish
\`\`\`python
# include only required imports
import json  

{{{{parametersDescription}}}}

# your code here

# Output: {{{{outputDescription}}}}
result = ..expected_output..
\`\`\`

# Task Description:
{{{{taskDescription}}}}";

        private static class CodeGenerationPromptPlaceHolders
        {
            public const string AgentsDescription = "{{agentsDescription}}";
            public const string ParametersDescription = "{{parametersDescription}}";
            public const string OutputDescription = "{{outputDescription}}";
            public const string TaskDescription = "{{taskDescription}}";
        }

        private const int RegenerationAttempts = 3;

        private readonly IPythonCodeAgent _pythonCodeAgent;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        public CompositePythonAgent(
            IBaseAgentHelper baseAgentHelper,
            IPythonCodeAgent pythonCodeAgent,
            ILogger<CompositePythonAgent> logger,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider
        )
        : base(baseAgentHelper, logger)
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
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.DeepSeekLlm, ConnectionType.GeminiLlm }, _debugMessageSenderName, agent.LlmType);
            var temperature = GetTemperature(llmConnection, agent);
            var topP = GetTopP(agent);
            var prompt = await GetParameterValueAsync(AgentContentParameters.Prompt);

            var parameterDescription = agent.Content["parameterDescription"].Value
                .Split(',')
                .Select((p, i) => $@"# Parameter{i + 1}: {p} (string)
parameter{i + 1} = Parameters['parameter{i + 1}']")
                .ToList();
            var parametersDescription = string.Join(Environment.NewLine, parameterDescription);

            string agentsDescription = await GetAgentsDescriptions(agent);

            string promptTemplate = await GetParameterValueAsync(AgentContentParameters.CodeGenerationPrompt);
            if (string.IsNullOrEmpty(promptTemplate))
                promptTemplate = CodeGenerationPromptText;
            promptTemplate = promptTemplate
                .Replace(CodeGenerationPromptPlaceHolders.AgentsDescription, agentsDescription)
                .Replace(CodeGenerationPromptPlaceHolders.ParametersDescription, parametersDescription)
                .Replace(CodeGenerationPromptPlaceHolders.OutputDescription, agent.Content["outputDescription"].Value)
                .Replace(CodeGenerationPromptPlaceHolders.TaskDescription, prompt)
                .Trim();

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt", promptTemplate);
            var cachekey = promptTemplate.GetHash();
            if (!CodeCache.TryGetValue(cachekey, out var code) && _requestAccessor.UseCachedPlan)
            {
                code = await _semanticKernelProvider.ExecutePrompt(llmConnection, promptTemplate, temperature, topP, SystemMessage);
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
                code = await _semanticKernelProvider.ExecutePrompt(llmConnection, fixErrorPrompt, temperature, topP, SystemMessage);

            }
            throw new AiCoreUiException("Code generation failed after multiple attempts.");
        }

        private async Task<string> GetAgentsDescriptions(AgentModel agent)
        {
            var enabledAgents = agent.Content[AgentContentParameters.EnabledAgents].Value.JsonGet<Dictionary<string, bool>>();
            if (enabledAgents == null || !enabledAgents.Any())
                return string.Empty;
            var agentsList = await _plannerHelpers.GetAgentsList();
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
            return llmConnection.Content.TryGetValue(AgentContentParameters.Temperature, out var val) ? Convert.ToDouble(val) : 0;
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
    }

    public interface ICompositePythonAgent: IDoCallWrapperAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
