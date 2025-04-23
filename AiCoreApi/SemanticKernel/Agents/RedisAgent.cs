using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using StackExchange.Redis;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class RedisAgent : BaseAgent, IRedisAgent
    {
        private string _debugMessageSenderName = "RedisAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Action = "action";
            public const string CacheKey = "cacheKey";
            public const string Value = "value";
            public const string LifeTimeSeconds = "lifeTimeSeconds";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public RedisAgent(
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<RedisAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var action = ApplyParameters(agent.Content[AgentContentParameters.Action].Value, parameters);
            var cacheKey = ApplyParameters(agent.Content[AgentContentParameters.CacheKey].Value, parameters);
            var value = ApplyParameters(agent.Content[AgentContentParameters.Value].Value, parameters);
            var lifeTimeSeconds = ApplyParameters(agent.Content[AgentContentParameters.LifeTimeSeconds].Value, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"Action: {action}, Cache Key: {cacheKey}, Value: {value}");
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.Redis, _debugMessageSenderName, connectionName: connectionName);
            var connectionString = connection.Content["connectionString"];
            var redis = ConnectionMultiplexer.Connect(connectionString);
            var db = redis.GetDatabase();

            string result;
            switch (action.ToUpper())
            {
                case "GET":
                    result = await db.StringGetAsync(cacheKey);
                    break;
                case "PUT":
                    await db.StringSetAsync(cacheKey, value, TimeSpan.FromSeconds(int.Parse(lifeTimeSeconds)));
                    result = "Value set successfully.";
                    break;
                case "DELETE":
                    await db.KeyDeleteAsync(cacheKey);
                    result = "Key deleted successfully.";
                    break;
                default:
                    result = "Unknown action.";
                    break;
            }
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }
    }

    public interface IRedisAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
