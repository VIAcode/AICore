using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AudioPromptAgent : BaseAgent, IAudioPromptAgent
    {
        private string _debugMessageSenderName = "AudioPromptAgent";

        // Parameter keys in AgentModel.Content
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
            ExtendedConfig extendedConfig,
            ILogger<AudioPromptAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            // Decode HTML-encoded parameters (same logic as WhisperAgent)
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            // Read values from stored agent content or from passed-in parameters
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

            return await CallChatGptAsync(
                systemMessage,
                prompt,
                base64Audio,
                mimeType,
                voice,
                temperatureVal,
                topPVal,
                modalitiesStr,
                agent.LlmType);
        }

        private async Task<string> CallChatGptAsync(
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

            var connection = GetConnection(
                _requestAccessor,
                _responseAccessor,
                connections,
                ConnectionType.AzureOpenAiLlm,
                _debugMessageSenderName,
                connectionId);

            var openAiApiKey = connection.Content["azureOpenAiKey"];
            var openAiEndpoint = connection.Content["endpoint"];
            var modelName = connection.Content["deploymentName"];

            var requestUri = $"{openAiEndpoint}/openai/deployments/{modelName}/chat/completions?api-version=2025-01-01-preview";

            var userMessage = new
            {
                role = "user",
                content = new List<object>
                {
                    new { type = "text", text = userPrompt },
                    
                }
            };
            if (!string.IsNullOrEmpty(base64Audio))
            {
                userMessage.content.Add(new
                {
                    type = "input_audio",
                    input_audio = new
                    {
                        data = base64Audio,
                        format = DetermineAudioFormat(mimeType)
                    }
                });
            }
            var modalitiesArray = ParseModalities(modalities);
            var requestBody = new
            {
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = new []
                        {
                            new { type = "text", text = systemMessage }
                        }
                    },
                    userMessage
                },
                temperature = temperature,
                top_p = topP,
                frequency_penalty = 0,
                presence_penalty = 0,
                max_tokens = 5000,
                stop = (string)null,
                modalities = modalitiesArray,
                audio = new
                {
                    voice = string.IsNullOrWhiteSpace(voice) ? "alloy" : voice,
                    format = "wav"
                },
                stream = false
            };

            var jsonRequest = JsonSerializer.Serialize(requestBody);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"URI: {requestUri}\r\nBody: {jsonRequest}");

            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Add("api-key", openAiApiKey);
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            // Submit POST request
            using var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(requestUri, content);

            if (!response.IsSuccessStatusCode)
            {
                _responseAccessor.AddDebugMessage(
                    _debugMessageSenderName,
                    "DoCall Error",
                    $"Failed to get chat completion with audio. Status code: {response.StatusCode}");
                throw new Exception("Failed to get chat completion with audio.");
            }

            var result = string.Empty;
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseElement = JsonDocument.Parse(responseContent).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("audio");
            if (modalitiesArray.Contains("text"))
            {
                result = responseElement.GetProperty("transcript").GetString();
            }
            if (modalitiesArray.Contains("audio"))
            {
                var audio = responseElement.GetProperty("data").GetString();
                _responseAccessor.CurrentMessage.AddFile("audio.mp3", audio);
            }
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", responseContent);
            return result;
        }

        private static string DetermineAudioFormat(string mimeType)
        {
            return mimeType switch
            {
                "audio/mpeg" => "mp3",
                "audio/wav" => "wav",
                _ => "mp3"
            };
        }

        private static string[] ParseModalities(string modalities)
        {
            if (string.IsNullOrWhiteSpace(modalities))
            {
                return new[] { "text", "audio" };
            }

            return modalities
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim())
                .ToArray();
        }
    }

    public interface IAudioPromptAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
