using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;
using System.Reflection;

namespace AiCoreApi.SemanticKernel;

public sealed class DeepSeekChatCompletionService : IChatCompletionService
{
    private readonly string _modelName;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly double _temperature;
    private readonly HttpClient _httpClient;

    public DeepSeekChatCompletionService(
            string modelName,
            string endpoint,
            string apiKey,
            double temperature,
            HttpClient? httpClient = null)
    {
        _modelName = modelName;
        _endpoint = endpoint;
        _apiKey = apiKey;
        _temperature = temperature;
        _httpClient = httpClient ?? new HttpClient();
    }

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>
    {
        { "modelId", _modelName },
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
        DeepSeekResponseFormat? outputFormat = null;
        var messages = chatHistory.Select(ch =>
            new DeepSeekMessage
            {
                Role = ch.Role.Label.ToLower(),
                Content = ch.Content ?? ""
            }).ToList();
        var temperature = _temperature;
        var topP = 1.0;
        if (executionSettings is Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings _openAIPromptExecutionSettings 
            && _openAIPromptExecutionSettings.ResponseFormat != null)
        {
            if(_openAIPromptExecutionSettings.Temperature.HasValue)
                temperature = _openAIPromptExecutionSettings.Temperature.Value;
            
            if (_openAIPromptExecutionSettings.TopP.HasValue)
                topP = _openAIPromptExecutionSettings.TopP.Value;
            
            var responseFormat = _openAIPromptExecutionSettings.ResponseFormat;
            var jsonSchemaProperty = responseFormat.GetType().GetProperty("JsonSchema", BindingFlags.Public | BindingFlags.Instance);
            if (jsonSchemaProperty != null)
            {
                var jsonSchema = jsonSchemaProperty.GetValue(responseFormat);
                if (jsonSchema != null)
                {
                    var schemaProperty = jsonSchema.GetType().GetProperty("Schema", BindingFlags.Public | BindingFlags.Instance);
                    if (schemaProperty != null)
                    {
                        var schema = schemaProperty.GetValue(jsonSchema);
                        if (schema != null)
                        {
                            var schemaText = schema.ToString();
                            if (!string.IsNullOrEmpty(schemaText))
                            {
                                var jsonObjectContent = schemaText.Contains("\"type\": \"object\"")
                                    ? $"JSON OUTPUT SCHEMA: {schemaText}"
                                    : $"JSON OUTPUT SAMPLE: {schemaText}";
                                outputFormat = new DeepSeekResponseFormat { Type = "json_object" };
                                messages.Insert(0, new DeepSeekMessage
                                {
                                    Role = "system",
                                    Content = jsonObjectContent
                                });
                            }
                        }
                    }
                }
            }
        }
        var message = new DeepSeekRequestMessage
        {
            Temperature = temperature,
            TopP = topP,
            Messages = messages,
            Model = _modelName,
            ResponseFormat = outputFormat,
            Stream = false
        };

        var json = JsonConvert.SerializeObject(message);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        _httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + _apiKey);
        var response = await _httpClient.PostAsync(new Uri($"{_endpoint.TrimEnd('/')}/chat/completions"), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var deepSeekResponseMessage = JsonConvert.DeserializeObject<DeepSeekResponseMessage>(responseContent);
        if (deepSeekResponseMessage == null)
            throw new InvalidOperationException("Failed to deserialize response from DeepSeek API.");

        var responseMessage = deepSeekResponseMessage.Choices.First().Message;
        var chatMessageContent = new ChatMessageContent
        {
            Role = new AuthorRole(responseMessage.Role.ToLower()),
            Content = responseMessage.Content,
        };
        return new List<ChatMessageContent> { chatMessageContent };
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public class DeepSeekRequestMessage
    {
        [JsonProperty("temperature")]
        public double Temperature { get; set; }
        [JsonProperty("top_p")]
        public double TopP { get; set; }
        [JsonProperty("messages")]
        public List<DeepSeekMessage> Messages { get; set; } = new();
        [JsonProperty("model")]
        public string Model { get; set; } = "deepseek-chat";
        [JsonProperty("response_format", NullValueHandling = NullValueHandling.Ignore)]
        public DeepSeekResponseFormat? ResponseFormat { get; set; }
        [JsonProperty("stream")]
        public bool Stream { get; set; }
    }

    public class DeepSeekResponseFormat
    {
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
        public string? Type { get; set; }
    }

    public class DeepSeekMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = "user";
        [JsonProperty("content")]
        public string Content { get; set; } = "";
    }

    public class DeepSeekResponseMessage
    {
        [JsonProperty("id")]
        public string ResponseId { get; set; } = "";
        [JsonProperty("choices")]
        public List<DeepSeekResponseChoice> Choices { get; set; } = new();
        [JsonProperty("model")]
        public string Model { get; set; } = "";
        [JsonProperty("usage")]
        public DeepSeekResponseUsage Usage { get; set; } = new();

        public class DeepSeekResponseUsage
        {
            [JsonProperty("completion_tokens")]
            public int CompletionTokens { get; set; }
            [JsonProperty("prompt_tokens")]
            public int PromptTokens { get; set; }
            [JsonProperty("total_tokens")]
            public int TotalTokens { get; set; }
        }

        public class DeepSeekResponseChoice
        {
            [JsonProperty("index")]
            public int CompletionTokens { get; set; }
            [JsonProperty("finish_reason")]
            public string FinishReason { get; set; } = "";
            [JsonProperty("message")]
            public DeepSeekMessage Message { get; set; } = new();
        }
    }
}

public static class DeepSeekKernelBuilderExtensions
{
    public static IKernelBuilder AddDeepSeekChatCompletion(
        this IKernelBuilder builder,
        string modelName,
        string apiKey,
        string? temperature = null,
        string? serviceId = null,
        HttpClient? httpClient = null)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Model Name cannot be null or whitespace.", nameof(modelName));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key cannot be null or whitespace.", nameof(apiKey));

        if (!string.IsNullOrWhiteSpace(temperature) && double.TryParse(temperature, out var temperatureValue)) { }
        else
        {
            temperatureValue = 0;
        }
        DeepSeekChatCompletionService Factory(IServiceProvider serviceProvider, object? _) =>
            new(modelName,
                "https://api.deepseek.com",
                apiKey,
                temperatureValue,
                httpClient);

        builder.Services.AddKeyedSingleton<IChatCompletionService>(serviceId, (Func<IServiceProvider, object?, DeepSeekChatCompletionService>)Factory);
        return builder;
    }
}
