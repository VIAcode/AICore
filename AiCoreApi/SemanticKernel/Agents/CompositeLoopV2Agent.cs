using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel.ChatCompletion;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CompositeLoopV2Agent : BaseEnabledAgentsAgent, ICompositeLoopV2Agent
    {
        private string _debugMessageSenderName = "CompositeLoopV2Agent";
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IPlannerHelpers _plannerHelpers;
        private readonly IAgentExecutor _agentExecutor;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IConnectionProcessor _connectionProcessor;

        private const string CustomActionsPlaceholder = "{{custom actions}}";
        private const string CallAgentCustomAction = @"{
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""call""] },
            ""agent"": { ""type"": ""string"", ""description"": ""Agent Name"" },
            ""params"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
          },
          ""required"": [""action"", ""agent"", ""params""],
          ""additionalProperties"": false
        },";
        private const string RevisePlanCustomAction = @"{
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""modifyexecutionplan""] },
            ""changeReason"": { ""type"": ""string"", ""description"": ""Reason for modification"" }
          },
          ""required"": [""action"", ""changeReason""],
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
            ""result"": { ""type"": ""string"", ""description"": ""Answer to the users question"" }
          },
          ""required"": [""action"", ""result""],
          ""additionalProperties"": false
        },
        {
          ""description"": ""Cannot complete due to missing data or failed attempt."",
          ""type"": ""object"",
          ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""cannot""] },
            ""reason"": { ""type"": ""string"", ""description"": ""The reason why can not answer"" }
          },
          ""required"": [""action"", ""reason""],
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
            public const string PreprocessPromptTemplate = "preprocessPromptTemplate";
            public const string UseExecutionPlan = "useExecutionPlan";
            public const string ExecutionPlanTemplate = "executionPlanTemplate";
            public const string PlanModificationPrompt = "planModificationPrompt";
            public const string PlannerModificationActionTemplate = "plannerModificationActionTemplate";
            public const string MaxStepAnswerLength = "maxStepAnswerLength";
        }

        public CompositeLoopV2Agent(
            IBaseAgentHelper baseAgentHelper,
            IPlannerHelpers plannerHelpers,
            IAgentExecutor agentExecutor,
            ISemanticKernelProvider semanticKernelProvider,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<CompositeLoopV2Agent> logger)
            : base(baseAgentHelper, logger)
        {
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

            // Initialize connections and configuration
            var config = await InitializeConfigurationAsync(agent, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", config.UserInput);

            // Preprocess user input if needed
            var processedInput = await PreprocessUserInputAsync(config);

            // Prepare execution plan if needed
            var executionPlan = await PrepareExecutionPlanAsync(config);

            // Create conversation context
            var context = new ConversationContext(config, processedInput, executionPlan);

            // Execute planning loop
            var result = await ExecutePlanningLoopAsync(config, context);

            return result;
        }

        private async Task<AgentConfiguration> InitializeConfigurationAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = await GetConnectionAsync(
                _requestAccessor,
                _responseAccessor,
                connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm, ConnectionType.GeminiLlm, ConnectionType.DeepSeekLlm },
                _debugMessageSenderName,
                agent.LlmType);

            return new AgentConfiguration
            {
                LlmConnection = llmConnection,
                UserInput = await GetParameterValueAsync(AgentContentParameters.UserInput),
                SystemMessage = await GetParameterValueAsync(AgentContentParameters.SystemMessage),
                MaxIterations = Convert.ToInt32(await GetParameterValueAsync(AgentContentParameters.MaxIterations)),
                MaxStepAnswerLength = Convert.ToInt32(await GetParameterValueAsync(AgentContentParameters.MaxStepAnswerLength, "0")),
                PreprocessPromptTemplate = await GetParameterValueAsync(AgentContentParameters.PreprocessPromptTemplate),
                FinalPolishPrompt = await GetParameterValueAsync(AgentContentParameters.FinalPolishPrompt),
                UseExecutionPlan = Convert.ToBoolean(await GetParameterValueAsync(AgentContentParameters.UseExecutionPlan, "False")),
                ExecutionPlanTemplate = await GetParameterValueAsync(AgentContentParameters.ExecutionPlanTemplate),
                PlanModificationPrompt = await GetParameterValueAsync(AgentContentParameters.PlanModificationPrompt),
                PlannerModificationActionTemplate = await GetParameterValueAsync(AgentContentParameters.PlannerModificationActionTemplate),
                Temperature = GetTemperature(llmConnection, agent),
                TopP = GetTopP(agent),
                AgentsDescription = await GetAgentsDescriptions(agent, parameters)
            };
        }

        private async Task<string> PreprocessUserInputAsync(AgentConfiguration config)
        {
            if (string.IsNullOrEmpty(config.PreprocessPromptTemplate))
                return config.UserInput;

            var preprocessedInput = await _semanticKernelProvider.ExecutePrompt(
                config.LlmConnection,
                config.PreprocessPromptTemplate
                    .Replace("{userInput}", config.UserInput)
                    .Replace("{agentsList}", config.AgentsDescription),
                config.Temperature,
                config.TopP,
                string.Empty);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Preprocessed Prompt", preprocessedInput);

            return preprocessedInput;
        }

        private async Task<string> PrepareExecutionPlanAsync(AgentConfiguration config)
        {
            if (!config.UseExecutionPlan || string.IsNullOrEmpty(config.ExecutionPlanTemplate))
                return string.Empty;

            var executionPlan = await _semanticKernelProvider.ExecutePrompt(
                config.LlmConnection,
                config.ExecutionPlanTemplate
                    .Replace("{userInput}", config.UserInput)
                    .Replace("{agentsList}", config.AgentsDescription),
                config.Temperature,
                config.TopP,
                string.Empty);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execution Plan", executionPlan);
            return executionPlan;
        }

        private string GetJsonSchema(AgentConfiguration config, int iteration) =>
            PlannerPromptJsonSchema.Replace(CustomActionsPlaceholder,
                iteration == config.MaxIterations - 1
                    ? string.Empty
                    : CallAgentCustomAction + RevisePlanCustomAction);

        private async Task<string> ExecutePlanningLoopAsync(AgentConfiguration config, ConversationContext context)
        {
            for (var iteration = 0; iteration < config.MaxIterations; iteration++)
            {
                context.CurrentIteration++; 

                // Produce ChatHistory from context
                var chatHistory = context.ProduceChatHistory(config);

                var currentStep = context.ProduceNextStepPrompt();
                chatHistory.AddUserMessage(currentStep);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {iteration + 1}", currentStep);

                // Execute planner prompt
                var plannerResponse = await _semanticKernelProvider.ExecutePromptWithHistory(
                    config.LlmConnection,
                    chatHistory,
                    config.Temperature,
                    config.TopP,
                    GetJsonSchema(config, iteration),
                    true);

                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"Step {iteration + 1} Answer", plannerResponse);

                // Parse and execute planner instruction
                var instruction = ParsePlannerResponse(plannerResponse);
                var result = await ExecutePlannerInstructionAsync(
                    instruction,
                    config,
                    context,
                    plannerResponse);

                if (!result.ShouldContinue)
                    return result.FinalResult!;
            }

            throw new AiCoreUiException("Planner did not finish within max iterations");
        }

        private PlannerInstruction ParsePlannerResponse(string plannerResponse)
        {
            var parsedPlannerResponse = plannerResponse.JsonGet<PlannerResponse>();
            var parsed = parsedPlannerResponse?.NextStep;
            if (parsed == null || string.IsNullOrEmpty(parsed.Action))
                throw new AiCoreUiException("Planner response invalid or empty");

            return parsed;
        }

        private async Task<PlannerInstructionResult> ExecutePlannerInstructionAsync(
            PlannerInstruction instruction,
            AgentConfiguration config,
            ConversationContext context,
            string plannerResponse)
        {
            switch (instruction.Action.ToLower())
            {
                case "call":
                    await ExecuteAgentCallAsync(instruction, context, plannerResponse, config);
                    return new PlannerInstructionResult { FinalResult = null, ShouldContinue = true };

                case "finish":
                    var finishResult = await ExecuteFinishActionAsync(instruction, config);
                    return new PlannerInstructionResult { FinalResult = finishResult, ShouldContinue = false };

                case "cannot":
                    var cannotResult = $"Planner could not complete: {instruction.Reason}";
                    return new PlannerInstructionResult { FinalResult = cannotResult, ShouldContinue = false };

                case "modifyexecutionplan":
                    await ExecuteModifyExecutionPlanAsync(instruction, config, context);
                    return new PlannerInstructionResult { FinalResult = null, ShouldContinue = true };

                default:
                    throw new AiCoreUiException("Unknown planner action: " + instruction.Action);
            }
        }

        private async Task ExecuteAgentCallAsync(
            PlannerInstruction instruction,
            ConversationContext context,
            string plannerResponse,
            AgentConfiguration config)
        {
            try
            {
                var subAgentResult = await _agentExecutor.ExecuteAsync(instruction.Agent, instruction.Params);
                if(subAgentResult.Length > config.MaxStepAnswerLength && config.MaxStepAnswerLength > 0)
                {
                    subAgentResult = subAgentResult.Substring(0, config.MaxStepAnswerLength) + "...";
                }
                context.AddExecutionEntry(instruction.Agent, instruction.Params, subAgentResult);
                context.ExecutionHistory.Last().PlannerResponse = plannerResponse;
            }

            catch (Exception ex)
            {
                var errorText = $"ERROR: {ex.Message}";
                context.AddExecutionEntry(instruction.Agent, instruction.Params, errorText);
                context.ExecutionHistory.Last().PlannerResponse = plannerResponse;
            }
        }

        private async Task<string> ExecuteFinishActionAsync(PlannerInstruction instruction, AgentConfiguration config)
        {
            if (string.IsNullOrEmpty(config.FinalPolishPrompt))
                return instruction.Result ?? "";

            var finalPrompt = config.FinalPolishPrompt.Replace("{result}", instruction.Result ?? "");
            var finalHistory = new ChatHistory();
            finalHistory.AddUserMessage(finalPrompt);

            var result = await _semanticKernelProvider.ExecutePromptWithHistory(
                config.LlmConnection,
                finalHistory,
                config.Temperature,
                config.TopP);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Final Polished Prompt", result);

            return result;
        }

        private async Task ExecuteModifyExecutionPlanAsync(
            PlannerInstruction instruction,
            AgentConfiguration config,
            ConversationContext context)
        {
            var defaultPrompt = @"Based on the execution results and identified issues, create a revised execution plan.

Current Execution Plan:
{currentPlan}

Original User Request:
{userInput}

Execution History:
{executionHistory}

Available Agents:
{agentsList}

Reason for Plan Modification:
{modificationReason}

Provide a clear, revised execution plan that addresses the issues and outlines the next steps to achieve the user's goal.";

            var prompt = string.IsNullOrEmpty(config.PlanModificationPrompt)
                ? defaultPrompt
                : config.PlanModificationPrompt;

            prompt = prompt
                .Replace("{currentPlan}", context.ExecutionPlan)
                .Replace("{userInput}", config.UserInput)
                .Replace("{agentsList}", config.AgentsDescription)
                .Replace("{executionHistory}", context.GetExecutionHistoryAsText())
                .Replace("{modificationReason}", instruction.ChangeReason);

            var modifiedPlan = await _semanticKernelProvider.ExecutePrompt(
                config.LlmConnection,
                prompt,
                config.Temperature,
                config.TopP,
                string.Empty);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Modified Execution Plan", modifiedPlan);

            // Update context with new plan
            context.UpdateExecutionPlan(modifiedPlan);

            // Add plan modification entry to history
            context.ExecutionHistory.Add(new ExecutionHistoryEntry
            {
                IsPlanModification = true,
                ModificationReason = instruction.ChangeReason ?? "Issues encountered during execution",
                Timestamp = DateTime.UtcNow
            });
        }

        private async Task<string> GetAgentsDescriptions(AgentModel agent, Dictionary<string, string> parameters)
        {
            var enabledAgents = agent.Content[AgentContentParameters.EnabledAgents].Value.JsonGet<Dictionary<string, string>>();

            if (enabledAgents == null || !enabledAgents.Any())
                return string.Empty;

            var agentsList = await _plannerHelpers.GetAgentsList();
            var result = $"# Agents (all parameters are strings, outputs are strings){Environment.NewLine}";

            foreach (var agentItem in agentsList)
            {
                var agentId = agentItem.AgentId.ToString();

                if (enabledAgents.TryGetValue(agentId, out var enabled))
                {
                    var enabledParts = enabled.Split(':');
                    var isEnable = enabledParts[0].ToLower() == "true";
                    var conditionAgent = enabledParts.Length > 1 ? enabledParts[1] : string.Empty;
                    if (isEnable && (string.IsNullOrEmpty(conditionAgent) || await CheckConditionAgent(conditionAgent, parameters)))
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

        private async Task<bool> CheckConditionAgent(string conditionAgent, Dictionary<string, string> parameters)
        {
            if (string.IsNullOrEmpty(conditionAgent))
                return true;
            try
            {
                var result = await _agentExecutor.ExecuteAsync(conditionAgent, parameters.Select(x => x.Value).ToList());
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
            public List<string> Params { get; set; } = new();
            public string Result { get; set; } = "";
            public string Reason { get; set; } = "";
            public string ChangeReason { get; set; } = "";
        }

        public class PlannerInstructionResult
        {
            public bool ShouldContinue { get; set; }
            public string? FinalResult { get; set; }
        }
    }

    // Helper classes
    public class AgentConfiguration
    {
        public ConnectionModel LlmConnection { get; set; } = null!;
        public string UserInput { get; set; } = "";
        public string SystemMessage { get; set; } = "";
        public int MaxIterations { get; set; }
        public int MaxStepAnswerLength { get; set; }
        public string PreprocessPromptTemplate { get; set; } = "";
        public string FinalPolishPrompt { get; set; } = "";
        public bool UseExecutionPlan { get; set; }
        public string ExecutionPlanTemplate { get; set; } = "";
        public string PlanModificationPrompt { get; set; } = "";
        public string PlannerModificationActionTemplate { get; set; } = "";
        public double Temperature { get; set; }
        public double TopP { get; set; }
        public string AgentsDescription { get; set; } = "";
    }

    public class ConversationContext
    {
        private readonly AgentConfiguration _config;

        public string SystemPrompt { get; private set; }
        public string ExecutionPlan { get; private set; }
        public List<ExecutionHistoryEntry> ExecutionHistory { get; private set; }
        public string UserInput { get; private set; }
        public int CurrentIteration { get; set; }
        public bool IsLastIteration => CurrentIteration >= _config.MaxIterations - 1;

        public ConversationContext(
            AgentConfiguration config,
            string userInput,
            string? initialExecutionPlan = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            UserInput = userInput ?? throw new ArgumentNullException(nameof(userInput));
            ExecutionPlan = initialExecutionPlan ?? string.Empty;
            ExecutionHistory = new List<ExecutionHistoryEntry>();
            CurrentIteration = 0;

            InitializeSystemPrompt();
        }

        private void InitializeSystemPrompt()
        {
            if (string.IsNullOrEmpty(_config.SystemMessage))
            {
                SystemPrompt = string.Empty;
                return;
            }

            SystemPrompt = _config.SystemMessage.Replace("{agentsList}", _config.AgentsDescription);

            if (_config.UseExecutionPlan && !string.IsNullOrEmpty(_config.PlannerModificationActionTemplate))
            {
                SystemPrompt += $"{Environment.NewLine}{_config.PlannerModificationActionTemplate}";
            }
        }

        public void UpdateExecutionPlan(string newPlan)
        {
            ExecutionPlan = newPlan ?? string.Empty;
        }

        public void AddExecutionEntry(string agentName, List<string> parameters, string result)
        {
            ExecutionHistory.Add(new ExecutionHistoryEntry
            {
                AgentName = agentName,
                Parameters = parameters ?? new List<string>(),
                Result = result ?? string.Empty,
                Timestamp = DateTime.UtcNow
            });
        }

        public string GetExecutionHistoryAsText()
        {
            if (!ExecutionHistory.Any())
                return "No execution history yet.";

            return string.Join(
                Environment.NewLine,
                ExecutionHistory.Select(h =>
                    $"Agent: {h.AgentName}, Params: [{string.Join(", ", h.Parameters)}], Result: {h.Result}"));
        }

        public ChatHistory ProduceChatHistory(AgentConfiguration config)
        {
            var chatHistory = new ChatHistory();

            // Add system message
            if (!string.IsNullOrEmpty(SystemPrompt))
            {
                var systemMessage = IsLastIteration
                    ? SystemPrompt.Replace("{agentsList}", "Last step so no agents available. Use 'finish' or 'cannot' action.")
                    : SystemPrompt;

                chatHistory.AddSystemMessage(systemMessage);
            }

            // Add execution plan if available
            if (!string.IsNullOrEmpty(ExecutionPlan))
            {
                var planPrefix = ExecutionHistory.Any(e => e.IsPlanModification)
                    ? "# Execution Plan (REVISED)"
                    : "# Execution Plan";

                chatHistory.AddSystemMessage($"{planPrefix}{Environment.NewLine}{ExecutionPlan}");
            }

            // Add initial user input
            chatHistory.AddUserMessage(UserInput);

            // Add execution history as conversation turns
            foreach (var entry in ExecutionHistory)
            {
                if (entry.IsPlanModification)
                {
                    chatHistory.AddUserMessage($"Plan has been revised. Reason: {entry.ModificationReason}. Continue with the new plan.");
                }
                else if (!string.IsNullOrEmpty(entry.PlannerPrompt))
                {
                    chatHistory.AddUserMessage(entry.PlannerPrompt);
                }

                if (!string.IsNullOrEmpty(entry.PlannerResponse))
                {
                    var responseWithResult = string.IsNullOrEmpty(entry.Result)
                        ? entry.PlannerResponse
                        : $"{entry.PlannerResponse}{Environment.NewLine}# Result:{entry.Result}";

                    chatHistory.AddAssistantMessage(responseWithResult);
                }
            }
            
            return chatHistory;
        }

        public string ProduceNextStepPrompt()
        {
            // Add reasoning context summary instead of simple iteration prompt
            var lastEntry = ExecutionHistory.LastOrDefault(e => !e.IsPlanModification);
            var lastAgentName = lastEntry?.AgentName ?? "none";
            var lastResult = lastEntry?.Result ?? "no result";
            var shortenedLastResult = lastResult.Length > 300
                ? lastResult.Substring(0, 300) + "..."
                : lastResult;

            // Detect if plan was revised
            var planStatus = ExecutionHistory.Any(e => e.IsPlanModification)
                ? "Revised after failure"
                : "Original";

            // Try to generate a simple "remaining goal" heuristic
            var remainingGoal = "Continue reasoning toward the final answer.";
            if (!string.IsNullOrEmpty(lastResult))
            {
                if (lastResult.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    lastResult.Contains("no result", StringComparison.OrdinalIgnoreCase))
                    remainingGoal = "Identify missing data or adjust strategy to move toward resolution.";
                else if (lastResult.Length < 50)
                    remainingGoal = "Interpret the short result and decide next logical step.";
            }

            // Build summary text
            var reasoningSummary = $@"# Reasoning Context Summary
User request: {UserInput}
Current step: {CurrentIteration + 1}/{_config.MaxIterations}
Previous agent: {lastAgentName}
Last agent result: {shortenedLastResult}
Execution plan status: {planStatus}
Outstanding goal: {remainingGoal}

Based on this, decide the next best action.";
            return reasoningSummary;
        }
    }

    public class ExecutionHistoryEntry
    {
        public string AgentName { get; set; } = "";
        public List<string> Parameters { get; set; } = new();
        public string Result { get; set; } = "";
        public string PlannerPrompt { get; set; } = "";
        public string PlannerResponse { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsPlanModification { get; set; }
        public string ModificationReason { get; set; } = "";
    }

    public interface ICompositeLoopV2Agent : IDoCallWrapperAgent
    {
        Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);
    }
}