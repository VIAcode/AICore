using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;
using System.Reflection;

namespace AiCoreApi.SemanticKernel;

public sealed class GeminiChatCompletionService : IChatCompletionService
{
    private readonly string _modelName;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly double _temperature;
    private readonly int _maxTokens;
    private readonly HttpClient _httpClient;

    public GeminiChatCompletionService(
        string modelName,
        string endpoint,
        string apiKey,
        double temperature,
        int maxTokens,
        HttpClient? httpClient = null)
    {
        _modelName = modelName;
        _endpoint = endpoint;
        _apiKey = apiKey;
        _temperature = temperature;
        _maxTokens = maxTokens;
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
        GeminiRequest? request = null;

        var messages = chatHistory.Select(ch =>
        {
            var parts = new List<GeminiPart>();
            if (!string.IsNullOrEmpty(ch.Content))
            {
                parts.Add(new GeminiPart { Text = ch.Content });
            }
            // --- Handle images (ImageContent objects) ---
            foreach (var item in ch.Items)
            {
                if (item is ImageContent img && img.DataUri != null)
                {
                    // img.DataUri = "data:image/png;base64,iVBORw0K..."
                    var dataUri = img.DataUri;
                    var mimeType = img.MimeType ?? "image/png";
                    var commaIndex = dataUri.IndexOf(",");
                    if (commaIndex != -1)
                    {
                        var base64Data = dataUri.Substring(commaIndex + 1);
                        parts.Add(new GeminiPart
                        {
                            InlineData = new GeminiInlineData
                            {
                                MimeType = mimeType,
                                Data = base64Data
                            }
                        });
                    }
                    else
                    {
                        // Log or handle the malformed dataUri case if necessary
                        // For now, we skip adding this part
                        continue;
                    }
                }
            }
            if (parts.Count == 0) return null;
            return new GeminiMessage
            {
                Role = ch.Role.Label.ToLower(),
                Parts = parts
            };
        })
        .Where(m => m != null)
        .ToList()!;

        var temperature = _temperature;
        var maxTokens = _maxTokens;
        var topP = 1.0;
        GeminiGenerationConfig? generationConfig = new()
        {
            Temperature = temperature,
            TopP = topP
        };

        if (executionSettings is Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIPromptExecutionSettings _openAIPromptExecutionSettings)
        {
            if (_openAIPromptExecutionSettings.Temperature.HasValue)
                temperature = _openAIPromptExecutionSettings.Temperature.Value;

            if (_openAIPromptExecutionSettings.TopP.HasValue)
                topP = _openAIPromptExecutionSettings.TopP.Value;

            generationConfig.Temperature = temperature;
            generationConfig.TopP = topP;
            generationConfig.MaxOutputTokens = maxTokens;

            var responseFormat = _openAIPromptExecutionSettings.ResponseFormat;
            var jsonSchemaProperty = responseFormat?.GetType().GetProperty("JsonSchema", BindingFlags.Public | BindingFlags.Instance);

            if (jsonSchemaProperty != null)
            {
                var jsonSchema = jsonSchemaProperty.GetValue(responseFormat);
                if (jsonSchema != null)
                {
                    var schemaProperty = jsonSchema.GetType().GetProperty("Schema", BindingFlags.Public | BindingFlags.Instance);
                    if (schemaProperty != null)
                    {
                        var schema = schemaProperty.GetValue(jsonSchema)?.ToString();
                        if (!string.IsNullOrEmpty(schema))
                        {
                            generationConfig.ResponseMimeType = "application/json";
                            generationConfig.ResponseSchema = JsonConvert.DeserializeObject<object>(schema);
                        }
                    }
                }
            }
        }

        var systemMessage = messages.Where(m => m.Role == "system")
            .SelectMany(m => m.Parts)
            .Select(p => p.Text)
            .FirstOrDefault();
        messages = messages.Where(m => m.Role != "system").ToList();
        
        request = new GeminiRequest
              {
                  SystemInstruction = systemMessage == null
                      ? null
                      : new GeminiMessage
                          {
                              Parts = new List<GeminiPart>
                              {
                                  new GeminiPart { Text = systemMessage }
                              }
                          },
                  Contents = messages,
                  GenerationConfig = generationConfig
              };

        var json = JsonConvert.SerializeObject(request);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);

