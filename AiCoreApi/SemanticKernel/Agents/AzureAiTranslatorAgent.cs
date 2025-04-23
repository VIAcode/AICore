using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using System.Text;
using System.Text.Json;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AzureAiTranslatorAgent : BaseAgent, IAzureAiTranslatorAgent
    {
        private string _debugMessageSenderName = "AzureAiTranslatorAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string From = "from";
            public const string To = "to";
            public const string Text = "text";
        }

        const string Endpoint = "https://api.cognitive.microsofttranslator.com";

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;

        public AzureAiTranslatorAgent(
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            ExtendedConfig extendedConfig,
            ILogger<AzureAiTranslatorAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiTranslator, _debugMessageSenderName, connectionName: connectionName);

            var apiKey = connection.Content["apiKey"];
            var region = connection.Content["region"];
            var fromLanguage = agent.Content.ContainsKey(AgentContentParameters.From) 
                ? ApplyParameters(agent.Content[AgentContentParameters.From].Value, parameters) : "";
            var toLanguage = ApplyParameters(agent.Content[AgentContentParameters.To].Value, parameters);
            var text = ApplyParameters(agent.Content[AgentContentParameters.Text].Value, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"Connection: {connectionName}\r\nFrom: {fromLanguage}.\r\nTo: {toLanguage}\r\nText: {text}");
            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            var route = $"/translate?api-version=3.0{(string.IsNullOrWhiteSpace(fromLanguage) ? "" : $"&from={fromLanguage}")}&to={toLanguage}";
            var uri = new Uri(Endpoint + route);
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", region);
            var requestBody = new List<object> { new { Text = text } };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(uri, content);
            response.EnsureSuccessStatusCode();
            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(jsonResponse);
            var translation = document.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", translation);
            return translation;
        }
    }

    public interface IAzureAiTranslatorAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
