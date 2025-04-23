using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AzureAiSpeechCreateSpeechAgent : BaseAgent, IAzureAiSpeechCreateSpeechAgent
    {
        private string _debugMessageSenderName = "AzureAiSpeechCreateSpeechAgent";

        private static class AgentContentParameters
        {
            public const string SpeechConnectionName = "speechConnectionName";
            public const string Quality = "quality";
            public const string Voice = "voice";
            public const string Text = "text";
        }
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public AzureAiSpeechCreateSpeechAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<AzureAiSpeechCreateSpeechAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var voice = ApplyParameters(agent.Content[AgentContentParameters.Voice].Value, parameters);
            var text = ApplyParameters(agent.Content[AgentContentParameters.Text].Value, parameters);
            var speechConnectionName = agent.Content[AgentContentParameters.SpeechConnectionName].Value;
            var quality = agent.Content[AgentContentParameters.Quality].Value;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall", $"Connection: {speechConnectionName}\r\nVoice: {voice}\r\nText: {text}");
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var speechConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiSpeech, _debugMessageSenderName, connectionName: speechConnectionName);
            var region = speechConnection.Content["region"];
            var apiKey = speechConnection.Content["apiKey"];

            var endpoint = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1";

            var accessToken = await GetAccessToken(apiKey, region);

            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", region);
            httpClient.DefaultRequestHeaders.Add("X-Microsoft-OutputFormat", quality);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "AI Core");

            var voiceParts = voice.Split(';');
            var voiceName = voiceParts[0];
            var voiceGender = voiceParts.Length > 1 ? voiceParts[1] : "";
            var voiceLocale = voiceParts.Length > 2 && Regex.IsMatch(voiceParts[2], @"^[a-z]{2}-[A-Z]{2}$") ? voiceParts[2] : ""; 
            var voiceStyle = voiceParts.Length > 2 && string.IsNullOrEmpty(voiceLocale) ? voiceParts[2] : "";
            if (string.IsNullOrEmpty(voiceLocale))
            {
                var match = Regex.Match(voiceName, @"^([a-z]{2}-[A-Z]{2})");
                if (match.Success)
                {
                    voiceLocale = match.Groups[1].Value;
                }
            }

            var requestBody = new System.Text.StringBuilder();
            requestBody.Append($"<speak version='1.0'");
            if (!string.IsNullOrEmpty(voiceLocale))
            {
                requestBody.Append($" xml:lang='{voiceLocale}'");
            }
            requestBody.Append(">");
            requestBody.Append($"<voice");
            if (!string.IsNullOrEmpty(voiceStyle))
            {
                requestBody.Append($" style='{voiceStyle}'");
            }
            if (!string.IsNullOrEmpty(voiceLocale))
            {
                requestBody.Append($" xml:lang='{voiceLocale}'");
            }
            if (!string.IsNullOrEmpty(voiceGender))
            {
                requestBody.Append($" xml:gender='{voiceGender}'");
            }
            if (!string.IsNullOrEmpty(voiceName))
            {
                requestBody.Append($" name='{voiceName}'");
            }
            requestBody.Append(">");
            requestBody.Append(text);
            requestBody.Append("</voice></speak>");
            var content = new StringContent(requestBody.ToString(), System.Text.Encoding.UTF8, "application/ssml+xml");

            var response = await httpClient.PostAsync(endpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Error", $"Failed to generate the audio file. Status code: {response.StatusCode}");
                throw new Exception("Failed to generate the audio file.");
            }

            var audioStream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            await audioStream.CopyToAsync(memoryStream);
            var bytes = memoryStream.ToArray();
            var result = "Done";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Result", result);
            var fileName = "audio" + (quality.Contains("mp3") ? ".mp3" : ".wav");
            _responseAccessor.CurrentMessage.AddFile(fileName, Convert.ToBase64String(bytes), bytes.Length);
            return result;
        }

        private async Task<string> GetAccessToken(string apiKey, string region)
        {
            var tokenUri = $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken";

            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-www-form-urlencoded"));
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
            var response = await httpClient.PostAsync(tokenUri, new StringContent(string.Empty));
            if (!response.IsSuccessStatusCode)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Error", $"Failed to generate access token. Status code: {response.StatusCode}");
                throw new Exception("Failed to generate access token.");
            }
            return await response.Content.ReadAsStringAsync();
        }

    }

    public interface IAzureAiSpeechCreateSpeechAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
