using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;

namespace AiCoreApi.SemanticKernel;

public sealed class CohereChatCompletionService : IChatCompletionService
{
    private readonly string _modelId;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly List<string> _connectors;
    private readonly HttpClient _httpClient;

    public CohereChatCompletionService(
            string modelId,
            Uri endpoint,
            string apiKey,
            List<string>? connectors = null,
            HttpClient? httpClient = null)
    {
        _modelId = modelId;
        _endpoint = endpoint;
        _apiKey = apiKey;
        _connectors = connectors ?? new List<string>();
        _httpClient = httpClient ?? new HttpClient();
    }

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>
    {
        { "modelId", _modelId },
        { "endpoint", _endpoint },
        { "apiKey", _apiKey },
        { "httpClient", _httpClient }
    };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var cohereMessageHistory = chatHistory.Select(ch => 
            new CohereMessageHistory
            {
                Role = ch.Role.Label.ToLower().Replace("assistant", "chatbot"),
                Message = ch.Content ?? ""
            }).ToList();
        var temperature = 0.0;
        var topP = 1.0;
        if (executionSettings is Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings openAiPromptExecutionSettings
            && openAiPromptExecutionSettings.ResponseFormat != null)
        {
            if (openAiPromptExecutionSettings.Temperature.HasValue)
                temperature = openAiPromptExecutionSettings.Temperature.Value;
            
            if (openAiPromptExecutionSettings.TopP.HasValue)
                topP = openAiPromptExecutionSettings.TopP.Value;
        }
        var message = new CohereRequestMessage
        {
            ChatHistory = cohereMessageHistory.Take(cohereMessageHistory.Count - 1).ToList(),
            Temperature = temperature,
            TopP = topP,
            Message = cohereMessageHistory.Last().Message,
            Model = _modelId,
            Connectors = _connectors,
            Stream = false
        };

        var json = JsonConvert.SerializeObject(message);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + _apiKey);
        var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var cohereResponseMessage = JsonConvert.DeserializeObject<CohereResponseMessage>(responseContent);
        if (cohereResponseMessage == null)
            throw new InvalidOperationException("Failed to deserialize response from Cohere API.");
        
        var chatMessageContent = cohereResponseMessage.ChatHistory.Select(ch =>
            new ChatMessageContent
            {
                Role = new AuthorRole(ch.Role.ToLower().Replace("chatbot", "assistant")),
                Content = ch.Message,
            }).ToList();
        return chatMessageContent.Slice(chatMessageContent.Count - 1, 1);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public class CohereRequestMessage
    {
        [JsonProperty("message")]
        public string Message { get; set; } = "";
        [JsonProperty("temperature")]
        public double Temperature { get; set; }
        [JsonProperty("top_p")]
        public double TopP { get; set; }
        [JsonProperty("chat_history")]
        public List<CohereMessageHistory> ChatHistory { get; set; } = new();
        [JsonProperty("model")]
        public string Model { get; set; } = "command-r-08-2024";
        [JsonProperty("connectors")]
        public List<string> Connectors { get; set; } = new();
        [JsonProperty("stream")]
        public bool Stream { get; set; }
        [JsonProperty("prompt_truncation")]
        public string PromptTruncation { get; set; } = "OFF";
    }

    public class CohereMessageHistory
    {
        [JsonProperty("role")]
        public string Role { get; set; } = "user";
        [JsonProperty("message")]
        public string Message { get; set; } = "";
    }

    public class CohereResponseMessage
    {
        [JsonProperty("response_id")]
        public string ResponseId { get; set; } = "";
        [JsonProperty("text")]
        public string Text { get; set; } = "";
        [JsonProperty("generation_id")]
        public string GenerationId { get; set; } = "";
        [JsonProperty("chat_history")]
        public List<CohereMessageHistory> ChatHistory { get; set; } = new();
        [JsonProperty("finish_reason")]
        public string FinishReason { get; set; } = "";
        [JsonProperty("meta")]
        public CohereResponseMessageMeta Meta { get; set; } = new();

        public class CohereResponseMessageMeta
        {
            [JsonProperty("api_version")]
            public CohereResponseMessageMetaApiVersion ApiVersion { get; set; } = new();
            [JsonProperty("billed_units")]
            public CohereResponseMessageMetaTokens BilledUnits { get; set; } = new();
            [JsonProperty("tokens")]
            public CohereResponseMessageMetaTokens Tokens { get; set; } = new();
        }

        public class CohereResponseMessageMetaTokens
        {
            [JsonProperty("input_tokens")]
            public int InputTokens { get; set; }
            [JsonProperty("output_tokens")]
            public int OutputTokens { get; set; }
        }
        public class CohereResponseMessageMetaApiVersion
        {
            [JsonProperty("version")]
            public string Version { get; set; } = "1";
        }
    }
}

public static class CohereKernelBuilderExtensions
{
    public static IKernelBuilder AddCohereChatCompletion(
        this IKernelBuilder builder,
        string modelId,
        string apiKey,
        List<string>? connectors = null,
        string? serviceId = null,
        HttpClient? httpClient = null)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("Model ID cannot be null or whitespace.", nameof(modelId));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key cannot be null or whitespace.", nameof(apiKey));

        CohereChatCompletionService Factory(IServiceProvider serviceProvider, object? _) =>
            new(modelId,
                new Uri("https://api.cohere.com/v1/chat"),
                apiKey,
                connectors,
                httpClient);

        builder.Services.AddKeyedSingleton<IChatCompletionService>(serviceId, (Func<IServiceProvider, object?, CohereChatCompletionService>)Factory);
        return builder;
    }
}
