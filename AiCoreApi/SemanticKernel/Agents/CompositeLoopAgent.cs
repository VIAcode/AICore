using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeLoopAgent : BaseEnabledAgentsAgent, ICompositeLoopAgent
    {
        private string _debugMessageSenderName = "CompositeLoopAgent";

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private const string LastStepNoAgentsMessage = "Last step so no agents available. Use 'finish' or 'cannot' action.";

        private const string PlannerPromptJsonSchema = @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""title"": ""PlannerInstruction"",
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string""
    },
    ""agent"": {
      ""type"": ""string""
    },
    ""params"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string""
      }
    },
    ""result"": {
      ""type"": ""string""
    },
    ""reason"": {
      ""type"": ""string""
    }
  },
  ""required"": [""action""],
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
        }

        public CompositeLoopAgent(
            IBaseAgentHelper baseAgentHelper,
            IPlannerHelpers plannerHelpers,
            ISemanticKernelProvider semanticKernelProvider,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<CompositeLoopAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _plannerHelpers = plannerHelpers;
            _semanticKernelProvider = semanticKernelProvider;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.GeminiLlm, ConnectionType.DeepSeekLlm }, _debugMessageSenderName, agent.LlmType);

            var userInput = GetParameterValue(AgentContentParameters.UserInput);
            var systemMessage = GetParameterValue(AgentContentParameters.SystemMessage);
            var maxIterations = Convert.ToInt32(GetParameterValue(AgentContentParameters.MaxIterations));
            var preprocessPromptTemplate = GetParameterValue(AgentContentParameters.PreprocessPromptTemplate);
            var finalPolishPrompt = GetParameterValue(AgentContentParameters.FinalPolishPrompt);
            var plannerPromptTemplate = GetParameterValue(AgentContentParameters.PlannerPromptTemplate);
            var temperature = GetTemperature(llmConnection, agent);
            var topP = GetTopP(agent);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"DoCall Request", userInput);

            if (!string.IsNullOrEmpty(preprocessPromptTemplate))
            {
                userInput = await _semanticKernelProvider.ExecutePrompt(llmConnection, preprocessPromptTemplate.Replace("{userInput}", userInput), temperature, topP, string.Empty);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Preprocessed Prompt", userInput);
            }

            string agentsDescription = await GetAgentsDescriptions(agent);

            var history = new List<(string agent, List<string> @params, string result)>();
            for (int i = 0; i < maxIterations; i++)
            {
                var context = string.Join($"{Environment.NewLine}{Environment.NewLine}", history.Select(h => $"Agent: {h.agent}, Params: [{string.Join(", ", h.@params)}], Result: {h.result}"));

                if (i == maxIterations - 1)
                {
                    agentsDescription = LastStepNoAgentsMessage;
                }

                var plannerPrompt = plannerPromptTemplate
                    .Replace("{agentsList}", agentsDescription)
                    .Replace("{userInput}", userInput)
                    .Replace("{previousResults}", context);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {i + 1}", plannerPrompt);

                var plannerResponse = await _semanticKernelProvider.ExecutePrompt(llmConnection, plannerPrompt, temperature, topP, systemMessage, PlannerPromptJsonSchema);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {i + 1} Answer", plannerResponse);
                var parsed = plannerResponse.JsonGet<PlannerInstruction>();

                if (parsed == null || string.IsNullOrEmpty(parsed.Action))
                    throw new AiCoreUiException("Planner response invalid or empty");

                switch (parsed.Action.ToLower())
                {
                    case "call":
                        try
                        {
                            var subAgentResult = await ExecuteAgent(parsed.Agent, parsed.Params);
                            history.Add((parsed.Agent, parsed.Params, subAgentResult));
                        }
                        catch (Exception ex)
                        {
                            history.Add((parsed.Agent, parsed.Params, $"ERROR: {ex.Message}"));
                        }
                        break;

                    case "finish":
                        if (!string.IsNullOrEmpty(finalPolishPrompt))
                        {
                            var result = await _semanticKernelProvider.ExecutePrompt(llmConnection, finalPolishPrompt.Replace("{result}", parsed.Result ?? ""), temperature, topP, string.Empty);
                            _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Final Polished Prompt", result);
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

        private async Task<string> ExecuteAgent(string agentName, List<string>? parameters = null)
        {
            _plannerHelpers.CompositeLoopAgent = this;
            try
            {
                return await _plannerHelpers.ExecuteAgent(agentName, parameters);
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "ExecuteAgent Error", $"Agent: {agentName}\r\n\r\n Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}");
                throw;
            }
        }

        private async Task<string> GetAgentsDescriptions(AgentModel agent)
        {
            var enabledAgents = agent.Content[AgentContentParameters.EnabledAgents].Value.JsonGet<Dictionary<string, bool>>();
            if (enabledAgents == null || !enabledAgents.Any())
                return string.Empty;
            var agentsList = await _plannerHelpers.GetAgentsList();
            var result = $@"# Agents (all parameters are strings, out outputs are strings){Environment.NewLine}";
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
            return llmConnection.Content.ContainsKey("temperature")
                ? Convert.ToDouble(llmConnection.Content["temperature"])
                : 0;
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
    }

    public class PlannerInstruction
    {
        public string Action { get; set; } = "";
        public string Agent { get; set; } = "";
        public List<string> Params { get; set; } = new();
        public string Result { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public interface ICompositeLoopAgent
    {
        Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
