using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;
using System.Web;
using AiCoreApi.Models.ViewModels;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseAgent : IDoCallWrapperAgent
    {
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly MonitoringConfig _monitoringConfig;
        private readonly IIngestionParametersHelper _parametersHelper;

        private readonly ILogger<BaseAgent> _logger;
        private Dictionary<string, string>? _parameters;
        private AgentModel _agent = new();

        protected BaseAgent(IBaseAgentHelper baseAgentHelper,
            ILogger<BaseAgent> logger)
        {
            _responseAccessor = baseAgentHelper.ResponseAccessor;
            _requestAccessor = baseAgentHelper.RequestAccessor;
            _monitoringConfig = baseAgentHelper.MonitoringConfig;
            _parametersHelper = baseAgentHelper.ParametersHelper;
            _logger = logger;
        }

        private static class AgentContentParameters
        {
            public const string ParameterDescription = "parameterDescription";
            public const string OutputDescription = "outputDescription";
        }

        public async Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions)
        {
            var functionName = agent.Name.ToCamelCase();
            var functionDescription = agent.Description;
            var outputDescription = agent.Content[AgentContentParameters.OutputDescription].Value;
            var parametersList = new List<KernelParameterMetadata>();
            if (agent.Content.ContainsKey(AgentContentParameters.ParameterDescription) && !string.IsNullOrWhiteSpace(agent.Content[AgentContentParameters.ParameterDescription].Value))
            {
                var parameterDescription = ParameterRecordModel.Parse(agent.Content[AgentContentParameters.ParameterDescription].Value);
                parametersList.AddRange(parameterDescription.Select((t, i) => new KernelParameterMetadata(name: $"parameter{i + 1}") { Description = t.Name, IsRequired = true }));
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

        protected async Task<string?> GetParameterValueAsync(string parameterName, string? defaultValue = "")
        {
            var text = _agent.Content.ContainsKey(parameterName) ? _agent.Content[parameterName].Value : string.Empty;

            if (string.IsNullOrEmpty(text))
                return defaultValue;

            if (_parameters == null || _parameters.Count == 0)
                return text;

            var result = await ApplyParametersAsync(text, null);
            result = await _parametersHelper.ApplySecret(result);
            return result;
        }

        protected async Task<string> ApplyParametersAsync(string text, Dictionary<string, string>? additionalParameters = null) => 
            await _parametersHelper.ApplyParametersAsync(text, $"{_agent.Name} ({_agent.Type})", _parameters!, additionalParameters);


        public virtual async Task OnAddUpdate(AgentModel agentModel)
        {
            // Do nothing, this method is for override in derived classes if needed
        }

        public virtual async Task OnDelete(AgentModel agentModel)
        {
            // Do nothing, this method is for override in derived classes if needed
        }

        public virtual async Task OnExport(AgentModel agentModel, Dictionary<int, Models.ViewModels.AgentModelProcessed> agentsToExport)
        {
            agentsToExport[agentModel.AgentId].Processed = true;
        }

        public virtual async Task OnImport(AgentModel agentModel, Dictionary<string, Models.ViewModels.AgentModelProcessed> agentsToImport)
        {
            agentsToImport[agentModel.Name].Processed = true;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters)
        {
            try
            {
                _responseAccessor.Level++;
                if (agent.Tags.Any())
                {
                    var userTags = await _requestAccessor.UserContext.GetTagsAsync();
                    var userHasAnyTag =
                        agent.Tags.Any(agentTag => userTags.Any(userTag => agentTag.Name == userTag.Name));
                    if (!userHasAnyTag)
                    {
                        var agentTagsNames = string.Join(", ", agent.Tags.Select(t => t.Name));
                        var noAccessText = $"NO ACCESS TAG [{agentTagsNames}]";
                        _responseAccessor.AddDebugMessage($"{agent.Name} ({agent.Type})", "Access Error", noAccessText);
                        _logger.LogWarning("User {Login} has no access to agent {AgentName} [{agentTagsNames}].",
                            _requestAccessor.Login, agent.Name, agentTagsNames);
                        return noAccessText;
                    }
                }

                if (_monitoringConfig.LogAgentRun)
                {
                    var parametersString = _monitoringConfig.LogAgentPii
                        ? string.Join(", ", parameters.Select(p => $"{p.Key}: {p.Value}"))
                        : "[PII]";
                    _logger.LogCritical("[{DateTime}][Run] {Login}, Action:{Action}, Agent: {Agent}, Parameters: {url}",
                        DateTime.UtcNow.ToString("g"), _requestAccessor.Login, "ApiCall", agent.Name, parametersString);
                }
                parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
                _parameters = parameters;
                _agent = agent;
                var result = await DoCall(agent, parameters);

                if (_monitoringConfig.LogAgentResult)
                    _logger.LogCritical("[{DateTime}][Result] {Login}, Action:{Action}, Agent: {Agent}, Result: {url}",
                        DateTime.UtcNow.ToString("g"), _requestAccessor.Login, "ApiCall", agent.Name,
                        _monitoringConfig.LogAgentPii ? result : "[PII]");
                return result;
            }
            finally
            {
                _responseAccessor.Level--;
            }
        }

        public abstract Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters);

        protected async Task<ConnectionModel> GetConnectionAsync(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            List<ConnectionModel> connections,
            ConnectionType connectionType,
            string debugMessageSenderName,
            int? connectionId = 0,
            string? connectionName = "")
        {
            return await GetConnectionAsync(requestAccessor, responseAccessor, connections, new[]{connectionType} , debugMessageSenderName, connectionId, connectionName);
        }

        protected async Task<ConnectionModel> GetConnectionAsync(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            List<ConnectionModel> connections, 
            ConnectionType[] connectionTypes, 
            string debugMessageSenderName,
            int? connectionId = 0,
            string? connectionName = "")
        {
            if (!string.IsNullOrEmpty(connectionName) && connectionName.Contains("{{"))
                connectionName = await ApplyParametersAsync(connectionName);

            var connectionSpecified = connectionId > 0 || !string.IsNullOrEmpty(connectionName);
            // Check connection specified for Agent
            var connection = connections.FirstOrDefault(conn =>
                connectionTypes.Contains(conn.Type) &&
                (conn.ConnectionId == connectionId || conn.Name == connectionName));
            if (connection != null)
                return await _parametersHelper.ApplySecrets(connection);

            // Check connection specified in Request
            connection = connections.FirstOrDefault(conn =>
                connectionTypes.Contains(conn.Type) && 
                requestAccessor.DefaultConnectionNames.Contains(conn.Name));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default from Request: {connection.Name}");
                return await _parametersHelper.ApplySecrets(connection);
            }

            // Check just any connection
            connection = connections.FirstOrDefault(conn => connectionTypes.Contains(conn.Type));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default: {connection.Name}");
                return await _parametersHelper.ApplySecrets(connection);
            }
            var connectionTypesString = string.Join(", ", connectionTypes.Select(e => e.ToString()));
            responseAccessor.AddDebugMessage(debugMessageSenderName, "Error", $"No any [{connectionTypesString}] connections found.");
            throw new Exception("No any LLM connections found.");
        }
    }

    public interface IDoCallWrapperAgent
    {
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
