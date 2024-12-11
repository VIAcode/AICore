using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class PromptAgent : BaseAgent, IPromptAgent
    {
        private const string DebugMessageSenderName = "PromptAgent";
        public static class AgentPromptPlaceholders
        {
            public const string HasFilesPlaceholder = "hasFiles";
            public const string FilesNamesPlaceholder = "filesNames";
            public const string FilesDataPlaceholder = "filesData";
        }

        private static class AgentContentParameters
        {
            public const string Prompt = "prompt";
            public const string SystemMessage = "systemMessage";
            public const string Temperature = "temperature";
        }

        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ILogger<PromptAgent> _logger;

        public PromptAgent(
            ISemanticKernelProvider semanticKernelProvider,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<PromptAgent> logger)
        {
            _semanticKernelProvider = semanticKernelProvider;
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _logger = logger;
        }
        
        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var templateText = ApplyParameters(agent.Content[AgentContentParameters.Prompt].Value, parameters);
            templateText = ApplyParameters(templateText, new Dictionary<string, string>
            {
                {AgentPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().HasFiles().ToString()},
                {AgentPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileContents()},
                {AgentPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileNames()}
            });
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Request", templateText);

            var systemMessage = agent.Content.ContainsKey(AgentContentParameters.SystemMessage) ? agent.Content[AgentContentParameters.SystemMessage].Value : string.Empty;
            var temperature = agent.Content.ContainsKey(AgentContentParameters.Temperature) ? Convert.ToDouble(agent.Content[AgentContentParameters.Temperature].Value) : 0;

            var connections = await _connectionProcessor.List();
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureOpenAiLlm, DebugMessageSenderName, agent.LlmType);

            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            if (!string.IsNullOrEmpty(systemMessage))
                history.AddSystemMessage(systemMessage);
            var message = new ChatMessageContentItemCollection
            {
                new TextContent(templateText),
            };
            history.AddUserMessage(message);
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
            };
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            var result = resultContent.Content ?? "";
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Response", result);
            _logger.LogInformation("{Login}, Action:{Action}, ConnectionName: {ConnectionName}, Incoming Text Length: {Incoming}, Outgoing Text Length: {Outgoing}",
                _requestAccessor.Login, "Prompt", llmConnection.Name, templateText.Length, result.Length);
            return result;
        }

        public async Task<string> Prompt(string prompt, double temperature = 0, string connectionName = "")
        {
            var connections = await _connectionProcessor.List();
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureOpenAiLlm, DebugMessageSenderName, connectionName: connectionName);
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