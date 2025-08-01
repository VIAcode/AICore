using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.IngestionServices
{
    internal class WebUrlIngestionService: IWebUrlIngestionService
    {
        private readonly ILogger<WebUrlIngestionService> _logger;
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;

        public WebUrlIngestionService(
            ILogger<WebUrlIngestionService> logger,
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IHttpClientFactory httpClientFactory,
            IDataIngestionHelperService dataIngestionHelperService)
        {
            _logger = logger;
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _httpClientFactory = httpClientFactory;
            _dataIngestionHelperService = dataIngestionHelperService;
        }
        
        public async Task Process(IngestionModel ingestion, int taskId)
        {
            var metadata = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
            var url = ingestion.Content["Url"];
            var documentId = ingestion.Content["Url"].UniqueId();
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);

            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            // Process URL just once
            var documentMetadata = metadata.Find(x => x.DocumentId == documentId);
            if (documentMetadata != null)
                return;

            documentMetadata = new DocumentMetadataModel(documentId)
            {
                IngestionId = ingestion.IngestionId,
                Name = string.IsNullOrWhiteSpace(ingestion.Name) ? url : ingestion.Name,
                Url = url,
                CreatedTime = DateTime.UtcNow,
                LastModifiedTime = DateTime.UtcNow,
            };
            try
            {
                await _fileIngestionClient.Upload(embeddingConnectionModel, documentId, url, ingestion.Tags.ToTagDictionary(), translateStepModel);
                await _documentMetadataProcessor.Set(documentMetadata);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to import url: {url}.");
            }
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            throw new NotImplementedException();
        }

        public async Task<string> GetFileByPath(IngestionModel ingestion, string filePath)
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var uri = new Uri(filePath);
            try
            {
                var response = await httpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file by path: {filePath}");
                throw;
            }
        }

        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            throw new NotImplementedException();
        }
    }

    public interface IWebUrlIngestionService : IDataIngestionWorker
    {
    }
}
