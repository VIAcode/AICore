using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents;

public class AudioPromptAgent : BaseAgent, IAudioPromptAgent
{
    private string _debugMessageSenderName = "AudioPromptAgent";

    private static class AgentContentParameters
    {
        public const string Prompt = "prompt";
        public const string Base64Audio = "base64Audio";
        public const string MimeType = "mimeType";
        public const string SystemMessage = "systemMessage";
        public const string Voice = "voice";
        public const string Temperature = "temperature";
        public const string TopP = "top_p";
        public const string Modalities = "modalities";
    }

    private readonly IConnectionProcessor _connectionProcessor;
    private readonly RequestAccessor _requestAccessor;
    private readonly ResponseAccessor _responseAccessor;
    private readonly IHttpClientFactory _httpClientFactory;

    public AudioPromptAgent(
        IConnectionProcessor connectionProcessor,
        RequestAccessor requestAccessor,
        ResponseAccessor responseAccessor,
        IHttpClientFactory httpClientFactory,
        MonitoringConfig monitoringConfig,
        ILogger<AudioPromptAgent> logger)
        : base(responseAccessor, requestAccessor, monitoringConfig, logger)
    {
        _connectionProcessor = connectionProcessor;
        _requestAccessor = requestAccessor;
        _responseAccessor = responseAccessor;
        _httpClientFactory = httpClientFactory;
    }

    public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
    {
        parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
        _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

        var prompt = ApplyParameters(agent.Content[AgentContentParameters.Prompt].Value, parameters);
        var base64Audio = ApplyParameters(agent.Content[AgentContentParameters.Base64Audio].Value, parameters);
        var mimeType = ApplyParameters(agent.Content[AgentContentParameters.MimeType].Value, parameters);
        var systemMessage = ApplyParameters(agent.Content[AgentContentParameters.SystemMessage].Value, parameters);
        var voice = ApplyParameters(agent.Content[AgentContentParameters.Voice].Value, parameters);
        var temperatureStr = ApplyParameters(agent.Content[AgentContentParameters.Temperature].Value, parameters);
        var topPStr = ApplyParameters(agent.Content[AgentContentParameters.TopP].Value, parameters);
        var modalitiesStr = ApplyParameters(agent.Content[AgentContentParameters.Modalities].Value, parameters);

        if (mimeType.Contains("webm") && !string.IsNullOrEmpty(base64Audio))
        {
            base64Audio = Audio.ConvertWebmBase64ToWavBase64(base64Audio);
            mimeType = "audio/wav";
        }

        double.TryParse(temperatureStr, out var temperatureVal);
        double.TryParse(topPStr, out var topPVal);

        return await CallLlmAsync(systemMessage, prompt, base64Audio, mimeType, voice,
            temperatureVal, topPVal, modalitiesStr, agent.LlmType);
    }

    private async Task<string> CallLlmAsync(
        string systemMessage,
        string userPrompt,
        string base64Audio,
        string mimeType,
        string voice,
        double temperature,
        double topP,
        string modalities,
        int? connectionId)
    {
        var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
        var connection = GetConnection(_requestAccessor, _responseAccessor, connections,
            new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.GeminiLlm }, _debugMessageSenderName, connectionId);

