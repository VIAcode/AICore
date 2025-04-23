using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Microsoft.Data.SqlClient;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class SqlServerAgent : BaseAgent, ISqlServerAgent
    {
        private string _debugMessageSenderName = "SqlServerAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string SqlQuery = "sqlQuery";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public SqlServerAgent(
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<SqlServerAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var sqlQuery = ApplyParameters(agent.Content[AgentContentParameters.SqlQuery].Value, parameters);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", sqlQuery);
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.SqlServer, _debugMessageSenderName, connectionName: connectionName);
            var result = await ExecuteScript(sqlQuery, connection);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        private async Task<string> ExecuteScript(string script, ConnectionModel connection)
        {
            var connectionString = connection.Content["connectionString"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "connectionString";
            using var sqlServerConnection = new SqlConnection(connectionString);
            if (accessType != "connectionString")
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenAsync(accessType, "https://database.windows.net/.default");
                sqlServerConnection.AccessToken = accessToken;
            }
            sqlServerConnection.Open();
            using var command = new SqlCommand(script, sqlServerConnection);
            using var reader = await command.ExecuteReaderAsync();
            var tables = new List<List<Dictionary<string, object>>>();
            do
            {
                var rows = new List<Dictionary<string, object>>();
                while (reader.Read())
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }
                if (rows.Count > 0)
                {
                    tables.Add(rows);
                }

            } while (reader.NextResult());
            return JsonSerializer.Serialize(tables);
        }
    }

    public interface ISqlServerAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
