using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeLoopAgent : BaseCompositeAgent, ICompositeLoopAgent
    {
        private string _debugMessageSenderName = "CompositeLoopAgent";
        private readonly ExtendedConfig _extendedConfig;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly IAgentExecutor _agentExecutor;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private const string LastStepNoAgentsMessage = "Last step so no agents available. Use 'finish' or 'cannot' action.";

        private const string CustomActionsPlaceholder = "{{custom actions}}";
        private const string CallAgentCustomAction = @"{
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""call""] },
            ""agent"": { ""type"": ""string"", ""description"": ""Agent Name"" },
            ""parameters"": { 
              ""type"": ""object"", 
              ""description"": ""Dictionary of named parameters to pass into the agent."",
              ""additionalProperties"": { ""type"": ""string"" }
            },
            ""description"": { ""type"": ""string"", ""description"": ""A short, user-friendly explanation (in plain language) of why this agent is being called. This explanation must be written for the end user, not a developer. Avoid technical details; describe the purpose in a way a non-technical user can understand"" }
          },
          ""required"": [""action"", ""agent"", ""parameters"", ""description""],
          ""additionalProperties"": false
        },";

        private const string PlannerPromptJsonSchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""nextStep"": {
      ""description"": ""Call an agent with parameters."",
      ""anyOf"": [
        " + CustomActionsPlaceholder + @"
        {
          ""description"": ""Finish the reasoning with final answer."",
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""finish""] },
            ""result"": { ""type"": ""string"", ""description"": ""Answer to the users question"" },
            ""description"": { ""type"": ""string"", ""description"": ""A short, user-friendly explanation (in plain language) of the final answer. This explanation must be written for the end user, not a developer. Avoid technical details; describe the answer in a way a non-technical user can understand"" }
          },
          ""required"": [""action"", ""result"", ""description""],
          ""additionalProperties"": false
        },
        {
          ""description"": ""Cannot complete due to missing data or failed attempt."",
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""cannot""] },
            ""reason"": { ""type"": ""string"", ""description"": ""The reason why can not answer"" },
            ""description"": { ""type"": ""string"", ""description"": ""A short, user-friendly explanation (in plain language) of why the planner cannot complete the request. This explanation must be written for the end user, not a developer. Avoid technical details; describe the issue in a way a non-technical user can understand"" }
          },
          ""required"": [""action"", ""reason"", ""description""],
          ""additionalProperties"": false
        }
      ]
    }
  },
  ""required"": [""nextStep""],
  ""additionalProperties"": false
}";


        private static class AgentContentParameters
        {
            public const string UserInput = "userInput";
            public const string EnabledAgents = "enabledAgents";
            public const string Temperature = "temperature";
            public const string MaxIterations = "maxIterations";
            public const string TopP = "top_p";
            public const string SystemMessage = "systemMessage";
            public const string FinalPolishPrompt = "finalPolishPrompt";
            public const string PlannerPromptTemplate = "plannerPromptTemplate";
            public const string PreprocessPromptTemplate = "preprocessPromptTemplate";
            public const string UseStrictJsonMode = "useStrictJsonMode";
        }

        public CompositeLoopAgent(
            ExtendedConfig extendedConfig,
            IBaseAgentHelper baseAgentHelper,
            IPlannerHelpers plannerHelpers,
            IAgentExecutor agentExecutor,
            ISemanticKernelProvider semanticKernelProvider,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<CompositeLoopAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _extendedConfig = extendedConfig;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _plannerHelpers = plannerHelpers;
            _agentExecutor = agentExecutor;
            _semanticKernelProvider = semanticKernelProvider;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.GeminiLlm, ConnectionType.DeepSeekLlm },
                _debugMessageSenderName, agent.LlmType);

            var userInput = await GetParameterValueAsync(AgentContentParameters.UserInput);
            var systemMessage = await GetParameterValueAsync(AgentContentParameters.SystemMessage);
            var maxIterations = Convert.ToInt32(await GetParameterValueAsync(AgentContentParameters.MaxIterations));
            var preprocessPromptTemplate = await GetParameterValueAsync(AgentContentParameters.PreprocessPromptTemplate);
            var finalPolishPrompt = await GetParameterValueAsync(AgentContentParameters.FinalPolishPrompt);
            var plannerPromptTemplate = await GetParameterValueAsync(AgentContentParameters.PlannerPromptTemplate);
            var temperature = GetTemperature(llmConnection, agent);
            var useStrictJsonMode = bool.TryParse(await GetParameterValueAsync(AgentContentParameters.UseStrictJsonMode), out var strictMode) && strictMode;
            var topP = GetTopP(agent);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", userInput);

            var agentsDescription = await GetAgentsDescriptions(agent, parameters);

            if (!string.IsNullOrEmpty(preprocessPromptTemplate))
            {
                userInput = await _semanticKernelProvider.ExecutePrompt(
                    llmConnection,
                    preprocessPromptTemplate
                        .Replace("{userInput}", userInput)
                        .Replace("{agentsList}", agentsDescription),
                    temperature,
                    topP,
                    string.Empty);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Preprocessed Prompt", userInput);
            }

            var history = new List<(string agent, Dictionary<string, string> parameters, string result)>();

            for (int i = 0; i < maxIterations; i++)
            {
                var context = string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}",
                    history.Select(h =>
                    {
                        var paramsStr = string.Join(", ", h.parameters.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                        return $"Agent: {h.agent}, Parameters: {{{paramsStr}}}, Result: {h.result}";
                    }));

                var jsonSchema = string.Empty;
                if (i == maxIterations - 1)
                {
                    agentsDescription = LastStepNoAgentsMessage;
                    jsonSchema = PlannerPromptJsonSchema.Replace(CustomActionsPlaceholder, string.Empty);
                }
                else
                {
                    jsonSchema = PlannerPromptJsonSchema.Replace(CustomActionsPlaceholder, CallAgentCustomAction);
                }

                var plannerPrompt = plannerPromptTemplate
                    .Replace("{agentsList}", agentsDescription)
                    .Replace("{userInput}", userInput)
                    .Replace("{previousResults}", context);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {i + 1}", plannerPrompt);

                var plannerResponse = await _semanticKernelProvider.ExecutePrompt(
                    llmConnection,
                    plannerPrompt,
                    temperature,
                    topP,
                    systemMessage,
                    jsonSchema,
                    useStrictJsonMode);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {i + 1} Answer", plannerResponse);

                var parsedResponse = plannerResponse.JsonGet<PlannerResponse>();
                var parsed = parsedResponse?.NextStep;
                if (parsed == null || string.IsNullOrEmpty(parsed.Action))
                    throw new AiCoreUiException("Planner response invalid or empty");

                if (_extendedConfig.UseReasoningMessages && !string.IsNullOrEmpty(parsed.Description))
                    _responseAccessor.AddReasoningMessage(parsed.Description);

                switch (parsed.Action.ToLower())
                {
                    case "call":
                        try
                        {
                            var paramsList = parsed.Parameters.Select(kvp => kvp.Value).ToList();
                            var subAgentResult = await _agentExecutor.ExecuteAsync(parsed.Agent, paramsList);
                            history.Add((parsed.Agent, parsed.Parameters, subAgentResult));
                        }
                        catch (Exception ex)
                        {
                            history.Add((parsed.Agent, parsed.Parameters, $"ERROR: {ex.Message}"));
                        }
                        break;

                    case "finish":
                        if (!string.IsNullOrEmpty(finalPolishPrompt))
                        {
                            var result = await _semanticKernelProvider.ExecutePrompt(
                                llmConnection,
                                finalPolishPrompt.Replace("{result}", parsed.Result ?? ""),
                                temperature,
                                topP,
                                string.Empty);
                            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Final Polished Prompt", result);
                            return result;
                        }
                        return parsed.Result ?? "";

                    case "cannot":
                        return $"Planner could not complete: {parsed.Reason}";

                    default:
                        throw new AiCoreUiException("Unknown planner action: " + parsed.Action);
                }
            }

            throw new AiCoreUiException("Planner did not finish within max iterations");
        }

        private async Task<string> GetAgentsDescriptions(AgentModel agent, Dictionary<string, string> parameters)
        {
            var enabledAgents = agent.Content[AgentContentParameters.EnabledAgents].Value.JsonGet<Dictionary<string, string>>();
            if (enabledAgents == null || !enabledAgents.Any())
                return string.Empty;

            var agentsList = await _plannerHelpers.GetAgentsList();
            var result = $"# Agents (use named parameters as key-value pairs, all values are strings){Environment.NewLine}";

            foreach (var agentItem in agentsList)
            {
                var agentId = agentItem.AgentId.ToString();
                if (enabledAgents.TryGetValue(agentId, out var enabled))
                {
                    if (string.IsNullOrEmpty(enabled))
                        continue;
                    var enabledParts = enabled.Split(':');
                    var isEnable = enabledParts[0].ToLower() == "true";
                    var conditionAgent = enabledParts.Length > 1 ? enabledParts[1] : string.Empty;
                    if (isEnable && (string.IsNullOrEmpty(conditionAgent) || await CheckConditionAgent(conditionAgent, parameters)))
                        result += $@"
## AgentName: {agentItem.Name}
- Description: {agentItem.Description}
- Parameters: {GetParametersDefinition(ParameterRecordModel.Parse(agentItem.Content.GetValueOrDefault("parameterDescription")?.Value ?? ""))}
- Output: {agentItem.Content["outputDescription"].Value}
";
                }
            }

            return result;
        }

        private async Task<bool> CheckConditionAgent(string conditionAgent, Dictionary<string, string> parameters)
        {
            if (string.IsNullOrEmpty(conditionAgent))
                return true;
            try
            {
                var paramsList = parameters.Select(kvp => kvp.Value).ToList();
                var result = await _agentExecutor.ExecuteAsync(conditionAgent, paramsList);
                return result.Trim().ToLower() == "true";
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "CheckConditionAgent Error", $"{conditionAgent}: {ex}");
                return false;
            }
        }

        private double GetTemperature(ConnectionModel llmConnection, AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.Temperature, out var val) &&
                double.TryParse(val.Value, out var agentTemp))
                return agentTemp;

            return llmConnection.Content.ContainsKey(AgentContentParameters.Temperature)
                ? Convert.ToDouble(llmConnection.Content[AgentContentParameters.Temperature])
                : 0;
        }

        private double GetTopP(AgentModel agent)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.TopP, out var val) &&
                double.TryParse(val.Value, out var topP))
                return topP;

            return 0;
        }

        public class PlannerResponse
        {
            public PlannerInstruction NextStep { get; set; } = new();
        }

        public class PlannerInstruction
        {
            public string Action { get; set; } = "";
            public string Agent { get; set; } = "";
            public Dictionary<string, string> Parameters { get; set; } = new();
            public string Result { get; set; } = "";
            public string Reason { get; set; } = "";
            public string Description { get; set; } = "";
        }
    }

    public interface ICompositeLoopAgent : IDoCallWrapperAgent
    {
        Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);
    }
}