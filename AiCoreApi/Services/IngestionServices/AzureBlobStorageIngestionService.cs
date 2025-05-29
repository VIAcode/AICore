using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using Azure.Storage.Blobs;
using Azure.Storage;

namespace AiCoreApi.Services.IngestionServices
{
    public class AzureBlobStorageIngestionService : IAzureBlobStorageIngestionService
    {
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILogger<AzureBlobStorageIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IEntraTokenProvider _entraTokenProvider;

        public AzureBlobStorageIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<AzureBlobStorageIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IEntraTokenProvider entraTokenProvider)
        {
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _entraTokenProvider = entraTokenProvider;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            await _taskProcessor.SetMessage(taskId, "Initializing Azure Blob Storage ingestion");
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            var storageAccountConnectionId = Convert.ToInt32(ingestion.Content["ConnectionId"]);

            var docIds = await IngestBlobs(ingestion, taskId, translateStepModel, embeddingConnectionModel);
            await RemoveDeletedFiles(embeddingConnectionModel, ingestion, docIds, taskId);

            await _taskProcessor.SetMessage(taskId, "Completed");
        }

        private async Task<BlobServiceClient> ConnectStorageAccount(ConnectionModel connection, string accountName, string accessType)
        {
            var blobEndpoint = $"https://{accountName}.blob.core.windows.net";
            if (accessType == "apiKey")
            {
                var accountKey = connection.Content["apiKey"];
                var storageCredentials = new StorageSharedKeyCredential(accountName, accountKey);
                return new BlobServiceClient(new Uri(blobEndpoint), storageCredentials);
            }
            else
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://storage.azure.com/.default");
                return new BlobServiceClient(new Uri(blobEndpoint), new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn));
            }
        }

        private async Task<HashSet<string>> IngestBlobs(IngestionModel ingestion, int taskId, 
            TranslateStepModel translateStepModel, EmbeddingConnectionModel embeddingConnectionModel)
        {
            var connection = await _dataIngestionHelperService.GetDataSourceConnection(ingestion, ConnectionType.StorageAccount, "ConnectionId");
            var accountName = connection.Content["accountName"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";
            var blobServiceClient = await ConnectStorageAccount(connection, accountName, accessType);

            var containerName = ingestion.Content["ContainerName"];
            var prefix = ingestion.Content.ContainsKey("Prefix") ? ingestion.Content["Prefix"] : string.Empty;

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobs = containerClient.GetBlobs(prefix: prefix).ToList();
            var docIds = new HashSet<string>();

            var i = 0;
            var count = blobs.Count();
            foreach (var blob in blobs)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Processing blob '{blob.Name}' [{i}/{count}]");

                var contentHash = Convert.ToBase64String(blob.Properties.ContentHash);
                var docId = ($"{accountName}/{containerName}/{blob.Name}").UniqueId();
                docIds.Add(docId);

                var fileInDatabase = _documentMetadataProcessor.Get(docId);
                if (fileInDatabase != null)
                {
                    if (fileInDatabase.Url?.Split("#")?.LastOrDefault() == contentHash)
                        continue;
                }

                var file = fileInDatabase ?? new DocumentMetadataModel(docId)
                {
                    IngestionId = ingestion.IngestionId,
                    Url = $"https://{accountName}.blob.core.windows.net/{containerName}/{blob.Name}#{contentHash}",
                    CreatedTime = DateTime.UtcNow,
                    Name = blob.Name
                };
                await _documentMetadataProcessor.Set(file);

                // Remove previous version before re-uploading
                if (fileInDatabase != null)
                {
                    await _fileIngestionClient.Delete(embeddingConnectionModel, docId);
                    await Task.Delay(10000);
                }

                // Ingest blob data
                var blobClient = containerClient.GetBlobClient(blob.Name);
                var blobDownloadInfo = await blobClient.DownloadAsync();                

                await _fileIngestionClient.Upload(embeddingConnectionModel, docId, blob.Name, 
                    blobDownloadInfo.Value.Content, ingestion.Tags.ToTagDictionary(), translateStepModel, blob.Properties.ContentLength);
                _logger.LogInformation($"Blob {blob.Name} uploaded successfully.");

                file.ImportFinished = true;
                await _documentMetadataProcessor.Set(file);
            }

            return docIds;
        }

        private async Task RemoveDeletedFiles(EmbeddingConnectionModel embeddingConnectionModel, IngestionModel ingestion, HashSet<string> currentDocIds, int taskId)
        {
            var filesInDatabase = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
            var toRemove = filesInDatabase.Where(f => !currentDocIds.Contains(f.DocumentId)).ToList();
            int i = 0;
            foreach (var file in toRemove)
            {
                i++;
                try
                {
                    await _taskProcessor.SetMessage(taskId, $"Removing deleted file '{file.Name}' [{i}/{toRemove.Count}]");
                    await _fileIngestionClient.Delete(embeddingConnectionModel, file.DocumentId);
                    await _documentMetadataProcessor.Remove(file);
                    _logger.LogInformation($"Deleted file '{file.Name}' successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to delete file '{file.Name}' from memory or metadata.");
                }
            }
        }
    }

    public interface IAzureBlobStorageIngestionService : IDataIngestionWorker
    {
    }
}
