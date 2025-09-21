using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;

using IMcpClient = AiCoreApi.Common.IMcpClient;


namespace AiCoreApi.SemanticKernel.Agents
{
    public class McpClientAgent : BaseAgent, IMcpClientAgent
    {
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IMcpClient _mcpClient;

        private string _debugMessageSenderName = "McpClientAgent";
        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string McpServerAction = "mcpServerAction";
        }

        public McpClientAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IMcpClient mcpClient,
            ILogger<McpClientAgent> logger
        ) : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _mcpClient = mcpClient;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters)
            => await base.DoCallWrapper(agent, parameters);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = await GetParameterValueAsync(AgentContentParameters.ConnectionName);
            var mcpServerActionJson = await GetParameterValueAsync(AgentContentParameters.McpServerAction);

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.Mcp, _debugMessageSenderName, connectionName: connectionName);

            var serverUrl = await ApplyParametersAsync(connection.Content["serverUrl"].TrimEnd('/'), parameters);
            var customHeader = await ApplyParametersAsync(connection.Content.GetValueOrDefault("customHeader", ""));

            var paramDump = parameters != null && parameters.Any()
                ? string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}"))
                : "none";

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "MCP Start", $"Connecting to MCP server at {serverUrl} with parameters: {paramDump}");
            try
            {
                var result = await _mcpClient.ExecuteAction(serverUrl, customHeader, mcpServerActionJson);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "MCP Action Result", result);
                return result;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "MCP Error", $"Exception: {ex.Message}\r\nInner: {ex.InnerException?.Message}");
                throw;
            }
        }
    }

    public interface IMcpClientAgent
    {
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
