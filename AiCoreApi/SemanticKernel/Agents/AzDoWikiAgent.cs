using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Web;
using System.Net.Http.Headers;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AzDoWikiAgent : BaseAgent, IAzDoWikiAgent
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private string _debugMessageSenderName = "AzDoWikiAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Action = "action";
            public const string Path = "path";
            public const string Content = "content";
        }

        private static class Actions
        {
            public const string List = "list";
            public const string Get = "get";
            public const string AddOrUpdate = "addOrUpdate";
            public const string Delete = "delete";
        }

        private static class ConnectionParameters
        {
            public const string Pat = "pat";
            public const string Organization = "organization";
            public const string Project = "project";
            public const string Wiki = "wiki";
        }

        public AzDoWikiAgent(
            IBaseAgentHelper baseAgentHelper,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<AzDoWikiAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var path = GetParameterValue(AgentContentParameters.Path);
            var action = agent.Content[AgentContentParameters.Action].Value;
            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzDoWiki, _debugMessageSenderName, connectionName: connectionName);

            var pat = connection.Content[ConnectionParameters.Pat];
            var org = connection.Content[ConnectionParameters.Organization];
            var project = connection.Content[ConnectionParameters.Project];
            var wiki = connection.Content[ConnectionParameters.Wiki];

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"path: {path}");

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var patToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", patToken);

            switch (action)
            {
                case Actions.Get:
                    return await GetWikiPageContent(client, org, project, wiki, path);
                case Actions.List:
                    return await ListWikiPages(client, org, project, wiki);
                case Actions.AddOrUpdate:
                    var content = GetParameterValue(AgentContentParameters.Content);
                    return await AddOrUpdateWikiPage(client, org, project, wiki, path, content);
                case Actions.Delete:
                    return await DeleteWikiPage(client, org, project, wiki, path);
                default:
                    throw new ArgumentException($"Unsupported action: {action}");
            }
        }

        private async Task<string> ListWikiPages(HttpClient client, string org, string project, string wiki)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path=/&recursionLevel=full&api-version=7.0";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return json;
        }

        private async Task<string> GetWikiPageContent(HttpClient client, string org, string project, string wiki, string path)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={HttpUtility.UrlEncode(path)}&includeContent=True&api-version=7.0";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return json;
        }

        private async Task<string> AddOrUpdateWikiPage(HttpClient client, string org, string project, string wiki, string path, string content)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={HttpUtility.UrlEncode(path)}&api-version=7.0";

            var body = new
            {
                content = content
            };
            var jsonBody = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PutAsync(url, jsonBody);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> DeleteWikiPage(HttpClient client, string org, string project, string wiki, string path)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={HttpUtility.UrlEncode(path)}&api-version=7.0";
            var response = await client.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
            return "{\"status\":\"deleted\"}";
        }
    }

    public interface IAzDoWikiAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
