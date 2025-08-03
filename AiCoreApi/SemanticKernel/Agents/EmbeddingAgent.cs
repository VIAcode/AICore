using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using System.Text.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class EmbeddingAgent : BaseAgent, IEmbeddingAgent
    {
        private static class AgentContentParameters
        {
            public const string EmbeddingConnectionName = "embeddingConnection";
            public const string Text = "text";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEmbeddingProcessor _embeddingProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public EmbeddingAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            IEmbeddingProcessor embeddingProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<EmbeddingAgent> logger
        ) : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _embeddingProcessor = embeddingProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            var connectionName = agent.Content[AgentContentParameters.EmbeddingConnectionName].Value;
            var inputText = await GetParameterValueAsync(AgentContentParameters.Text);

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, agent.Name, connectionName: connectionName);

            _responseAccessor.AddDebugMessage(agent.Name, "DoCall Request", inputText);
            var embedding = await _embeddingProcessor.GetEmbeddingAsync(connection, inputText);

            var json = JsonSerializer.Serialize(embedding);
            _responseAccessor.AddDebugMessage(agent.Name, "DoCall Response", json);

            return json;
        }
    }

    public interface IEmbeddingAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
