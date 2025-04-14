using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Newtonsoft.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AzureAiSearchAgent : BaseAgent, IAzureAiSearchAgent
    {
        private string _debugMessageSenderName = "AzureAiSearchAgent";

        private static class AgentContentParameters
        {
            public const string AzureAiSearchConnectionName = "azureAiSearchConnectionName";
            public const string IndexName = "indexName";
            public const string QueryString = "queryString";
        }
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public AzureAiSearchAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<AzureAiSearchAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var queryString = ApplyParameters(agent.Content[AgentContentParameters.QueryString].Value, parameters);
            var azureAiSearchConnectionName = agent.Content[AgentContentParameters.AzureAiSearchConnectionName].Value;
            var indexName = agent.Content[AgentContentParameters.IndexName].Value;
            // Check if the action is search or add-update-delete, different actions have different endpoints
            var action = queryString.Contains("@search.action") ? "index" : "search";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall", $"Connection: {azureAiSearchConnectionName}\r\nAction: {action}\r\nIndex: {indexName}\r\nQuery: {queryString}");
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var aiSearchConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiSearch, _debugMessageSenderName, connectionName: azureAiSearchConnectionName);
            var apiKey = aiSearchConnection.Content["apiKey"];
            var resourceName = aiSearchConnection.Content["resourceName"];

            var endpoint = $"https://{resourceName}.search.windows.net/indexes/{indexName}/docs/{action}?api-version=2023-11-01";
            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
            var content = new StringContent(queryString, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(endpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Error", $"Request to Azure AI Search failed. Status code: {response.StatusCode}");
                throw new Exception("Request to Azure AI Search failed.");
            }
            var result = await response.Content.ReadAsStringAsync();
            var jsonResult = JsonConvert.DeserializeObject<dynamic>(result);
            result = jsonResult?.value.ToString() ?? "[]";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Result", result);
            return result;
        }
    }

    public interface IAzureAiSearchAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
