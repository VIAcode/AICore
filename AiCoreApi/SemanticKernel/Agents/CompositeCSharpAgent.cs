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
        private static ConcurrentDictionary<string, string> CodeCache = new();
        private string _debugMessageSenderName = "CompositeCSharpAgent";
        private static class AgentContentParameters
        {
            public const string EnabledAgents = "enabledAgents";
            public const string Prompt = "prompt";
            public const string CodeGenerationPrompt = "codeGenerationPrompt";
            public const string Temperature = "temperature";
            public const string TopP = "top_p";
        }

        private const string SystemMessage = "You are an expert C# developer.";
        private const int RegenerationAttempts = 3;

        private const string CodeGenerationPromptText = @$"
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
// Add necessary packages to import only when needed, use the following format:
#r ""nuget: PackageNameSample, 1.00""

using System; 
using System.Collections.Generic;
using System.Linq;
// add other necessary namespaces

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
        private static class CodeGenerationPromptPlaceHolders
        {
            public const string AgentsDescription = "{{agentsDescription}}";
            public const string ParametersDescription = "{{parametersDescription}}";
            public const string OutputDescription = "{{outputDescription}}";
            public const string TaskDescription = "{{taskDescription}}";
        }

        private readonly ICsharpCodeAgent _csharpCodeAgent;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        public CompositeCSharpAgent(
            IBaseAgentHelper baseAgentHelper,
            ICsharpCodeAgent csharpCodeAgent,
            ILogger<CompositeCSharpAgent> logger,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider
            )
            : base(baseAgentHelper, logger)
        {
            _csharpCodeAgent = csharpCodeAgent;
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
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.DeepSeekLlm, ConnectionType.GeminiLlm }, _debugMessageSenderName, agent.LlmType);
            var temperature = GetTemperature(llmConnection, agent);
            var topP = GetTopP(agent);
            var prompt = GetParameterValue(AgentContentParameters.Prompt);

            // Prompt to GPT for generating the class
            var parameterDescription = agent.Content["parameterDescription"].Value
                .Split(',')
                .Select((p, i)  => $@"        // Parameter{i+1}: {p}{Environment.NewLine}string parameter{i+1} = Parameters[""parameter{i+1}""]")
                .ToList();
            var parametersDescription = string.Join(Environment.NewLine, parameterDescription);
            _plannerHelpers.CompositeCSharpAgent = this;

            string agentsDescription = await GetAgentsDescriptions(agent);

            string promptTemplate = GetParameterValue(AgentContentParameters.CodeGenerationPrompt);
            if(string.IsNullOrEmpty(promptTemplate))
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


            // Forward generated code for compilation and execution
            for (var i = 0; i < RegenerationAttempts; i++)
            {
                try
                {
                    code = TrimCode(code);
                    var csharpAgent = new AgentModel
                    {
                        Name = $"{agent.Name}-Generated",
                        Type = AgentType.CsharpCode,
                        Content = new Dictionary<string, ConfigurableSetting>
                        {
                            { "csharpCode", new ConfigurableSetting { Value = code } }
                        }
                    };
                    var result = await _csharpCodeAgent.DoCallWrapper(csharpAgent, parameters);
                    CodeCache.TryAdd(cachekey, code);
                    return result;
                }
                catch(Exception)
                {
                    // If the code is not compilable, try to fix it
                    if (string.IsNullOrEmpty(_csharpCodeAgent.BuildError))
                        throw;

                    var fixErrorPrompt = $@"{code}



You are an expert C# developer. Fix code based on the Error Text below.
- Output only C# code. No explanation or comments.
- Do not change the class name or method signature.
- Ensure the code is fully compilable and free of undefined variables.

{_csharpCodeAgent.BuildError}
";
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Prompt Fix", fixErrorPrompt);
                    code = await _semanticKernelProvider.ExecutePrompt(llmConnection, fixErrorPrompt, temperature, topP, SystemMessage);
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
            var result = $@"
# Agents
If it is possible to use the existing Agent, use it. Do not create the code doing the same functionality.
Syntax: ExecuteAgent(""agentName"", new List<string> {{""Parameter1 value"", ""Parameter2 value"", ...}});
Existing Agents:
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
            if (agent.Content.ContainsKey(AgentContentParameters.Temperature))
            {
                var isCorrect = double.TryParse(agent.Content[AgentContentParameters.Temperature].Value, out var agentTemperature);
                if (isCorrect)
                    return agentTemperature;
            }
            return llmConnection.Content.ContainsKey("temperature") ? Convert.ToDouble(llmConnection.Content["temperature"]) : 0;
        }

        private double GetTopP(AgentModel agent)
        {
            if (agent.Content.ContainsKey(AgentContentParameters.TopP))
            {
                var isCorrect = double.TryParse(agent.Content[AgentContentParameters.TopP].Value, out var agentTopP);
                if (isCorrect)
                    return agentTopP;
            }
            return 0;
        }

        private string TrimCode(string code)
        {
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

            var extracted = code.Substring(startIndex, endIndex - startIndex);
            return extracted.Trim();
        }
    }


    public interface ICompositeCSharpAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
