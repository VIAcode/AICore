using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using System.Web;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class QdrantAgent : BaseAgent, IQdrantAgent
    {
        private string _debugMessageSenderName = "QdrantAgent";

        private static class AgentContentParameters
        {
            public const string QdrantConnectionName = "qdrantConnection";
            public const string EmbeddingConnectionName = "embeddingConnection";
            public const string CollectionName = "collectionName";
            public const string SearchString = "searchString";
            public const string TopK = "topK";
            public const string PointId = "pointId";
            public const string Tags = "tags"; 
            public const string Distance = "distance"; 
            public const string Size = "size";
            public const string Action = "action";
            public const string Limit = "limit";
            public const string Offset = "offset";
            public const string Method = "method";
            public const string Url = "url";
            public const string Payload = "payload";
            public const string Filter = "filter";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEmbeddingProcessor _embeddingProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public QdrantAgent(
            IConnectionProcessor connectionProcessor,
            IEmbeddingProcessor embeddingProcessor,
            IHttpClientFactory httpClientFactory,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            MonitoringConfig monitoringConfig,
            ILogger<QdrantAgent> logger
        ) : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _embeddingProcessor = embeddingProcessor;
            _httpClientFactory = httpClientFactory;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var action = agent.Content[AgentContentParameters.Action].Value;
            var collectionName = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.CollectionName) ? agent.Content[AgentContentParameters.CollectionName].Value : "", parameters);
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);

            var qdrantConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.Qdrant, _debugMessageSenderName, connectionName: agent.Content[AgentContentParameters.QdrantConnectionName].Value);

            switch (action)
            {
                case "COLLECTIONS_LIST":
                    return await ListCollections(qdrantConnection);
                case "COLLECTION_CREATE":
                {
                    var distance = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Distance) ? agent.Content[AgentContentParameters.Distance].Value : "Cosine", parameters);
                    var size = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Size) ? agent.Content[AgentContentParameters.Size].Value : "3072", parameters);
                    return await CreateCollection(collectionName, qdrantConnection, distance, Convert.ToInt32(size));
                }
                case "COLLECTION_DELETE":
                    return await DeleteCollection(collectionName, qdrantConnection);
                case "UPSERT_POINTS":
                    {
                        var embeddingConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                            new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, _debugMessageSenderName, connectionName: agent.Content[AgentContentParameters.EmbeddingConnectionName].Value);

                        var payload = ApplyParameters(agent.Content[AgentContentParameters.SearchString].Value, parameters);
                        var tags = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Tags) ? agent.Content[AgentContentParameters.Tags].Value : "Tag", parameters);

                        var chunks = await _embeddingProcessor.GetEmbeddingAsync(embeddingConnection, payload);
                        foreach (var chunk in chunks)
                        {
                            await AddPoint(collectionName, chunk, tags, qdrantConnection);
                        }
                        return "";
                    }
                case "DELETE_POINT":
                    {
                        var pointId = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.PointId) ? agent.Content[AgentContentParameters.PointId].Value : "0", parameters);
                        return await DeletePoint(collectionName, pointId, qdrantConnection);
                    }
                case "TAG_POINT":
                    {
                        var pointId = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.PointId) ? agent.Content[AgentContentParameters.PointId].Value : "0", parameters);
                        var tags = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Tags) ? agent.Content[AgentContentParameters.Tags].Value : "Tag", parameters);
                        return await UpdateTags(collectionName, pointId, tags, qdrantConnection);
                    }
                case "LIST_POINTS":
                    {
                        var limit = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Limit) ? agent.Content[AgentContentParameters.Limit].Value : "10", parameters);
                        var offset = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Offset) ? agent.Content[AgentContentParameters.Offset].Value : "0", parameters);
                        return await ListPoints(collectionName, qdrantConnection, limit, offset);
                    }
                case "CUSTOM":
                {
                    var method = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Method) ? agent.Content[AgentContentParameters.Method].Value : "POST", parameters);
                    var url = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Url) ? agent.Content[AgentContentParameters.Url].Value : "/", parameters);
                    var payload = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Payload) ? agent.Content[AgentContentParameters.Payload].Value : "", parameters);
                    return await Custom(collectionName, qdrantConnection, method, url, payload);
                }
                default: // SEARCH_POINTS
                    {
                        var embeddingConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                            new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, _debugMessageSenderName, connectionName: agent.Content[AgentContentParameters.EmbeddingConnectionName].Value);

                        var filter = ApplyParameters(agent.Content.ContainsKey(AgentContentParameters.Filter) ? agent.Content[AgentContentParameters.Filter].Value : "", parameters);
                        var searchString = ApplyParameters(agent.Content[AgentContentParameters.SearchString].Value, parameters);
                        var topK = int.Parse(ApplyParameters(agent.Content[AgentContentParameters.TopK].Value, parameters));

                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Search String", searchString);

                        var chunks = await _embeddingProcessor.GetEmbeddingAsync(embeddingConnection, searchString);
                        var result = await SearchQdrantManualHttp(collectionName, chunks[0].Vector.ToArray(), topK, filter, qdrantConnection);

                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
                        return result;
                    }
            }
        }

        private async Task<string> Custom(string name, ConnectionModel connection, string method, string url, string payload)
        {
            var client = _httpClientFactory.CreateClient("NoRetryClient");
            if (connection.Content.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            var fullUrl = $"{connection.Content["endpoint"].TrimEnd('/')}/{url.Trim('/')}";
            HttpResponseMessage response;
            var content = string.IsNullOrWhiteSpace(payload)
                ? null
                : new StringContent(payload, Encoding.UTF8, "application/json");
            switch (method.ToUpperInvariant())
            {
                case "GET":
                    response = await client.GetAsync(fullUrl);
                    break;
                case "POST":
                    response = await client.PostAsync(fullUrl, content);
                    break;
                case "PUT":
                    response = await client.PutAsync(fullUrl, content);
                    break;
                case "DELETE":
                    response = content == null
                        ? await client.DeleteAsync(fullUrl)
                        : await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, fullUrl) { Content = content });
                    break;
                default:
                    throw new NotSupportedException($"HTTP method '{method}' is not supported.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Custom request failed: {response.StatusCode} - {err}");
            }
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> ListCollections(ConnectionModel connection)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections";
            return await Get(url, connection);
        }

        private async Task<string> CreateCollection(string name, ConnectionModel connection, string distance, int size)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{name}";
            var body = new { vectors = new { size = size, distance = distance } };
            return await PutJson(url, body, connection);
        }

        private async Task<string> DeleteCollection(string name, ConnectionModel connection)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{name}";
            return await Delete(url, connection);
        }

        private async Task<string> AddPoint(string collectionName, Chunk chunk, string? tags, ConnectionModel connection)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{collectionName}/points";
            var payload = new
            {
                points = new[]
                {
                    new
                    {
                        id = Guid.NewGuid().ToString(),
                        vector = chunk.Vector,
                        payload = new
                        {
                            text = chunk.Text,
                            tags = tags?.Split(',')
                        }
                    }
                }
            };
            return await PutJson(url, payload, connection);
        }

        private async Task<string> DeletePoint(string collectionName, string id, ConnectionModel connection)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{collectionName}/points/delete";
            var body = new { points = id.Split(',') };
            return await PostJson(url, body, connection);
        }

        private async Task<string> UpdateTags(string collectionName, string id, string tags, ConnectionModel connection)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{collectionName}/points/payload";
            var payload = new
            {
                payload = new { tags = tags.Split(',') },
                points = id.Split(',')
            };
            return await PostJson(url, payload, connection);
        }

        private async Task<string> ListPoints(string collectionName, ConnectionModel connection, string limit, string offset)
        {
            var url = $"{connection.Content["endpoint"].TrimEnd('/')}/collections/{collectionName}/points/scroll";
            var isOffsetInt = Int32.TryParse(offset, out var offsetInt);
            object body = isOffsetInt 
                ? new { limit = Convert.ToInt32(limit), offset = offsetInt, with_vector = true }
                : new { limit = Convert.ToInt32(limit), offset = offset, with_vector = true };
            return await PostJson(url, body, connection);
        }

        private async Task<string> PostJson(string url, object body, ConnectionModel connection)
        {
            var client = _httpClientFactory.CreateClient("NoRetryClient");
            if (connection.Content.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Request failed: {response.StatusCode} - {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> PutJson(string url, object body, ConnectionModel connection)
        {
            var client = _httpClientFactory.CreateClient("NoRetryClient");
            if (connection.Content.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await client.PutAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Request failed: {response.StatusCode} - {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> Delete(string url, ConnectionModel connection)
        {
            var client = _httpClientFactory.CreateClient("NoRetryClient");
            if (connection.Content.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.DeleteAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Request failed: {response.StatusCode} - {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> Get(string url, ConnectionModel connection)
        {
            var client = _httpClientFactory.CreateClient("NoRetryClient");

            if (connection.Content.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"GET request failed: {response.StatusCode} - {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string> SearchQdrantManualHttp(string collectionName, float[] vector, int topK, string filter, ConnectionModel connection)
        {
            var endpoint = connection.Content["endpoint"].TrimEnd('/');
            var apiKey = connection.Content.ContainsKey("apiKey") ? connection.Content["apiKey"] : null;

            var client = _httpClientFactory.CreateClient("NoRetryClient");
            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var body = new
            {
                vector,
                limit = topK,
                with_payload = true,
                with_vector = true,
                filter = string.IsNullOrWhiteSpace(filter) ? null : JsonSerializer.Deserialize<object>(filter)
            };

            var url = $"{endpoint}/collections/{collectionName}/points/search";
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Qdrant search failed: {response.StatusCode} - {err}");
            }

            var json = await response.Content.ReadAsStringAsync();
            return json;
        }
    }

    public interface IQdrantAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