        return connection.Type switch
        {
            ConnectionType.AzureOpenAiLlm => await CallOpenAiLikeLlm(systemMessage, userPrompt, base64Audio, mimeType, voice, temperature, topP, modalities, connection, isAzure: true),
            ConnectionType.OpenAiLlm => await CallOpenAiLikeLlm(systemMessage, userPrompt, base64Audio, mimeType, voice, temperature, topP, modalities, connection, isAzure: false),
            ConnectionType.GeminiLlm => await CallGemini(systemMessage, userPrompt, base64Audio, mimeType, voice, temperature, topP, modalities, connection),
            _ => throw new NotSupportedException($"Unsupported LLM type: {connection.Type}")
        };
    }

    private async Task<string> CallOpenAiLikeLlm(
        string systemMessage,
        string userPrompt,
        string base64Audio,
        string mimeType,
        string voice,
        double temperature,
        double topP,
        string modalities,
        ConnectionModel connection,
        bool isAzure)
    {
        var (requestUri, headers) = BuildOpenAiRequestUri(connection, isAzure);
        var modelName = isAzure ? null : connection.Content["modelName"];
        var requestBody = BuildOpenAiRequest(systemMessage, userPrompt, base64Audio, mimeType, voice, temperature, topP, modalities, modelName);

        var jsonResponse = await ExecuteHttpPostAsync(requestUri, requestBody, headers);

        return ExtractAudioAndTranscript(jsonResponse, ParseModalities(modalities));
    }

    private (string Uri, Dictionary<string, string> Headers) BuildOpenAiRequestUri(ConnectionModel connection, bool isAzure)
    {
        if (isAzure)
        {
            return (
                $"{connection.Content["endpoint"]}/openai/deployments/{connection.Content["deploymentName"]}/chat/completions?api-version=2025-01-01-preview",
                new() { { "api-key", connection.Content["azureOpenAiKey"] } }
            );
        }

        return (
            "https://api.openai.com/v1/chat/completions",
            new() { { "Authorization", $"Bearer {connection.Content["apiKey"]}" } }
        );
    }

    private object BuildOpenAiRequest(
        string systemMessage,
        string userPrompt,
        string base64Audio,
        string mimeType,
        string voice,
        double temperature,
        double topP,
        string modalities,
        string? modelName = null)
    {
        var modalitiesArray = ParseModalities(modalities);
        var userContent = new List<object> { new { type = "text", text = userPrompt } };

        if (!string.IsNullOrEmpty(base64Audio))
        {
            userContent.Add(new
            {
                type = "input_audio",
                input_audio = new { data = base64Audio, format = DetermineAudioFormat(mimeType) }
            });
        }

        var body = new Dictionary<string, object>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = new [] { new { type = "text", text = systemMessage } } },
                new { role = "user", content = userContent }
            },
            ["temperature"] = temperature,
            ["top_p"] = topP,
            ["max_tokens"] = 5000,
            ["modalities"] = modalitiesArray,
            ["audio"] = new
            {
                voice = string.IsNullOrWhiteSpace(voice) ? "alloy" : voice,
                format = "wav"
            },
            ["stream"] = false
        };

        if (!string.IsNullOrEmpty(modelName))
            body["model"] = modelName;

        return body;
    }


    private string ExtractAudioAndTranscript(string jsonResponse, string[] modalities)
    {
        var audioNode = JsonDocument.Parse(jsonResponse)
            .RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("audio");

        string result = modalities.Contains("text") ? audioNode.GetProperty("transcript").GetString() ?? "" : "";
        if (modalities.Contains("audio"))
        {
            var audio = audioNode.GetProperty("data").GetString();
            _responseAccessor.CurrentMessage.AddFile("audio.mp3", audio);
        }

        return result;
    }


    private async Task<string> CallGemini(
        string systemMessage,
        string userPrompt,
        string base64Audio,
        string mimeType,
        string voice,
        double temperature,
        double topP,
        string modalities,
        ConnectionModel connection)
    {
        var requestUri =
            $"https://generativelanguage.googleapis.com/v1beta/models/{connection.Content["modelName"]}:generateContent?key={connection.Content["apiKey"]}";

        var contents = new List<object>
        {
            new { role = "user", parts = new[] { new { text = userPrompt } } }
        };

        if (!string.IsNullOrEmpty(base64Audio))
        {
            contents.Add(new
            {
                role = "user",
                parts = new[]
                {
                    new { inlineData = new { mimeType, data = base64Audio } }
                }
            });
        }

        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = systemMessage } } },
            contents,
            generationConfig = new { temperature, topP, maxOutputTokens = 4096 }
        };

        var jsonResponse = await ExecuteHttpPostAsync(requestUri, requestBody);

        var candidates = JsonDocument.Parse(jsonResponse).RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0) return "";

        return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<string> ExecuteHttpPostAsync(string uri, object requestBody, Dictionary<string, string>? headers = null)
    {
        var jsonRequest = JsonSerializer.Serialize(requestBody);
        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Request", $"URI: {uri}\n{jsonRequest}");

        var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
        headers?.ToList().ForEach(h => httpClient.DefaultRequestHeaders.Add(h.Key, h.Value));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(uri, content);

        var responseContent = await response.Content.ReadAsStringAsync();
        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Response", responseContent);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Request failed: {response.StatusCode}");
        }

        return responseContent;
    }

    private static string DetermineAudioFormat(string mimeType) =>
        mimeType switch
        {
            "audio/mpeg" => "mp3",
            "audio/wav" => "wav",
            _ => "mp3"
        };

    private static string[] ParseModalities(string modalities) =>
        string.IsNullOrWhiteSpace(modalities)
            ? new[] { "text", "audio" }
            : modalities.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(m => m.Trim())
                        .ToArray();
}

public interface IAudioPromptAgent
{
    Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
}