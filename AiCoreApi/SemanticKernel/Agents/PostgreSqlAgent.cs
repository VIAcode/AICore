using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Npgsql;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class PostgreSqlAgent : BaseAgent, IPostgreSqlAgent
    {
        private const string DebugMessageSenderName = "PostgreSqlAgent";

        // In this context PostgreSQL does not allow parameter placeholders
        private const string InitSessionContextQuery = @"
SET aicore_session_context.login = '{0}';
SET aicore_session_context.login_type = '{1}';
";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string SqlQuery = "sqlQuery";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public PostgreSqlAgent(
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<PostgreSqlAgent> logger) : base(requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor; 
        }
        
        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var sqlQuery = ApplyParameters(agent.Content[AgentContentParameters.SqlQuery].Value, parameters);

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Request", sqlQuery);
            var connections = await _connectionProcessor.List();
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.PostgreSql, DebugMessageSenderName, connectionName: connectionName);
            var result = await ExecuteScript(sqlQuery, connection, parameters);
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Response", result);
            return result;
        }

        private async Task<string> ExecuteScript(string script, ConnectionModel connection, Dictionary<string, string> parameters)
        {
            var connectionString = connection.Content["connectionString"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "connectionString";
            
            var csBuilder  = new NpgsqlConnectionStringBuilder(connectionString);
            
            if (accessType != "connectionString")
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenAsync(accessType, "https://ossrdbms-aad.database.windows.net/.default");
                csBuilder.Password = accessToken;
            }

            var addSessionContext = false;
            if (parameters.TryGetValue("addSessionContext", out var addSessionContextRaw))
            {
                bool.TryParse(addSessionContextRaw, out addSessionContext);
            }

            // When connections are pooled session context may leak.
            // Explicitly setting this to false ensures that session context does not leak,
            // regardless of the connection string configuration
            if (addSessionContext)
            {
                csBuilder.NoResetOnClose = false;
            }

            await using var pgConnection = new NpgsqlConnection(csBuilder.ConnectionString);
            await pgConnection.OpenAsync().ConfigureAwait(false);

            if (addSessionContext)
            {
                await using var cmd = new NpgsqlCommand(PrepareInitSessionQuery(), pgConnection);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(script, pgConnection);
            await using var reader = await command.ExecuteReaderAsync();
            var tables = new List<List<Dictionary<string, object?>>>();
            do
            {
                var rows = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>();
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

            } while (await reader.NextResultAsync().ConfigureAwait(false));
            return JsonSerializer.Serialize(tables);
        }

        string PrepareInitSessionQuery()
        {
            return string.Format(InitSessionContextQuery, EscapeQueryString(_requestAccessor.Login),
                EscapeQueryString(_requestAccessor.LoginTypeString));
        }

        string EscapeQueryString(string? value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }

    public interface IPostgreSqlAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
