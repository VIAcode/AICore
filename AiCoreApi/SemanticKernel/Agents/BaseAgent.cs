using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Text;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseAgent
    {
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly ILogger<BaseAgent> _logger;

        protected BaseAgent(
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            ExtendedConfig extendedConfig,
            ILogger<BaseAgent> logger)
        {
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
            _extendedConfig = extendedConfig;
            _logger = logger;
        }

        private static class AgentContentParameters
        {
            public const string ParameterDescription = "parameterDescription";
            public const string OutputDescription = "outputDescription";
            public const string PlannerInstruction = "plannerInstruction";
        }

        public async Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions)
        {
            var functionName = agent.Name.ToCamelCase();
            var functionDescription = agent.Description;
            var outputDescription = agent.Content[AgentContentParameters.OutputDescription].Value;
            var parametersList = new List<KernelParameterMetadata>();
            if (agent.Content.ContainsKey(AgentContentParameters.ParameterDescription) && !string.IsNullOrWhiteSpace(agent.Content[AgentContentParameters.ParameterDescription].Value))
            {
                var parameterDescription = agent.Content[AgentContentParameters.ParameterDescription].Value?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                parametersList.AddRange(parameterDescription.Select((t, i) => new KernelParameterMetadata(name: $"parameter{i + 1}") { Description = t, IsRequired = true }));
            }
            var returnParam = new KernelReturnParameterMetadata { Description = outputDescription };
            var function = kernel.CreateFunctionFromMethod(
                AgentCallWrapper,
                functionName,
                functionDescription,
                parametersList,
                returnParam);
            var kernelPlugin = kernel.CreatePluginFromFunctions($"{functionName}Plugin", new[] { function });
            kernel.Plugins.Add(kernelPlugin);
            if (agent.Content.ContainsKey(AgentContentParameters.PlannerInstruction))
                pluginsInstructions.Add(agent.Content[AgentContentParameters.PlannerInstruction].Value);

            async Task<string> AgentCallWrapper(
                string parameter1 = "",
                string parameter2 = "",
                string parameter3 = "",
                string parameter4 = "",
                string parameter5 = "",
                string parameter6 = "",
                string parameter7 = "",
                string parameter8 = "",
                string parameter9 = "")
            {
                var parameters = new Dictionary<string, string>
                {
                    {"parameter1", parameter1},
                    {"parameter2", parameter2},
                    {"parameter3", parameter3},
                    {"parameter4", parameter4},
                    {"parameter5", parameter5},
                    {"parameter6", parameter6},
                    {"parameter7", parameter7},
                    {"parameter8", parameter8},
                    {"parameter9", parameter9}
                };
                return await DoCallWrapper(agent, parameters);
            }
        }


        protected string ApplyParameters(string text, Dictionary<string, string>? parameters)
        {
            if (string.IsNullOrEmpty(text) || parameters == null || parameters.Count == 0)
                return text;

            var inputSpan = text.AsSpan();
            var stringBuilder = new StringBuilder(text.Length);
            var startIndex = 0;
            while (true)
            {
                var openBraceIndex = inputSpan[startIndex..].IndexOf("{{");
                if (openBraceIndex == -1)
                {
                    stringBuilder.Append(inputSpan[startIndex..]);
                    break;
                }
                var closeBraceIndex = inputSpan[(startIndex + openBraceIndex + 2)..].IndexOf("}}");
                if (closeBraceIndex == -1)
                {
                    stringBuilder.Append(inputSpan[startIndex..]);
                    break;
                }
                // Handle nested braces ({{...{{...}}...}})
                while (true)
                {
                    var nextOpenBraceIndex = inputSpan[(startIndex + openBraceIndex + 2)..].IndexOf("{{");
                    if (nextOpenBraceIndex != -1 && (nextOpenBraceIndex + openBraceIndex) < closeBraceIndex)
                    {
                        openBraceIndex = nextOpenBraceIndex + openBraceIndex + 2;
                        closeBraceIndex = inputSpan[(startIndex + openBraceIndex + 2)..].IndexOf("}}");
                        continue;
                    }
                    break;
                }
                
                openBraceIndex += startIndex;
                closeBraceIndex += openBraceIndex + 2;
                stringBuilder.Append(inputSpan[startIndex..openBraceIndex]);
                var parameterKeySpan = inputSpan[(openBraceIndex + 2)..closeBraceIndex];
                var parameterKey = parameterKeySpan.ToString();
                if (parameters.TryGetValue(parameterKey, out var value))
                {
                    stringBuilder.Append(value);
                }
                else
                {
                    stringBuilder.Append("{{").Append(parameterKeySpan).Append("}}");
                }
                startIndex = closeBraceIndex + 2;
            }
            return stringBuilder.ToString();
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters)
        {
            if (agent.Tags.Any())
            {
                var userTags = await _requestAccessor.UserContext.GetTagsAsync();
                var userHasAnyTag = agent.Tags.Any(agentTag => userTags.Any(userTag => agentTag.Name == userTag.Name));
                if (!userHasAnyTag)
                {
                    var agentTagsNames = string.Join(", ", agent.Tags.Select(t => t.Name));
                    var noAccessText = $"NO ACCESS TAG [{agentTagsNames}]";
                    _responseAccessor.AddDebugMessage($"{agent.Name} ({agent.Type})", "Access Error", noAccessText);
                    _logger.LogWarning("User {Login} has no access to agent {AgentName} [{agentTagsNames}].", _requestAccessor.Login, agent.Name, agentTagsNames);
                    return noAccessText;
                }
            }
            if (_extendedConfig.LogAgentRun)
            {
                var parametersString = _extendedConfig.LogAgentPii
                    ? string.Join(", ", parameters.Select(p => $"{p.Key}: {p.Value}"))
                    : "[PII]";
                _logger.LogCritical("[{DateTime}][Run] {Login}, Action:{Action}, Agent: {Agent}, Parameters: {url}", DateTime.UtcNow.ToString("g"), _requestAccessor.Login, "ApiCall", agent.Name, parametersString);
            }
            var result = await DoCall(agent, parameters);

            if (_extendedConfig.LogAgentResult)
                _logger.LogCritical("[{DateTime}][Result] {Login}, Action:{Action}, Agent: {Agent}, Result: {url}", DateTime.UtcNow.ToString("g"), _requestAccessor.Login, "ApiCall", agent.Name,
                    _extendedConfig.LogAgentPii ? result : "[PII]");
            return result;
        }

        public abstract Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);

        protected ConnectionModel GetConnection(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            List<ConnectionModel> connections,
            ConnectionType connectionType,
            string debugMessageSenderName,
            int? connectionId = 0,
            string? connectionName = "")
        {
            return GetConnection(requestAccessor, responseAccessor, connections, new[]{connectionType} , debugMessageSenderName, connectionId, connectionName);
        }

        protected ConnectionModel GetConnection(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            List<ConnectionModel> connections, 
            ConnectionType[] connectionTypes, 
            string debugMessageSenderName,
            int? connectionId = 0,
            string? connectionName = "")
        {
            var connectionSpecified = connectionId > 0 || !string.IsNullOrEmpty(connectionName);
            // Check connection specified for Agent
            var connection = connections.FirstOrDefault(conn =>
                connectionTypes.Contains(conn.Type) &&
                (conn.ConnectionId == connectionId || conn.Name == connectionName));
            if (connection != null)
                return connection;

            // Check connection specified in Request
            connection = connections.FirstOrDefault(conn =>
                connectionTypes.Contains(conn.Type) && 
                requestAccessor.DefaultConnectionNames.Contains(conn.Name));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default from Request: {connection.Name}");
                return connection;
            }

            // Check just any connection
            connection = connections.FirstOrDefault(conn => connectionTypes.Contains(conn.Type));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default: {connection.Name}");
                return connection;
            }
            var connectionTypesString = string.Join(", ", connectionTypes.Select(e => e.ToString()));
            responseAccessor.AddDebugMessage(debugMessageSenderName, "Error", $"No any [{connectionTypesString}] connections found.");
            throw new Exception("No any LLM connections found.");
        }
    }
}
