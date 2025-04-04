using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using Azure.Monitor.Query;
using System.Text.RegularExpressions;

namespace AiCoreApi.SemanticKernel.Agents
{

    public class AzureLogAnalyticsAgent : BaseAgent, IAzureLogAnalyticsAgent
    {
        private enum QueryScope { Workspace, Resource }

        private const string DebugMessageSenderName = "AzureLogAnalyticsAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string KqlQuery = "kqlQuery";
            public const string TimeRangeStart = "timeRangeStart";
            public const string TimeRangeEnd = "timeRangeEnd";
            public const string Duration = "duration"; // <Number><Unit>: 2d, 8h, 30m, 10s (day/hour/minute/second)
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public AzureLogAnalyticsAgent(
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<SqlServerAgent> logger) : base(requestAccessor, extendedConfig, logger)
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
            var query = ApplyParameters(agent.Content[AgentContentParameters.KqlQuery].Value, parameters);
            var timeRange = ReadQueryTimeRange(agent, parameters);

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Request", $"Time range: {timeRange}\r\n\r\nKQL query:\r\n{query}");
            var connections = await _connectionProcessor.List();
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureLogAnalytics, DebugMessageSenderName, connectionName: connectionName);

            var result = await QueryLogsAsync(query, timeRange, connection);

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Response", result);
            return result;
        }

        private QueryTimeRange ReadQueryTimeRange(AgentModel agent, Dictionary<string, string> parameters)
        {
            DateTimeOffset? timeRangeStart = null;
            if (agent.Content.TryGetValue(AgentContentParameters.TimeRangeStart, out var timeRangeStartSetting))
            {
                timeRangeStart = DateTimeOffset.TryParse(timeRangeStartSetting.Value ?? "", out var value) ? value : null;
            }

            DateTimeOffset? timeRangeEnd = null;
            if (agent.Content.TryGetValue(AgentContentParameters.TimeRangeEnd, out var TimeRangeEndSetting))
            {
                timeRangeEnd = DateTimeOffset.TryParse(TimeRangeEndSetting.Value ?? "", out var value) ? value : null;
            }

            TimeSpan? duration = null;
            if (agent.Content.TryGetValue(AgentContentParameters.Duration, out var settingValue))
            {
                var match = Regex.Match(settingValue.Value, "^(?<value>\\d+)(?<unit>[dhms])$");
                if (match.Success)
                {
                    var value = Convert.ToInt32(match.Groups["value"].Value);
                    duration = match.Groups["unit"].Value switch
                    {
                        "d" => TimeSpan.FromDays(value),
                        "h" => TimeSpan.FromHours(value),
                        "m" => TimeSpan.FromMinutes(value),
                        "s" => TimeSpan.FromSeconds(value),
                        _ => null
                    };
                }
            }

            if (timeRangeStart.HasValue && timeRangeEnd.HasValue)
                return new QueryTimeRange(timeRangeStart.Value, timeRangeEnd.Value);
            else if (timeRangeStart.HasValue && duration.HasValue)
                return new QueryTimeRange(timeRangeStart.Value, duration.Value);
            else if (timeRangeEnd.HasValue && duration.HasValue)
                return new QueryTimeRange(duration.Value, timeRangeEnd.Value);
            else if (duration.HasValue)
                return new QueryTimeRange(duration.Value);
            else
                return QueryTimeRange.All;
        }

        private async Task<string> QueryLogsAsync(string query, QueryTimeRange timeRange, ConnectionModel connection)
        {
            var scopeValue = connection.Content.ContainsKey("scope") ? connection.Content["scope"] : "Workspace";
            if (!QueryScope.TryParse(scopeValue, out QueryScope scope))
            {
                throw new InvalidDataException($"Unknown Log Analytics scope type: {scopeValue}. Expected values are \"Workspace\" and \"Resource\"");
            }
            var scopeId = connection.Content.ContainsKey("scopeId") ? connection.Content["scopeId"] : null;
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                throw new InvalidDataException($"Empty Log Analytics scope ID. The scope ID must contain workspace or resource ID");
            }

            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "Default Managed Identity";
            var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://api.loganalytics.io/.default");
            var client = new LogsQueryClient(new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn));


            var result = scope == QueryScope.Workspace ? await client.QueryWorkspaceAsync(scopeId, query, timeRange)
                : await client.QueryResourceAsync(new Azure.Core.ResourceIdentifier(scopeId), query, timeRange);

            var tables = new List<List<Dictionary<string, object>>>();
            foreach(var table in result.Value.AllTables)
            {
                var rows = table.Rows.Select(
                    r => table.Columns
                        .Select(c => new { Key = c.Name, Value = r[c.Name] })
                        .ToDictionary(kv => kv.Key, kv => kv.Value))
                    .ToList();

                tables.Add(rows);
            }
            return JsonSerializer.Serialize(tables);
        }
    }

    public interface IAzureLogAnalyticsAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
