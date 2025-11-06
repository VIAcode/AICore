using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Text;
using AiCoreApi.Common.Monitoring;
using System.Web;
using AiCoreApi.Services.IngestionServices;
using AiCoreApi.Data.Processors;
using System.Text.RegularExpressions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseAgent : IDoCallWrapperAgent
    {
        private readonly ResponseAccessor _responseAccessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly MonitoringConfig _monitoringConfig;
        private readonly IDataIngestionWorkerFactory _dataIngestionWorkerFactory;
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly ICacheAccessor _cacheAccessor;
        private readonly IEntraTokenProvider _entraTokenProvider;

        private readonly ILogger<BaseAgent> _logger;
        private Dictionary<string, string>? _parameters;
        private AgentModel _agent = new();

        protected BaseAgent(IBaseAgentHelper baseAgentHelper,
            ILogger<BaseAgent> logger)
        {
            _responseAccessor = baseAgentHelper.ResponseAccessor;
            _requestAccessor = baseAgentHelper.RequestAccessor;
            _monitoringConfig = baseAgentHelper.MonitoringConfig;
            _dataIngestionWorkerFactory = baseAgentHelper.DataIngestionWorkerFactory;
            _cacheAccessor = baseAgentHelper.CacheAccessor;
            _entraTokenProvider = baseAgentHelper.EntraTokenProvider;
            _ingestionProcessor = baseAgentHelper.IngestionProcessor;
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
            result = await ApplySecret(result);
            return result;
        }

        protected async Task<string> ApplyParametersAsync(string text, Dictionary<string, string>? additionalParameters = null)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            var resolvedDsCache = new Dictionary<string, string>();

            while (i < text.Length)
            {
                // Handle escaped braces: \{{ or \}}
                if (i + 2 < text.Length && text[i] == '\\' && text[i + 1] == '{' && text[i + 2] == '{')
                {
                    sb.Append("{{");
                    i += 3;
                    continue;
                }
                if (i + 2 < text.Length && text[i] == '\\' && text[i + 1] == '}' && text[i + 2] == '}')
                {
                    sb.Append("}}");
                    i += 3;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
                {
                    int start = i + 2;
                    int braceDepth = 1;
                    int j = start;

                    while (j < text.Length - 1)
                    {
                        if (text[j] == '{' && text[j + 1] == '{')
                        {
                            braceDepth++;
                            j += 2;
                        }
                        else if (text[j] == '}' && text[j + 1] == '}')
                        {
                            braceDepth--;
                            j += 2;
                            if (braceDepth == 0) break;
                        }
                        else
                        {
                            j++;
                        }
                    }

                    if (braceDepth != 0 || j > text.Length)
                    {
                        sb.Append(text.Substring(i));
                        break;
                    }

                    string key = text.Substring(start, j - start - 2).Trim(); 
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        sb.Append("{{}}");
                        i = j;
                        continue;
                    }

                    string? value = null;

                    if (_parameters.TryGetValue(key, out var paramValue))
                    {
                        value = paramValue;
                    }
                    else if (additionalParameters != null && additionalParameters.TryGetValue(key, out var extra))
                    {
                        value = extra;
                    }
                    else if (key.StartsWith("DS:"))
                    {
                        if (!resolvedDsCache.TryGetValue(key, out value!))
                        {
                            value = await ResolveDataSourceValueAsync(key);
                            resolvedDsCache[key] = value;
                        }
                    }

                    sb.Append(value ?? $"{{{{{key}}}}}");

                    i = j;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            return sb.ToString();
        }


        private async Task<string?> ResolveDataSourceValueAsync(string key)
        {
            var parts = key.Split(':');
            if (parts.Length < 3 || parts.Length > 4)
                return "Invalid Data Source parameters count";

            var dsName = parts[1];
            var dsPath = parts[2];
            var cacheSeconds = (parts.Length == 4 && int.TryParse(parts[3], out var c)) ? c : 0;
            var cacheKey = $"{HttpUtility.UrlEncode(dsName)}_{HttpUtility.UrlEncode(dsPath)}";

            var cachedValue = _cacheAccessor.GetCacheValue(cacheKey);
            if (!string.IsNullOrEmpty(cachedValue))
                return cachedValue;

            var ingestion = await _ingestionProcessor.Get(dsName, _requestAccessor.WorkspaceId);
            if (ingestion == null)
                return $"Data Source '{dsName}' not found";

            var worker = _dataIngestionWorkerFactory.GetService(ingestion);
            var file = await worker.GetFileByPath(ingestion, dsPath);
            if (string.IsNullOrEmpty(file))
                return $"File '{dsPath}' not found in Data Source '{dsName}'";

            if (cacheSeconds > 0)
                _cacheAccessor.SetCacheValue(cacheKey, file, cacheSeconds);

            return file;
        }

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
                return await ApplySecrets(connection);

            // Check connection specified in Request
            connection = connections.FirstOrDefault(conn =>
                connectionTypes.Contains(conn.Type) && 
                requestAccessor.DefaultConnectionNames.Contains(conn.Name));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default from Request: {connection.Name}");
                return await ApplySecrets(connection);
            }

            // Check just any connection
            connection = connections.FirstOrDefault(conn => connectionTypes.Contains(conn.Type));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default: {connection.Name}");
                return await ApplySecrets(connection);
            }
            var connectionTypesString = string.Join(", ", connectionTypes.Select(e => e.ToString()));
            responseAccessor.AddDebugMessage(debugMessageSenderName, "Error", $"No any [{connectionTypesString}] connections found.");
            throw new Exception("No any LLM connections found.");
        }

        protected async Task<ConnectionModel> ApplySecrets(ConnectionModel connectionModel)
        {
            var keys = connectionModel.Content.Keys.ToList();
            foreach (var key in keys)
            {
                connectionModel.Content[key] = await ApplySecret(connectionModel.Content[key]);
            }
            return connectionModel;
        }

        protected async Task<string> ApplySecret(string value)
        {
            var regex = new Regex(@"\{\{secret:(?<name>[^}]+)\}\}", RegexOptions.Compiled);
            if (string.IsNullOrEmpty(value))
                return value;
            var matches = regex.Matches(value);
            if (matches.Count == 0)
                return value;
            foreach (Match match in matches)
            {
                var secretName = match.Groups["name"].Value.Trim();
                var secretValue = await _entraTokenProvider.GetSecretFromKeyVaultAsync(secretName);
                value = value.Replace(match.Value, secretValue);
            }
            return value;
        }
    }

    public interface IDoCallWrapperAgent
    {
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
