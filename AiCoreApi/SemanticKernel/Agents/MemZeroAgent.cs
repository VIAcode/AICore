using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;
using System.Text.Json;
using System.Net.Http.Headers;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class MemZeroAgent : BaseAgent, IMemZeroAgent
    {
        private string _sender = "MemZeroAgent";
        private static class Param
        {
            public const string ConnectionName = "connectionName";
            public const string Action = "action";
            public const string UserId = "userId";
            public const string TopK = "topK";
            public const string SearchString = "searchString";
            public const string Message = "message";
            public const string ExpirationDate = "expirationDate";
            public const string AsyncMode = "asyncMode";
        }

        private readonly IConnectionProcessor _connProc;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpFactory;
        public MemZeroAgent(
            IBaseAgentHelper baseAgentHelper, 
            IConnectionProcessor connProc, 
            RequestAccessor requestAccessor, 
            ResponseAccessor responseAccessor, 
            IHttpClientFactory httpFactory, 
            ILogger<MemZeroAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connProc = connProc;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpFactory = httpFactory;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _sender = $"{agent.Name} ({agent.Type})";

            var connName = agent.Content[Param.ConnectionName].Value;
            var action = GetParameterValue(Param.Action).ToUpper();

            var connections = await _connProc.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.MemZero, _sender, connectionName: connName);
            var baseUrl = connection.Content["baseUrl"].TrimEnd('/', ' ');
            var apiKey = connection.Content["apiKey"];
            var projectId = connection.Content["projectId"];
            var organizationId = connection.Content["organizationId"];

            var client = _httpFactory.CreateClient(HttpClients.NoRetryClient);
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", apiKey);

            HttpResponseMessage resp;
            var result = "";
            var userId = GetParameterValue(Param.UserId);

            switch (action)
            {
                case "ADD":
                    {
                        var message = GetParameterValue(Param.Message);
                        var expirationDate = agent.Content.ContainsKey(Param.ExpirationDate)
                            ? GetParameterValue(Param.ExpirationDate)
                            : "";
                        var asyncMode = GetParameterValue(Param.AsyncMode);
                        var addPayload = new Dictionary<string, object>
                        {
                            { "messages", new[] { new { role = "user", content = message } } },
                            { "agent_id", "Assistant" },
                            { "user_id", userId },
                            { "infer", true },
                            { "org_id", organizationId },
                            { "project_id", projectId },
                            { "version", "v2" },
                            { "async_mode", asyncMode }
                        };
                        if (!string.IsNullOrEmpty(expirationDate))
                        {
                            addPayload["expiration_date"] = expirationDate;
                        }
                        var payloadString = JsonSerializer.Serialize(addPayload);
                        _responseAccessor.AddDebugMessage(_sender, "Add Request", $"{payloadString}");
                        resp = await client.PostAsync("/v1/memories/", new StringContent(payloadString, System.Text.Encoding.UTF8, "application/json"));
                        break;
                    }
                case "SEARCH":
                    {
                        var searchString = GetParameterValue(Param.SearchString);
                        var topK = GetParameterValue(Param.TopK);
                        var searchPayload = new Dictionary<string, object>
                        {
                            { "query", searchString },
                            { "filters", new { user_id = userId } },
                            { "top_k", topK },
                            { "org_id", organizationId },
                            { "project_id", projectId },
                        };
                        var payloadString = JsonSerializer.Serialize(searchPayload);
                        _responseAccessor.AddDebugMessage(_sender, "Search Request", $"{payloadString}");
                        resp = await client.PostAsync("/v2/memories/search/", new StringContent(payloadString, System.Text.Encoding.UTF8, "application/json"));
                        break;
                    }
                case "DELETE":
                    {
                        var url = "/v1/memories/";
                        if (!string.IsNullOrEmpty(userId))
                            url += $"?user_id={HttpUtility.UrlEncode(userId)}";
                        _responseAccessor.AddDebugMessage(_sender, "Delete Request", $"{url}");
                        resp = await client.DeleteAsync(url);
                        break;
                    }
                default:
                    resp = null;
                    break;
            }
            if (resp != null)
                result = await resp.Content.ReadAsStringAsync();
            else
                result = "Unexpected action or response";

            _responseAccessor.AddDebugMessage(_sender, "Response", result);
            return result;
        }
    }

    public interface IMemZeroAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
