using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Text;

namespace AiCoreApi.SemanticKernel.Agents
{
    public abstract class BaseAgent
    {
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
                return await DoCall(agent, parameters);
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
            var connectionSpecified = connectionId > 0 || !string.IsNullOrEmpty(connectionName);
            // Check connection specified for Agent
            var connection = connections.FirstOrDefault(conn =>
                conn.Type == connectionType &&
                (conn.ConnectionId == connectionId || conn.Name == connectionName));
            if (connection != null)
                return connection;

            // Check connection specified in Request
            connection = connections.FirstOrDefault(conn => 
                conn.Type == connectionType && 
                requestAccessor.DefaultConnectionNames.Contains(conn.Name));
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default from Request: {connection.Name}");
                return connection;
            }

            // Check just any connection
            connection = connections.FirstOrDefault(conn => conn.Type == connectionType);
            if (connection != null)
            {
                if (connectionSpecified)
                    responseAccessor.AddDebugMessage(debugMessageSenderName, "Warning", $"Specified connection not found. Using default: {connection.Name}");
                return connection;
            }

            responseAccessor.AddDebugMessage(debugMessageSenderName, "Error", $"No any {connectionType} connections found.");
            throw new Exception("No any LLM connections found.");
        }
    }
}
