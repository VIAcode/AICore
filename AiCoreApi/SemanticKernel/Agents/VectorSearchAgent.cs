using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Models.ViewModels;
using Microsoft.KernelMemory;
using AiCoreApi.Data.Processors;
using System.Web;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class VectorSearchAgent : BaseAgent, IVectorSearchAgent
    {
        private string _debugMessageSenderName = "VectorSearchAgent";

        private static class AgentContentParameters
        {
            public const string QueryString = "queryString";
            public const string Tags = "tags";
            public const string MinRelevance = "minRelevance";
            public const string MaxResultsCount = "maxResultsCount";
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

        public VectorSearchAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IKernelMemoryProvider kernelMemoryProvider,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IConnectionProcessor connectionProcessor,
            ILoginProcessor loginProcessor,
            IFeatureFlags featureFlags,
            ExtendedConfig extendedConfig,
            ILogger<VectorSearchAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _kernelMemoryProvider = kernelMemoryProvider;
            _documentMetadataProcessor = documentMetadataProcessor;
            _connectionProcessor = connectionProcessor;
            _loginProcessor = loginProcessor;
            _featureFlags = featureFlags;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var queryString = ApplyParameters(agent.Content[AgentContentParameters.QueryString].Value, parameters);
            var embeddingConnectionName = agent.Content[AgentContentParameters.EmbeddingConnectionName].Value;
            var vectorDbConnectionName = agent.Content.ContainsKey(AgentContentParameters.VectorDBConnectionName)
                ? agent.Content[AgentContentParameters.VectorDBConnectionName].Value
                : "";
            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags)
                ? agent.Content[AgentContentParameters.Tags].Value
                : "";
            var minRelevance = Convert.ToDouble(
                agent.Content.ContainsKey(AgentContentParameters.MinRelevance) 
                    ? agent.Content[AgentContentParameters.MinRelevance].Value 
                    : "0");
            var maxResultsCount = Convert.ToInt32(
                agent.Content.ContainsKey(AgentContentParameters.MaxResultsCount)
                    ? agent.Content[AgentContentParameters.MaxResultsCount].Value
                    : "-1");

            return await Search(queryString, maxResultsCount, minRelevance,  tags, embeddingConnectionName, vectorDbConnectionName, agent.LlmType);
        }

        public async Task<string> Search(string queryString, int maxResultsCount, double minRelevance, string tags, string embeddingConnectionName, string vectorDbConnectionName, int? llmConnectionId)
        {
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# VectorDb Connection: {vectorDbConnectionName}\r\n# QueryString: {queryString}\r\n# Tags: {tags}\r\n# MinRelevance: {minRelevance}");

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
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName, llmConnectionId);
            var vectorIndexName = embeddingConnection.Content.ContainsKey("indexName")
                ? embeddingConnection.Content["indexName"]
                : "default";

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);
            if (filters.Count == 0 && _featureFlags.IsEnabled(FeatureFlags.Names.Tagging))
            {
                filters.Add(MemoryFilters.ByTag(AiCoreConstants.TagName, "Non-existing Tag"));
            }
            var searchResults = await kernelMemory.SearchAsync(queryString, minRelevance: minRelevance, 
                index: vectorIndexName,
                limit: maxResultsCount,
                filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);
            var result = searchResults.Results.Select(citation =>
            {
                var documentMetadata = _documentMetadataProcessor.Get(citation.DocumentId);
                return new SearchItemModel
                {
                    SourceName = documentMetadata.Name,
                    Link = documentMetadata.Url,
                    CreateTime = documentMetadata.CreatedTime,
                    UpdatedTime = documentMetadata.LastModifiedTime,
                    SourceContentType = citation.SourceContentType,
                    Texts = citation.Partitions.Select(partition => new SearchItemPartitionTextModel
                    {
                        Text = partition.Text,
                        Relevance = partition.Relevance,
                        PartNumber = Convert.ToInt32(partition.Tags.FirstOrDefault(tag => tag.Key.StartsWith("__part_n")).Value.FirstOrDefault())
                    }).ToList(),
                };
            }).ToList();
            _responseAccessor.CurrentMessage.Text = result.ToJson();
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _responseAccessor.CurrentMessage.Text);
            return _responseAccessor.CurrentMessage.Text;
        }
    }

    public interface IVectorSearchAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> Search(string queryString, int maxResultsCount, double minRelevance, string tags, string embeddingConnectionName, string vectorDbConnectionName, int? llmConnectionId);
    }
}
