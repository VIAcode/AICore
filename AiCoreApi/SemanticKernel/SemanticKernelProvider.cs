using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;

namespace AiCoreApi.SemanticKernel
{
    public class SemanticKernelProvider : ISemanticKernelProvider
    {
        private readonly RequestAccessor _requestAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEntraTokenProvider _entraTokenProvider; 

        public SemanticKernelProvider(
            RequestAccessor requestAccessor,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            IEntraTokenProvider entraTokenProvider)
        {
            _requestAccessor = requestAccessor;
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _entraTokenProvider = entraTokenProvider;
        }

        public Kernel GetKernel(ConnectionModel connectionModel)
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClients.RetryClient);
            var kernelBuilder = Kernel.CreateBuilder();
            if (connectionModel.Type == ConnectionType.AzureOpenAiLlm)
            {
                var accessType = connectionModel.Content.ContainsKey("accessType") ? connectionModel.Content["accessType"] : "apiKey";
                if (accessType == "apiKey")
                {
                    kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(
                        connectionModel.Content["deploymentName"],
                        connectionModel.Content["endpoint"],
                        connectionModel.Content["azureOpenAiKey"],
                        httpClient: httpClient);
                }
                else
                {
                    var accessToken = Task.Run(() => _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://cognitiveservices.azure.com/.default")).GetAwaiter().GetResult();
                    kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(
                        connectionModel.Content["deploymentName"],
                        connectionModel.Content["endpoint"],
                        new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn),
                        httpClient: httpClient);
                }
            }
            else if (connectionModel.Type == ConnectionType.OpenAiLlm)
            {
                var baseUrl = connectionModel.Content.ContainsKey("baseUrl") && !string.IsNullOrEmpty(connectionModel.Content["baseUrl"])
                    ? new Uri(connectionModel.Content["baseUrl"].TrimEnd('/'))
                    : new Uri("https://api.openai.com/v1");
                kernelBuilder = kernelBuilder.AddOpenAIChatCompletion(
                    connectionModel.Content["modelName"],
                    baseUrl,
                    connectionModel.Content["apiKey"],
                    httpClient: httpClient);
            }
            else if (connectionModel.Type == ConnectionType.CohereLlm)
            {
                kernelBuilder = kernelBuilder.AddCohereChatCompletion(
                    connectionModel.Content["modelName"],
                    connectionModel.Content["apiKey"],
                    new List<string>(),
                    httpClient: httpClient);
            }
            else if (connectionModel.Type == ConnectionType.AzureOpenAiLlmCarousel)
            {
                kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(
                    nameof(ConnectionType.AzureOpenAiLlmCarousel),
                    "https://api.openai.azure.com",
                    connectionModel.Content["azureOpenAiLlmConnections"],
                    httpClient: httpClient);
            }
            else if (connectionModel.Type == ConnectionType.DeepSeekLlm)
            {
                kernelBuilder = kernelBuilder.AddDeepSeekChatCompletion(
                    connectionModel.Content["modelName"],
                    connectionModel.Content["apiKey"],
                    connectionModel.Content["temperature"],
                    httpClient: httpClient);
            }
            else if (connectionModel.Type == ConnectionType.GeminiLlm)
            {
                kernelBuilder = kernelBuilder.AddGeminiChatCompletion(
                    connectionModel.Content["modelName"],
                    connectionModel.Content["apiKey"],
                    connectionModel.Content["temperature"],
                    connectionModel.Content["maxAnswersTokens"],
                    httpClient: httpClient);
            }
            return kernelBuilder.Build();
        }

        public async Task<Kernel> GetKernel()
        {
            // Get the default LLM connection
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = connections.FirstOrDefault(conn =>
                conn.Type.IsLlmConnection() &&
                _requestAccessor.DefaultConnectionNames.Contains(conn.Name))
                    ?? connections.FirstOrDefault(conn => conn.Type.IsLlmConnection());
            if (llmConnection == null)
                throw new Exception("No any LLM connection found");

            return GetKernel(llmConnection);

        }

        public async Task<string> ExecutePrompt(ConnectionModel llmConnection, string templateText, double temperature, double topP, string systemMessage, string jsonSchema = "")
        {
            return await ExecutePrompt(llmConnection, templateText, temperature, topP, systemMessage, jsonSchema, false);
        }

        public async Task<string> ExecutePrompt(ConnectionModel llmConnection, string templateText, double temperature, double topP, string systemMessage, string jsonSchema, bool jsonSchemaIsStrict)
        {
            var kernel = GetKernel(llmConnection);
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
                TopP = topP,
            };
            if (!string.IsNullOrEmpty(jsonSchema))
            {
                var chatResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "prompt_result",
                    jsonSchema: BinaryData.FromString(jsonSchema),
                    jsonSchemaIsStrict: jsonSchemaIsStrict);
                executionSettings.ResponseFormat = chatResponseFormat;
            }
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            return resultContent.Content ?? "";
        }

        public async Task<string> ExecutePromptWithHistory(ConnectionModel llmConnection, ChatHistory history, double temperature, double topP, string jsonSchema = "", bool jsonSchemaIsStrict = false)
        {
            var kernel = GetKernel(llmConnection);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                Temperature = temperature,
                TopP = topP,
            };
            if (!string.IsNullOrEmpty(jsonSchema))
            {
                var chatResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "prompt_result",
                    jsonSchema: BinaryData.FromString(jsonSchema),
                    jsonSchemaIsStrict: jsonSchemaIsStrict);
                executionSettings.ResponseFormat = chatResponseFormat;
            }
            var resultContent = await chat.GetChatMessageContentAsync(history, executionSettings);
            return resultContent.Content ?? "";
        }
    }

    public interface ISemanticKernelProvider
    {
        Kernel GetKernel(ConnectionModel connectionModel);
        Task<Kernel> GetKernel();
        Task<string> ExecutePrompt(ConnectionModel llmConnection, string templateText, double temperature, double topP, string systemMessage, string jsonSchema = "");
        Task<string> ExecutePrompt(ConnectionModel llmConnection, string templateText, double temperature, double topP, string systemMessage, string jsonSchema, bool jsonSchemaIsStrict);
        Task<string> ExecutePromptWithHistory(ConnectionModel llmConnection, ChatHistory history, double temperature, double topP, string jsonSchema = "", bool jsonSchemaIsStrict = false);
    }
}
