using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using System.Collections.Concurrent;
using AiCoreApi.Common.Extensions;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeCSharpAgent : BaseEnabledAgentsAgent, ICompositeCSharpAgent
    {
        private readonly ICsharpCodeAgent _csharpCodeAgent;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        private static readonly ConcurrentDictionary<string, string> CodeCache = new();
        private string _debugMessageSenderName = "CompositeCSharpAgent";

        private const string SystemMessage = "You are an expert C# developer.";
        private const int RegenerationAttempts = 3;

        private static class AgentContentParameters
        {
            public const string EnabledAgents = "enabledAgents";
            public const string Prompt = "prompt";
            public const string CodeGenerationPrompt = "codeGenerationPrompt";
            public const string Temperature = "temperature";
            public const string TopP = "top_p";
        }

        private const string CodeGenerationPromptTemplate = @$"
# Introduction
You are an expert C# developer. Complete the code based on the given task.
- Output only C# code. No explanation or comments.
- The result must be a complete and executable script from ""Code to finish"" section, not just your part.
- Do not change the class name or method signature.
- Ensure the code is fully compilable and free of undefined variables.
- Use existing Agents where applicable. Do not re-implement Agent functionality.
- Import only required NuGet packages in the format: #r ""nuget: PackageName, Version""
- Do NOT import AiCoreApi.Common — it is already available.

{{{{agentsDescription}}}}
# Code to finish
#r ""nuget: SamplePackage, 1.0.0""
using System;
using System.Collections.Generic;
using System.Linq;

class Agent
{{
    public string Run(
        Dictionary<string, string> Parameters,
        AiCoreApi.Common.RequestAccessor RequestAccessor,
        AiCoreApi.Common.ResponseAccessor ResponseAccessor,
        Func<string, List<string>?, string> ExecuteAgent,
        Func<string, string> GetCacheValue,
        Func<string, string, int, string> SetCacheValue)
    {{
{{{{parametersDescription}}}}

        //your code here

        // Output: {{{{outputDescription}}}}
        return result;
    }}
}}

# Task Description:
{{{{taskDescription}}}}";

        public CompositeCSharpAgent(
            IBaseAgentHelper baseAgentHelper,
            ICsharpCodeAgent csharpCodeAgent,
            ILogger<CompositeCSharpAgent> logger,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider)
            : base(baseAgentHelper, logger)
        {
            _csharpCodeAgent = csharpCodeAgent;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
            _plannerHelpers = plannerHelpers;
            _semanticKernelProvider = semanticKernelProvider;
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
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.DeepSeekLlm, ConnectionType.GeminiLlm },
                _debugMessageSenderName,
                agent.LlmType);

            var temperature = GetTemperature(llmConnection, agent);
            var topP = GetTopP(agent);
            var taskPrompt = await GetParameterValueAsync(AgentContentParameters.Prompt);

            var paramDescription = agent.Content["parameterDescription"].Value
                .Split(',')
                .Select((p, i) => $@"        // Parameter{i + 1}: {p}{Environment.NewLine}string parameter{i + 1} = Parameters[""parameter{i + 1}""];")
                .ToList();
            var parametersDescription = string.Join(Environment.NewLine, paramDescription);

            var agentsDescription = await GetAgentsDescriptions(agent);

            var promptTemplate = await GetParameterValueAsync(AgentContentParameters.CodeGenerationPrompt);
            if (string.IsNullOrEmpty(promptTemplate))
                promptTemplate = CodeGenerationPromptTemplate;

            promptTemplate = promptTemplate
                .Replace("{{agentsDescription}}", agentsDescription)
                .Replace("{{parametersDescription}}", parametersDescription)
                .Replace("{{outputDescription}}", agent.Content["outputDescription"].Value)
                .Replace("{{taskDescription}}", taskPrompt)
                .Trim();

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt", promptTemplate);

            var cacheKey = promptTemplate.GetHash();
            if (!_requestAccessor.UseCachedPlan || !CodeCache.TryGetValue(cacheKey, out var code))
            {
                code = await _semanticKernelProvider.ExecutePrompt(llmConnection, promptTemplate, temperature, topP, SystemMessage);
                CodeCache[cacheKey] = code;
            }

            for (var attempt = 0; attempt < RegenerationAttempts; attempt++)
            {
                try
                {
                    code = TrimCode(code);

                    var generatedAgent = new AgentModel
                    {
                        Name = $"{agent.Name}-Generated",
                        Type = AgentType.CsharpCode,
                        Content = new Dictionary<string, ConfigurableSetting>
                        {
                            { "csharpCode", new ConfigurableSetting { Value = code } }
                        }
                    };

                    var result = await _csharpCodeAgent.DoCallWrapper(generatedAgent, parameters);
                    return result;
                }
                catch (Exception)
                {
                    if (string.IsNullOrEmpty(_csharpCodeAgent.BuildError))
                        throw;

                    var fixPrompt = $@"
{code}

You are an expert C# developer. Fix code based on the following build error.
- Output only C# code. No explanations.
- Do not change the class name or method signature.
- Ensure the code is fully compilable.

Error:
{_csharpCodeAgent.BuildError}
";

                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt Fix", fixPrompt);
                    code = await _semanticKernelProvider.ExecutePrompt(llmConnection, fixPrompt, temperature, topP, SystemMessage);
                    _csharpCodeAgent.BuildError = string.Empty;
                }
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
If possible, use existing Agents. Do not re-implement their functionality.
Syntax: ExecuteAgent(""agentName"", new List<string> { ""Param1"", ""Param2"" });
Existing Agents:
";

            foreach (var item in agentsList)
            {
                var agentId = item.AgentId.ToString();
                if (enabledAgents.TryGetValue(agentId, out var isEnabled) && isEnabled)
                {
                    result += $@"
## AgentName: {item.Name}
- Description: {item.Description}
- Parameters: {item.Content.GetValueOrDefault("parameterDescription")?.Value ?? ""}
- Output: {item.Content.GetValueOrDefault("outputDescription")?.Value ?? ""}
";
                }
            }

            return result.Trim();
        }

        private double GetTemperature(ConnectionModel llmConnection, AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.Temperature, out var val) &&
                double.TryParse(val.Value, out var temp))
                return temp;

            return llmConnection.Content.ContainsKey("temperature")
                ? Convert.ToDouble(llmConnection.Content["temperature"])
                : 0.0;
        }

        private double GetTopP(AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.TopP, out var val) &&
                double.TryParse(val.Value, out var topP))
                return topP;

            return 0.0;
        }

        private string TrimCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            var startIndex = code.IndexOf("```", StringComparison.Ordinal);
            if (startIndex == -1)
                return code.Trim();

            startIndex = code.IndexOf('\n', startIndex);
            if (startIndex == -1)
                return code.Trim();

            startIndex += 1;
            var endIndex = code.IndexOf("```", startIndex, StringComparison.Ordinal);
            if (endIndex == -1)
                return code.Trim();

            return code.Substring(startIndex, endIndex - startIndex).Trim();
        }
    }

    public interface ICompositeCSharpAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
