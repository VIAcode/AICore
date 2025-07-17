using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI.AzureOpenAI;
using Microsoft.KernelMemory.MemoryDb.AzureAISearch;
using Microsoft.KernelMemory.MemoryDb.Qdrant;
using Microsoft.KernelMemory.MemoryStorage;

namespace AiCoreApi.Services.IngestionServices
{
    public class TagService : ITagService
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDocumentMetadataProcessor _documentProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly Config _config;
        private readonly ILogger<TagService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;

        public TagService(
            IIngestionProcessor ingestionProcessor,
            IDocumentMetadataProcessor documentProcessor,
            ITaskProcessor taskProcessor,
            Config config,
            ILogger<TagService> logger,
            IDataIngestionHelperService dataIngestionHelperService)
        {
            _ingestionProcessor = ingestionProcessor;
            _documentProcessor = documentProcessor;
            _taskProcessor = taskProcessor;
            _config = config;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
        }

        public async Task Process(int ingestionId, int taskId, string payload)
        {
            var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId)
                ?? throw new InvalidOperationException($"Data source '{ingestionId}' not found.");

            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingModel);

            var tags = ingestion.Tags.Select(t => t.Name).ToList();
            var azureConfig = GetAzureOpenAiConfig(embeddingConnection);
            var documents = _documentProcessor.GetByIngestion(ingestionId);

            IMemoryDb memoryDb = embeddingModel.ConnectionType switch
            {
                ConnectionTypeEnum.Qdrant => new QdrantMemory(new QdrantConfig { Endpoint = _config.QdrantUrl }, new AzureOpenAITextEmbeddingGenerator(azureConfig)),
                ConnectionTypeEnum.AzureAiSearch => new AzureAISearchMemory(GetAzureAiSearchConfig(embeddingModel), new AzureOpenAITextEmbeddingGenerator(azureConfig)),
                _ => throw new ApplicationException("Unsupported connection type")
            };

            await ProcessDocuments(memoryDb, documents, taskId, embeddingConnection.Content["indexName"], tags);
        }

        private AzureOpenAIConfig GetAzureOpenAiConfig(ConnectionModel connection) => new()
        {
            Auth = AzureOpenAIConfig.AuthTypes.APIKey,
            APIKey = connection.Content["apiKey"],
            Endpoint = connection.Content["endpoint"],
            Deployment = connection.Content["modelName"],
            MaxTokenTotal = int.Parse(connection.Content["maxTokens"])
        };

        private AzureAISearchConfig GetAzureAiSearchConfig(EmbeddingConnectionModel model)
        {
            var parts = model.ConnectionString.Split(';');
            return new AzureAISearchConfig
            {
                Endpoint = $"https://{parts[0]}.search.windows.net",
                APIKey = parts[1],
                UseHybridSearch = Convert.ToBoolean(parts[2]),
                Auth = AzureAISearchConfig.AuthTypes.APIKey
            };
        }

        private async Task ProcessDocuments(
            IMemoryDb memoryDb,
            List<DocumentMetadataModel> documents,
            int taskId,
            string indexName,
            List<string> tags)
        {
            var total = documents.Count;
            for (var i = 0; i < total; i++)
            {
                var document = documents[i];
                await _taskProcessor.SetMessage(taskId, $"Processing '{document.Name}' [{i + 1}/{total}]");

                try
                {
                    await foreach (var record in memoryDb.GetListAsync(indexName,
                        new List<MemoryFilter> { MemoryFilters.ByDocument(document.DocumentId) },
                        limit: int.MaxValue,
                        withEmbeddings: true))
                    {
                        record.Tags.Remove(AiCoreConstants.TagName);
                        record.Tags.Add(AiCoreConstants.TagName, tags!);
                        await memoryDb.UpsertAsync(indexName, record);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to update tags for '{document.Name}'.");
                }
            }
            await _taskProcessor.SetMessage(taskId, "Processing complete.");
        }
    }

    public interface ITagService : IIngestionDataService
    {
    }
}
