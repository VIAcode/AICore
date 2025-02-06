using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;

namespace AiCoreApi.SemanticKernel
{
    public class SemanticKernelProvider : ISemanticKernelProvider
    {
        private readonly RequestAccessor _requestAccessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public SemanticKernelProvider(
            RequestAccessor requestAccessor,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory)
        {
            _requestAccessor = requestAccessor;
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
        }

        public Kernel GetKernel(ConnectionModel connectionModel)
        {
            var httpClient = _httpClientFactory.CreateClient("RetryClient");
            var kernelBuilder = Kernel.CreateBuilder();
            if (connectionModel.Type == ConnectionType.AzureOpenAiLlm)
            {
                kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(
                    connectionModel.Content["deploymentName"],
                    connectionModel.Content["endpoint"],
                    connectionModel.Content["azureOpenAiKey"],
                    httpClient: httpClient);
            }
            else if (connectionModel.Type == ConnectionType.OpenAiLlm)
            {
                kernelBuilder = kernelBuilder.AddOpenAIChatCompletion(
                    connectionModel.Content["modelName"],
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
            return kernelBuilder.Build();
        }

        public async Task<Kernel> GetKernel()
        {
            // Get the default LLM connection
            var connections = await _connectionProcessor.List();
            var llmConnection = connections.FirstOrDefault(conn =>
                conn.Type.IsLlmConnection() &&
                _requestAccessor.DefaultConnectionNames.Contains(conn.Name))
                    ?? connections.FirstOrDefault(conn => conn.Type.IsLlmConnection());
            if (llmConnection == null)
                throw new Exception("No any LLM connection found");

            return GetKernel(llmConnection);

        }
    }

    public interface ISemanticKernelProvider
    {
        Kernel GetKernel(ConnectionModel connectionModel);
        Task<Kernel> GetKernel();
    }
}
