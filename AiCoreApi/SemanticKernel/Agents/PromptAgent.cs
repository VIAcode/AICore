using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using Microsoft.KernelMemory.AI;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class PromptAgent : BaseAgent, IPromptAgent
    {
        private string _debugMessageSenderName = "PromptAgent";
        public static class AgentPromptPlaceholders
        {
            public const string HasFilesPlaceholder = "hasFiles";
            public const string FilesNamesPlaceholder = "filesNames";
            public const string FilesDataPlaceholder = "filesData";
        }

        private static class AgentContentParameters
        {
            public const string Prompt = "prompt";
            public const string OutputType = "outputType";
            public const string JsonSchema = "jsonSchema";
            public const string SystemMessage = "systemMessage";
            public const string StrictMode = "strictMode";
            public const string Temperature = "temperature";
            public const string TopP = "top_p";
        }

        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public PromptAgent(
            IBaseAgentHelper baseAgentHelper,
            ISemanticKernelProvider semanticKernelProvider,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<PromptAgent> logger) : base(baseAgentHelper, logger)
        {
            _semanticKernelProvider = semanticKernelProvider;
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var templateText = GetParameterValue(AgentContentParameters.Prompt);
            templateText = ApplyParameters(templateText, new Dictionary<string, string>
            {
                {AgentPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().HasFiles().ToString()},
                {AgentPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileContents()},
                {AgentPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileNames()}
            });
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", templateText);

            var outputType = GetParameterValue(AgentContentParameters.OutputType);
            var jsonSchema = GetParameterValue(AgentContentParameters.JsonSchema);
            var systemMessage = GetParameterValue(AgentContentParameters.SystemMessage);
            var strictMode = !agent.Content.ContainsKey(AgentContentParameters.StrictMode) || agent.Content[AgentContentParameters.StrictMode].Value == "true";

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[]
                {
                    ConnectionType.AzureOpenAiLlm, 
                    ConnectionType.OpenAiLlm,
                    ConnectionType.CohereLlm, 
                    ConnectionType.AzureOpenAiLlmCarousel, 
                    ConnectionType.DeepSeekLlm,
                    ConnectionType.GeminiLlm,
                }, _debugMessageSenderName, agent.LlmType);

            var temperature = llmConnection.Content.ContainsKey("temperature") ? Convert.ToDouble(llmConnection.Content["temperature"]) : 0;
            if (agent.Content.ContainsKey(AgentContentParameters.Temperature))
            {
                var isCorrect = double.TryParse(agent.Content[AgentContentParameters.Temperature].Value, out var agentTemperature);
                if (isCorrect)
                    temperature = agentTemperature;
            }

            var topP = (double)0;
            if (agent.Content.ContainsKey(AgentContentParameters.TopP))
            {
                var isCorrect = double.TryParse(agent.Content[AgentContentParameters.TopP].Value, out var agentTopP);
                if (isCorrect)
                    topP = agentTopP;
            }

            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            if (!string.IsNullOrEmpty(systemMessage))
                history.AddSystemMessage(systemMessage);
            if (llmConnection.Content.ContainsKey("maxRequestTokens") && Int32.TryParse(llmConnection.Content["maxRequestTokens"], out var maxRequestTokens))
            {
                var requestTokensCount = new O200KTokenizer().CountTokens(templateText); // Default Tokenizer for gpt-4o-* models
                if (requestTokensCount > maxRequestTokens - 4000)
                {
                    // take the first maxRequestTokens - 4000 tokens
                    var tokens = new O200KTokenizer().GetTokens(templateText);
                    var tokensToTake = maxRequestTokens - 4000;
                    var tokensToTakeList = tokens.Take(tokensToTake).ToList();
                    var tokensToTakeString = string.Join(" ", tokensToTakeList);
                    templateText = tokensToTakeString;
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"Request tokens count: {requestTokensCount}, maxRequestTokens: {maxRequestTokens}. Template text was truncated.");
                }
            }

            var message = new ChatMessageContentItemCollection
            {
                new TextContent(templateText),
            };
            history.AddUserMessage(message);
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
                TopP = topP,
            };


            if (outputType == "json")
            {
                var chatResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "prompt_result",
                    jsonSchema: BinaryData.FromString(jsonSchema),
                    jsonSchemaIsStrict: strictMode);
                executionSettings.ResponseFormat = chatResponseFormat;
            }
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            var result = resultContent.Content ?? "";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        public async Task<string> Prompt(string prompt, double temperature = 0, string connectionName = "")
        {
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[]
                {
                    ConnectionType.AzureOpenAiLlm, 
                    ConnectionType.OpenAiLlm, 
                    ConnectionType.CohereLlm, 
                    ConnectionType.AzureOpenAiLlmCarousel, 
                    ConnectionType.DeepSeekLlm,
                    ConnectionType.GeminiLlm,
                }, _debugMessageSenderName, connectionName: connectionName);
            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            var message = new ChatMessageContentItemCollection
            {
                new TextContent(prompt),
            };
            history.AddUserMessage(message);
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
            };
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            var result = resultContent.Content ?? "";
            return result;
        }
    }

    public interface IPromptAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> Prompt(string prompt, double temperature = 0, string connectionName = "");
    }
}
