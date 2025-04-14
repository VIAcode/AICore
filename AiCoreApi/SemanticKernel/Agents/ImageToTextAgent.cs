using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class ImageToTextAgent : BaseAgent, IImageToTextAgent
    {
        private string _debugMessageSenderName = "ImageToTextAgent";
        public static class AgentPromptPlaceholders
        {
            public const string FileDataPlaceholder = "firstFileData";
        }

        private static class AgentContentParameters
        {
            public const string Base64Image = "base64Image";
            public const string MimeType = "mimeType";
            public const string Prompt = "prompt";
            public const string SystemMessage = "systemMessage";
        }

        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;

        public ImageToTextAgent(
            ISemanticKernelProvider semanticKernelProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<ImageToTextAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _semanticKernelProvider = semanticKernelProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var base64Image = ApplyParameters(agent.Content[AgentContentParameters.Base64Image].Value, parameters);
            var mimeType = ApplyParameters(agent.Content[AgentContentParameters.MimeType].Value, parameters);
            var prompt = ApplyParameters(agent.Content[AgentContentParameters.Prompt].Value, parameters);
            var systemMessage = agent.Content.ContainsKey(AgentContentParameters.SystemMessage) ? agent.Content[AgentContentParameters.SystemMessage].Value : string.Empty;

            if (_requestAccessor.MessageDialog.Messages!.Last().HasFiles())
            {
                base64Image = ApplyParameters(base64Image, new Dictionary<string, string>
                {
                    {AgentPromptPlaceholders.FileDataPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().Files!.First().Base64Data},
                });
            }
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"Prompt:\r\n{prompt}\r\n\r\nSystem Message:\r\n{systemMessage}\r\n\r\nImage ({mimeType}):\r\n{base64Image.Length} bytes");

            var imageData = Convert.FromBase64String(base64Image.StripBase64());
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections, 
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.OpenAiLlm }, _debugMessageSenderName, agent.LlmType);
            var kernel = _semanticKernelProvider.GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            if(!string.IsNullOrEmpty(systemMessage))
                history.AddSystemMessage(systemMessage);
            history.AddSystemMessage(systemMessage);
            var message = new ChatMessageContentItemCollection
            {
                new TextContent(prompt),
                new ImageContent(imageData, mimeType)
            };
            history.AddUserMessage(message);
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = Convert.ToDouble(llmConnection.Content.ContainsKey("temperature") ? llmConnection.Content["temperature"] : 0),
            };
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            var result = resultContent.Content ?? "";

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }
    }

    public interface IImageToTextAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
