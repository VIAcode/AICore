using System.Net.Http.Headers;
using System.Text;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OpenSearchAgent : BaseAgent, IOpenSearchAgent
    {
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private string _debugMessageSenderName = "OpenSearchAgent";

        private static class AgentContentParameters
        {
            public const string Action = "action";
            public const string IndexName = "indexName";
            public const string Query = "query";
            public const string DocumentId = "documentId";
            public const string Payload = "payload";
            public const string ConnectionName = "connectionName";
        }

        private static class ConnectionContentParameters
        {
            public const string Endpoint = "endpoint";
            public const string AccessType = "accessType";
            public const string Login = "login";
            public const string Password = "password";
            public const string ApiKey = "apiKey";
        }

        public OpenSearchAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<OpenSearchAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var action = agent.Content[AgentContentParameters.Action].Value;
            var indexName = await GetParameterValueAsync(AgentContentParameters.IndexName);
            var query = await GetParameterValueAsync(AgentContentParameters.Query);
            var documentId = await GetParameterValueAsync(AgentContentParameters.DocumentId);
            var payload = await GetParameterValueAsync(AgentContentParameters.Payload);
            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.OpenSearch, _debugMessageSenderName, connectionName: connectionName);
            var endpoint = connection.Content[ConnectionContentParameters.Endpoint].TrimEnd('/');

            return action switch
            {
                "INDEX_DOCUMENT" => await IndexDocument(endpoint, indexName, payload, connection),
                "GET_DOCUMENT" => await GetDocument(endpoint, indexName, documentId, connection),
                "DELETE_DOCUMENT" => await DeleteDocument(endpoint, indexName, documentId, connection),
                "SEARCH" => await Search(endpoint, indexName, query, connection),
                _ => throw new ExceptionHandlingMiddleware.AiCoreUiException($"Unsupported action '{action}' for OpenSearch")
            };
        }

        private async Task<string> IndexDocument(string endpoint, string indexName, string jsonPayload, ConnectionModel connection)
        {
            var url = $"{endpoint}/{indexName}/_doc";
            return await PostJson(url, jsonPayload, connection);
        }

        private async Task<string> GetDocument(string endpoint, string indexName, string id, ConnectionModel connection)
        {
            var url = $"{endpoint}/{indexName}/_doc/{id}";
            return await Get(url, connection);
        }

        private async Task<string> DeleteDocument(string endpoint, string indexName, string id, ConnectionModel connection)
        {
            var url = $"{endpoint}/{indexName}/_doc/{id}";
            return await Delete(url, connection);
        }

        private async Task<string> Search(string endpoint, string indexName, string queryJson, ConnectionModel connection)
        {
            var url = $"{endpoint}/{indexName}/_search";
            return await PostJson(url, queryJson, connection);
        }

        private async Task<string> Get(string url, ConnectionModel connection)
        {
            var client = GetClient(connection);
            var response = await client.GetAsync(url);
            return await HandleResponse(response);
        }

        private async Task<string> Delete(string url, ConnectionModel connection)
        {
            var client = GetClient(connection);
            var response = await client.DeleteAsync(url);
            return await HandleResponse(response);
        }

        private async Task<string> PostJson(string url, string json, ConnectionModel connection)
        {
            var client = GetClient(connection);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            return await HandleResponse(response);
        }

        private HttpClient GetClient(ConnectionModel connection)
        {
            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var accessType = connection.Content[ConnectionContentParameters.AccessType];
            if (accessType == "credentials")
            {
                var login = connection.Content[ConnectionContentParameters.Login];
                var password = connection.Content[ConnectionContentParameters.Password];
                var byteArray = Encoding.ASCII.GetBytes($"{login}:{password}");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            }
            else if (accessType == "apiKey")
            {
                var apiKey = connection.Content[ConnectionContentParameters.ApiKey];
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                throw new ExceptionHandlingMiddleware.AiCoreUiException("Unsupported access type for OpenSearch");
            }
            return client;
        }

        private async Task<string> HandleResponse(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new ExceptionHandlingMiddleware.AiCoreUiException($"OpenSearch API error {response.StatusCode}: {body}");
            }
            return body;
        }
    }

    public interface IOpenSearchAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
