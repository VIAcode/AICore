using AiCoreApi.Models.DbModels;
using Microsoft.KernelMemory;

namespace AiCoreApi.Common.KernelMemory
{
    public sealed class KernelMemoryProvider : IKernelMemoryProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Config _config;
        private readonly ExtendedConfig _extendedConfig;

        public KernelMemoryProvider(
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            Config config,
            ExtendedConfig extendedConfig)
            
        {
            _serviceProvider = serviceProvider;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _extendedConfig = extendedConfig;
        }

        public IKernelMemory GetKernelMemory(ConnectionModel llmConnection, ConnectionModel embeddingConnection, ConnectionModel? vectorDbConnection)
        {
            var httpContext = _serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext;
            var serviceProvider = httpContext?.RequestServices ?? _serviceProvider;

            var qdConfig = new QdrantConfig { Endpoint = _config.QdrantUrl };
            var httpClient = _httpClientFactory.CreateClient(HttpClients.RetryClient);

            var searchClientConfig = new SearchClientConfig
            {
                EmptyAnswer = _extendedConfig.NoInformationFoundText,
                AnswerTokens = Convert.ToInt32(llmConnection.Content.ContainsKey("maxAnswersTokens") ? llmConnection.Content["maxAnswersTokens"] : "4096"),
                Temperature = Convert.ToDouble(llmConnection.Content.ContainsKey("temperature") ? llmConnection.Content["temperature"] : "0.5"),
            };

            var memoryBuilder = new KernelMemoryBuilder()
                .WithCustomPromptProvider(new MarkdownPromptProvider(serviceProvider))
                .WithSearchClientConfig(searchClientConfig);

            // Add AzureOpenAI or OpenAI text generation
            if (llmConnection.Type == ConnectionType.AzureOpenAiLlm)
                memoryBuilder = memoryBuilder.WithAzureOpenAITextGeneration(GetAzureOpenAIConfig(llmConnection), httpClient: httpClient);
            if (llmConnection.Type == ConnectionType.OpenAiLlm)
                memoryBuilder = memoryBuilder.WithOpenAITextGeneration(GetOpenAIConfig(llmConnection), httpClient: httpClient);
            if (llmConnection.Type == ConnectionType.DeepSeekLlm)
                memoryBuilder = memoryBuilder.WithCustomTextGenerator(new EmptyTextGenerator()); // DeepSeek does not support text generation in KernelMemory yet, use EmptyTextGenerator to avoid errors
            if (llmConnection.Type == ConnectionType.GeminiLlm)
                memoryBuilder = memoryBuilder.WithCustomTextGenerator(new EmptyTextGenerator()); // Gemini does not support text generation in KernelMemory yet, use EmptyTextGenerator to avoid errors
            if (llmConnection.Type == ConnectionType.CohereLlm)
                memoryBuilder = memoryBuilder.WithCustomTextGenerator(new EmptyTextGenerator()); // Cohere does not support text generation in KernelMemory yet, use EmptyTextGenerator to avoid errors


            // Add AzureOpenAI or OpenAI embedding generation
            if (embeddingConnection.Type == ConnectionType.AzureOpenAiEmbedding)
                memoryBuilder = memoryBuilder.WithAzureOpenAITextEmbeddingGeneration(GetAzureOpenAiEmbeddingConfig(embeddingConnection), httpClient: httpClient);
            if (embeddingConnection.Type == ConnectionType.OpenAiEmbedding)
                memoryBuilder = memoryBuilder.WithOpenAITextEmbeddingGeneration(GetOpenAiEmbeddingConfig(embeddingConnection), httpClient: httpClient);

            if (vectorDbConnection != null)
            {
                var aiSearchConfig = new AzureAISearchConfig
                {
                    Endpoint = $"https://{vectorDbConnection.Content["resourceName"]}.search.windows.net",
                    APIKey = vectorDbConnection.Content["apiKey"],
                    UseHybridSearch = vectorDbConnection.Content.ContainsKey("useHybridSearch") && vectorDbConnection.Content["useHybridSearch"].ToLower() == "true",
                    Auth = AzureAISearchConfig.AuthTypes.APIKey
                };
                memoryBuilder = memoryBuilder.WithAzureAISearchMemoryDb(aiSearchConfig);
            }
            else
            {
                memoryBuilder = memoryBuilder.WithQdrantMemoryDb(qdConfig);
            }
            return memoryBuilder.Build<MemoryServerless>();
        }

        private AzureOpenAIConfig GetAzureOpenAIConfig(ConnectionModel llmConnection) => new()
        {
            Auth = AzureOpenAIConfig.AuthTypes.APIKey,
            APIKey = llmConnection.Content["azureOpenAiKey"],
            Endpoint = llmConnection.Content["endpoint"],
            Deployment = llmConnection.Content["deploymentName"],
            MaxTokenTotal = Convert.ToInt32(llmConnection.Content["maxRequestTokens"]),
            MaxRetries = 5,
            APIType = AzureOpenAIConfig.APITypes.ChatCompletion
        };

        private OpenAIConfig GetOpenAIConfig(ConnectionModel llmConnection) => new()
        {
            APIKey = llmConnection.Content["apiKey"],
            TextModel = llmConnection.Content["modelName"],
            TextModelMaxTokenTotal = Convert.ToInt32(llmConnection.Content["maxRequestTokens"]),
            MaxRetries = 5,
        };

        private AzureOpenAIConfig GetAzureOpenAiEmbeddingConfig(ConnectionModel embeddingConnection) => new()
        {
            Auth = AzureOpenAIConfig.AuthTypes.APIKey,
            APIKey = embeddingConnection.Content["apiKey"],
            Endpoint = embeddingConnection.Content["endpoint"],
            Deployment = embeddingConnection.Content["modelName"],
            MaxTokenTotal = Convert.ToInt32(embeddingConnection.Content["maxTokens"])
        };
        private OpenAIConfig GetOpenAiEmbeddingConfig(ConnectionModel embeddingConnection) => new()
        {
            APIKey = embeddingConnection.Content["apiKey"],
            EmbeddingModel = embeddingConnection.Content["modelName"],
            EmbeddingModelMaxTokenTotal = Convert.ToInt32(embeddingConnection.Content["maxTokens"]),
            MaxRetries = 10,
        };
    }

    public interface IKernelMemoryProvider
    {
        IKernelMemory GetKernelMemory(ConnectionModel llmConnection, ConnectionModel embeddingConnection, ConnectionModel? vectorDbConnection);
    }
}
