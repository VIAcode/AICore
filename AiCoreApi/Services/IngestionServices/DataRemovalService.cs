using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.Services.IngestionServices
{
    public class DataRemovalService : IDataRemovalService
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;

        public DataRemovalService(
            IIngestionProcessor ingestionProcessor,
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            IDataIngestionHelperService dataIngestionHelperService)
        {
            _ingestionProcessor = ingestionProcessor;
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _dataIngestionHelperService = dataIngestionHelperService;
        }

        public async Task Process(int ingestionId, int taskId, string payload)
        {
            var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId) ?? throw new InvalidOperationException($"Data source '{ingestionId}' not found.");
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            var documentMetadataModels = await _documentMetadataProcessor.GetByIngestion(ingestionId);
            var i = 0;
            foreach (var documentMetadataModel in documentMetadataModels)
            {
                await _taskProcessor.SetMessage(taskId, $"Processing '{documentMetadataModel.Name}' [{++i}/{documentMetadataModels.Count}]");
                await _documentMetadataProcessor.Remove(documentMetadataModel);
                await _fileIngestionClient.Delete(embeddingConnectionModel, documentMetadataModel.DocumentId);
            }
            await _taskProcessor.SetMessage(taskId, "Complete.");
            await _ingestionProcessor.Remove(ingestionId);
        }
    }

    public interface IDataRemovalService: IIngestionDataService
    {
    }
}