        var response = await _httpClient.PostAsync(
            new Uri($"{_endpoint.TrimEnd('/')}/v1beta/models/{_modelName}:generateContent"),
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var geminiResponse = JsonConvert.DeserializeObject<GeminiResponse>(responseContent)
            ?? throw new InvalidOperationException("Failed to deserialize response from Gemini API.");

        var text = geminiResponse.Candidates.First().Content.Parts.First().Text;

        return new List<ChatMessageContent>
        {
            new ChatMessageContent
            {
                Role = new AuthorRole("assistant"),
                Content = text
            }
        };
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not implemented for Gemini.");
    }

    #region Gemini Models

    public class GeminiRequest
    {
        [JsonProperty("system_instruction")]
        public GeminiMessage? SystemInstruction { get; set; }

        [JsonProperty("contents")]
        public List<GeminiMessage> Contents { get; set; } = new();

        [JsonProperty("generationConfig")]
        public GeminiGenerationConfig GenerationConfig { get; set; } = new();
    }

    public class GeminiMessage
    {
        [JsonProperty("role", NullValueHandling = NullValueHandling.Ignore)]
        public string Role { get; set; } = "user";

        [JsonProperty("parts")]
        public List<GeminiPart> Parts { get; set; } = new();
    }

    public class GeminiPart
    {
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
        public string? Text { get; set; }

        [JsonProperty("inline_data", NullValueHandling = NullValueHandling.Ignore)]
        public GeminiInlineData? InlineData { get; set; }
    }

    public class GeminiInlineData
    {
        [JsonProperty("mime_type")]
        public string MimeType { get; set; } = "image/jpeg";

        [JsonProperty("data")]
        public string Data { get; set; } = "";
    }

    public class GeminiGenerationConfig
    {
        [JsonProperty("temperature")]
        public double Temperature { get; set; }

        [JsonProperty("topP")]
        public double TopP { get; set; }

        [JsonProperty("responseMimeType", NullValueHandling = NullValueHandling.Ignore)]
        public string? ResponseMimeType { get; set; }

        [JsonProperty("responseSchema", NullValueHandling = NullValueHandling.Ignore)]
        public object? ResponseSchema { get; set; }

        [JsonProperty("maxOutputTokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxOutputTokens { get; set; } = 4096;
    }

    public class GeminiResponse
    {
        [JsonProperty("candidates")]
        public List<GeminiCandidate> Candidates { get; set; } = new();
    }

    public class GeminiCandidate
    {
        [JsonProperty("content")]
        public GeminiMessage Content { get; set; } = new();
    }

    #endregion
}

public static class GeminiKernelBuilderExtensions
{
    public static IKernelBuilder AddGeminiChatCompletion(
        this IKernelBuilder builder,
        string modelName,
        string apiKey,
        string? temperature = null,
        string? maxAnswersTokens = null,
        string? serviceId = null,
        HttpClient? httpClient = null)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(modelName)) throw new ArgumentException("Model Name cannot be null or whitespace.", nameof(modelName));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key cannot be null or whitespace.", nameof(apiKey));

        int maxTokensValue = 4096;
        double temperatureValue = 0;
        if (!string.IsNullOrWhiteSpace(temperature) && double.TryParse(temperature, out var t))
        {
            temperatureValue = t;
        }
        if (!string.IsNullOrWhiteSpace(maxAnswersTokens) && int.TryParse(maxAnswersTokens, out var m))
        {
            maxTokensValue = m;
        }

        GeminiChatCompletionService Factory(IServiceProvider serviceProvider, object? _) =>
            new(modelName,
                "https://generativelanguage.googleapis.com",
                apiKey,
                temperatureValue,
                maxTokensValue,
                httpClient);

        builder.Services.AddKeyedSingleton<IChatCompletionService>(serviceId, (Func<IServiceProvider, object?, GeminiChatCompletionService>)Factory);
        return builder;
    }
}
