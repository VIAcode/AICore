using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Web;
using System.Net.Http.Headers;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Monitoring;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class ConfluenceAgent : BaseAgent, IConfluenceAgent
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private string _debugMessageSenderName = "ConfluenceAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Action = "action";
            public const string PageId = "pageId";
            public const string SpaceKey = "spaceKey";
            public const string Title = "title";
            public const string Content = "content";
            public const string Expand = "expand";
            public const string ParentPageId = "parentPageId";
        }

        private static class Actions
        {
            public const string List = "list";
            public const string Get = "get";
            public const string Add = "add";
            public const string Update = "update";
            public const string Delete = "delete";
        }

        private static class ConnectionParameters
        {
            public const string BaseUrl = "baseUrl";
            public const string Username = "username";
            public const string ApiToken = "apiToken";
            public const string RootPageId = "rootPageId";
        }

        public ConfluenceAgent(
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            MonitoringConfig monitoringConfig,
            ILogger<ConfluenceAgent> logger)
            : base(responseAccessor, requestAccessor, monitoringConfig, logger)
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

            var action = agent.Content[AgentContentParameters.Action].Value;
            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.Confluence, _debugMessageSenderName, connectionName: connectionName);

            var baseUrl = connection.Content[ConnectionParameters.BaseUrl];
            var username = connection.Content[ConnectionParameters.Username];
            var apiToken = connection.Content[ConnectionParameters.ApiToken];
            var rootPageId = connection.Content[ConnectionParameters.RootPageId];

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            switch (action)
            {
                case Actions.List:
                    var listExpand = ApplyParameters(agent.Content[AgentContentParameters.Expand].Value, parameters);
                    return await ListPages(client, baseUrl, rootPageId, listExpand);

                case Actions.Get:
                    var getExpand = ApplyParameters(agent.Content[AgentContentParameters.Expand].Value, parameters);
                    var pageId = ApplyParameters(agent.Content[AgentContentParameters.PageId].Value, parameters);
                    return await GetPage(client, baseUrl, pageId, getExpand);

                case Actions.Add:
                    var addSpaceKey = ApplyParameters(agent.Content[AgentContentParameters.SpaceKey].Value, parameters);
                    var addTitle = ApplyParameters(agent.Content[AgentContentParameters.Title].Value, parameters);
                    var addContent = ApplyParameters(agent.Content[AgentContentParameters.Content].Value, parameters);
                    var parentPageId = agent.Content.ContainsKey(AgentContentParameters.ParentPageId)
                        ? ApplyParameters(agent.Content[AgentContentParameters.ParentPageId].Value, parameters)
                        : rootPageId;
                    return await AddPage(client, baseUrl, addSpaceKey, addTitle, addContent, parentPageId);

                case Actions.Update:
                    var updateTitle = ApplyParameters(agent.Content[AgentContentParameters.Title].Value, parameters);
                    var updateContent = ApplyParameters(agent.Content[AgentContentParameters.Content].Value, parameters);
                    var updatePageId = agent.Content.ContainsKey(AgentContentParameters.PageId)
                        ? ApplyParameters(agent.Content[AgentContentParameters.PageId].Value, parameters)
                        : string.Empty;
                    return await UpdatePage(client, baseUrl, updateTitle, updateContent, updatePageId);

                case Actions.Delete:
                    var deletePageId = ApplyParameters(agent.Content[AgentContentParameters.PageId].Value, parameters);
                    return await DeletePage(client, baseUrl, deletePageId);

                default:
                    throw new ArgumentException($"Unsupported action: {action}");
            }
        }

        private async Task<string> ListPages(HttpClient client, string baseUrl, string rootPageId, string expand)
        {
            var rootJson = await GetPageDetails(client, baseUrl, rootPageId, expand);
            var childrenJson = await GetAllDescendants(client, baseUrl, rootPageId, expand);

            using var rootDoc = JsonDocument.Parse(rootJson);
            using var childrenDoc = JsonDocument.Parse(childrenJson);

            var result = new
            {
                root = rootDoc.RootElement,
                children = childrenDoc.RootElement.GetProperty("results")
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        private async Task<string> GetPageDetails(HttpClient client, string baseUrl, string rootPageId, string expand)
        {
            var url = $"{baseUrl}/rest/api/content/{HttpUtility.UrlEncode(rootPageId)}?expand={expand}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> GetAllDescendants(HttpClient client, string baseUrl, string rootPageId, string expand)
        {
            var url = $"{baseUrl}/rest/api/content/{HttpUtility.UrlEncode(rootPageId)}/descendant/page?expand={expand}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> GetPage(HttpClient client, string baseUrl, string pageId, string expand)
        {
            var url = $"{baseUrl}/rest/api/content/{pageId}?expand={expand}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> UpdatePage(HttpClient client, string baseUrl, string title, string content, string pageId)
        {
            var pageData = await GetPage(client, baseUrl, pageId, "body.storage,version");
            using var doc = JsonDocument.Parse(pageData);
            int currentVersion = doc.RootElement
                .GetProperty("version")
                .GetProperty("number")
                .GetInt32();

            var updateBody = new
            {
                id = pageId,
                type = "page",
                title = title,
                version = new { number = currentVersion + 1 },
                body = new
                {
                    storage = new
                    {
                        value = content,
                        representation = "storage"
                    }
                }
            };

            var jsonBody = new StringContent(JsonSerializer.Serialize(updateBody),
                System.Text.Encoding.UTF8, "application/json");

            var response = await client.PutAsync($"{baseUrl}/rest/api/content/{pageId}", jsonBody);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> AddPage(HttpClient client, string baseUrl, string spaceKey, string title, string content, string rootPageId)
        {
            var createBody = new
            {
                type = "page",
                title = title,
                space = new { key = spaceKey },
                ancestors = new[]
                {
                    new { id = rootPageId }
                },
                body = new
                {
                    storage = new
                    {
                        value = content,
                        representation = "storage"
                    }
                }
            };
            var jsonBody = new StringContent(JsonSerializer.Serialize(createBody),
                System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl}/rest/api/content", jsonBody);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> DeletePage(HttpClient client, string baseUrl, string pageId)
        {
            var url = $"{baseUrl}/rest/api/content/{pageId}";
            var response = await client.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
            return "{\"status\":\"deleted\"}";
        }
    }

    public interface IConfluenceAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
