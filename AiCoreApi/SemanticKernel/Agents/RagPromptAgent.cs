using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Models.ViewModels;
using Microsoft.KernelMemory;
using AiCoreApi.Data.Processors;
using System.Web;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class RagPromptAgent : BaseAgent, IRagPromptAgent
    {
        private string _debugMessageSenderName = "RagPromptAgent";

        private static class AgentContentParameters
        {
            public const string Question = "question";
            public const string Prompt = "prompt";
            public const string Tags = "tags";
            public const string MinRelevance = "minRelevance";
            public const string EmbeddingConnectionName = "embeddingConnection";
            public const string VectorDBConnectionName = "vectorDBConnection";
        }
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IFeatureFlags _featureFlags;
        private readonly ExtendedConfig _extendedConfig;

        public RagPromptAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IKernelMemoryProvider kernelMemoryProvider,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IConnectionProcessor connectionProcessor,
            ILoginProcessor loginProcessor,
            IFeatureFlags featureFlags,
            ExtendedConfig extendedConfig,
            ILogger<RagPromptAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _kernelMemoryProvider = kernelMemoryProvider;
            _documentMetadataProcessor = documentMetadataProcessor;
            _connectionProcessor = connectionProcessor;
            _loginProcessor = loginProcessor;
            _featureFlags = featureFlags;
            _extendedConfig = extendedConfig;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var question = ApplyParameters(agent.Content[AgentContentParameters.Question].Value, parameters);
            var embeddingConnectionName = agent.Content[AgentContentParameters.EmbeddingConnectionName].Value;
            var vectorDbConnectionName = agent.Content.ContainsKey(AgentContentParameters.VectorDBConnectionName)
                ? agent.Content[AgentContentParameters.VectorDBConnectionName].Value
                : "";
            var prompt = agent.Content[AgentContentParameters.Prompt].Value;
            _responseAccessor.StepState = prompt;
            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags)
                ? agent.Content[AgentContentParameters.Tags].Value
                : "";
            var minRelevance = Convert.ToDouble(
                agent.Content.ContainsKey(AgentContentParameters.MinRelevance) 
                    ? agent.Content[AgentContentParameters.MinRelevance].Value 
                    : "0");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Question: {question}\r\n# Prompt: {prompt}\r\n# Tags: {tags}\r\n# MinRelevance: {minRelevance}");

            var allUserTags = await _loginProcessor.GetTagsByLogin(_requestAccessor.Login, _requestAccessor.LoginType);
            var agentTags = string.IsNullOrEmpty(tags)
                ? allUserTags.Where(e => _requestAccessor.Tags.Contains(e.TagId)).Select(e => e.Name.ToLower()).ToArray()
                : tags.ToLower().Split(',');

            var filters = allUserTags
                .Where(e => (string.IsNullOrEmpty(tags) && _requestAccessor.Tags.Count == 0) || agentTags.Contains(e.Name.ToLower()))
                .Select(e => MemoryFilters.ByTag(AiCoreConstants.TagName, e.Name))
                .ToList();

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionName) || vectorDbConnectionName == "Internal Qdrant")
                ? null
                : GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiSearch, _debugMessageSenderName, connectionName: vectorDbConnectionName);
            var embeddingConnection = GetConnection(_requestAccessor, _responseAccessor, connections, 
                new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, _debugMessageSenderName, connectionName: embeddingConnectionName);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections, 
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName, agent.LlmType);
            var vectorIndexName = embeddingConnection.Content.ContainsKey("indexName")
                ? embeddingConnection.Content["indexName"]
                : "default";

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);
            if(filters.Count == 0 && _featureFlags.IsEnabled(FeatureFlags.Names.Tagging))
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"{_extendedConfig.NoInformationFoundText} (filters)");
                return _extendedConfig.NoInformationFoundText;
            }

            var answer = await kernelMemory.AskAsync(question, minRelevance: minRelevance,
                index: vectorIndexName,
                filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);
            if (answer.NoResult)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _extendedConfig.NoInformationFoundText);
                return _extendedConfig.NoInformationFoundText;
            }
            _responseAccessor.CurrentMessage.Text = answer.Result;
            _responseAccessor.CurrentMessage.Sources = answer.RelevantSources.Select(s =>
            {
                var documentMetadata = _documentMetadataProcessor.Get(s.DocumentId);
                if (documentMetadata == null)
                {
                    return new MessageDialogViewModel.MessageSource
                    {
                        Name = s.SourceUrl,
                        Url = s.SourceUrl
                    };
                }

                return new MessageDialogViewModel.MessageSource
                {
                    Name = documentMetadata.Name,
                    Url = documentMetadata.Url
                };
            })
                .GroupBy(x => $"{x.Url}|{x.Name}")
                .Select(x => x.First())
                .ToList();
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _responseAccessor.CurrentMessage.Text);
            return _responseAccessor.CurrentMessage.Text;
        }
    }

    public interface IRagPromptAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
