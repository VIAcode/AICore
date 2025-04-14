using System.Net.Http.Headers;
using System.Web;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class WhisperAgent : BaseAgent, IWhisperAgent
    {
        private string _debugMessageSenderName = "WhisperAgent";
        public static class AgentPromptPlaceholders
        {
            public const string FileDataPlaceholder = "firstFileData";
        }

        private static class AgentContentParameters
        {
            public const string Base64Audio = "base64Audio";
            public const string Extension = "extension";
            public const string MimeType = "mimeType";
            public const string ConnectionName = "connectionName";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public WhisperAgent(
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            ExtendedConfig extendedConfig,
            ILogger<WhisperAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
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

            var base64Audio = ApplyParameters(agent.Content[AgentContentParameters.Base64Audio].Value, parameters);
            var extension = ApplyParameters(agent.Content[AgentContentParameters.Extension].Value, parameters);
            var mimeType = ApplyParameters(agent.Content[AgentContentParameters.MimeType].Value, parameters);
            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            if (_requestAccessor.MessageDialog.Messages!.Last().HasFiles())
            {
                base64Audio = ApplyParameters(base64Audio, new Dictionary<string, string> { 
                    { AgentPromptPlaceholders.FileDataPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().Files!.First().Base64Data } });
            }
            return await Transcribe(base64Audio.StripBase64(), extension.Trim(' ', '.'), mimeType, connectionName);
        }

        public async Task<string> Transcribe(string base64Audio, string extension, string mimeType, string connectionName = "")
        {
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"Extension: {extension}\r\nConnection: {connectionName}\r\nBase64Audio: {base64Audio.Length} bytes");
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureWhisper, _debugMessageSenderName, connectionName: connectionName);

            var whisperApiKey = connection.Content["apiKey"];
            var whisperEndpoint = connection.Content["endpoint"];

            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Add("api-key", whisperApiKey);
            var fileContent = new ByteArrayContent(Convert.FromBase64String(base64Audio));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
            using var form = new MultipartFormDataContent
            {
                { fileContent, "file", "audio." + extension }
            };
            var response = await httpClient.PostAsync(whisperEndpoint, form);
            if (!response.IsSuccessStatusCode)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Error", $"Failed to transcribe the audio file. Status code: {response.StatusCode}");
                throw new Exception("Failed to transcribe the audio file.");
            }
            var responseContent = await response.Content.ReadAsStringAsync();
            var recognizedText = responseContent.JsonGet<string>("text") ?? string.Empty;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", recognizedText);
            return recognizedText ?? "";
        }
    }

    public interface IWhisperAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> Transcribe(string base64Audio, string extension, string mimeType, string connectionName = "");
    }
}
