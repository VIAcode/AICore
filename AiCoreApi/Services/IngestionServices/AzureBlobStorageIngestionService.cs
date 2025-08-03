using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using Azure.Storage.Blobs;
using Azure.Storage;
using System.Security.Cryptography;
using System.Text;
using Microsoft.KernelMemory;

namespace AiCoreApi.Services.IngestionServices
{
    public class AzureBlobStorageIngestionService : IAzureBlobStorageIngestionService
    {
        private const int DelayOnReUploadSeconds = 10;

        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILogger<AzureBlobStorageIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;
        private readonly IConnectionProcessor _connectionProcessor;

        public AzureBlobStorageIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<AzureBlobStorageIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IEntraTokenProvider entraTokenProvider,
            IKernelMemoryProvider kernelMemoryProvider,
            IConnectionProcessor connectionProcessor)
        {
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _entraTokenProvider = entraTokenProvider;
            _kernelMemoryProvider = kernelMemoryProvider;
            _connectionProcessor = connectionProcessor;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            await _taskProcessor.SetMessage(taskId, "Initializing Azure Blob Storage ingestion");
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            var connections = await _connectionProcessor.List(ingestion.WorkspaceId);
            var llmConnection = connections.FirstOrDefault(x => x.Type.IsLlmConnection()); // Assuming there's a default LLM connection
            var vectorDbConnectionId = ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.VectorDbConnectionField) ? ingestion.Content[DataIngestionHelperService.Constants.VectorDbConnectionField] : "";

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null
                : connections.FirstOrDefault(x => x.ConnectionId.ToString() == vectorDbConnectionId);
            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);

            var docIds = await IngestBlobs(ingestion, taskId, translateStepModel, embeddingConnectionModel, kernelMemory);
            await RemoveDeletedFiles(embeddingConnectionModel, ingestion, docIds, taskId);

            await _taskProcessor.SetMessage(taskId, "Completed");
        }

        public async Task<string> GetFileByPath(IngestionModel ingestion, string path)
        {
            var connection = await _dataIngestionHelperService.GetDataSourceConnection(ingestion, ConnectionType.StorageAccount, "ConnectionId");
            var accountName = connection.Content["accountName"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";

            var parts = path.Split(',');
            if (parts.Length != 2)
                throw new ArgumentException("Invalid path format. Expected format: 'ContainerName,BlobName'.");
            var containerName = parts[0].Trim();
            var blobName = parts[1].Trim();

            var blobServiceClient = await ConnectStorageAccount(connection, accountName, accessType);
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (!await blobClient.ExistsAsync())
                throw new InvalidOperationException($"Blob '{blobName}' not found in container '{containerName}'.");

            var downloadInfo = await blobClient.DownloadContentAsync();
            // Try to return as text; fallback to Base64 if not UTF-8
            try
            {
                return downloadInfo.Value.Content.ToString();
            }
            catch
            {
                return Convert.ToBase64String(downloadInfo.Value.Content.ToArray());
            }
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            try
            {
                var metadata = _documentMetadataProcessor.Get(fileId) ?? throw new InvalidOperationException($"File with id '{fileId}' not found in metadata.");
                var containerName = ingestion.Content["ContainerName"];
                var blobName = metadata.Name;
                return await GetFileByPath(ingestion, $"{containerName},{blobName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file '{fileId}' from Azure Blob Storage.");
                throw;
            }
        }

        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            try
            {
                var connection = await _dataIngestionHelperService.GetDataSourceConnection(ingestion, ConnectionType.StorageAccount, "ConnectionId");
                var accountName = connection.Content["accountName"];
                var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";

                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File with id '{fileId}' not found in metadata.");

                var containerName = ingestion.Content["ContainerName"];
                var blobName = metadata.Name;

                var blobServiceClient = await ConnectStorageAccount(connection, accountName, accessType);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(articleText));
                await blobClient.UploadAsync(stream, overwrite: true);

                _logger.LogInformation($"Updated blob '{blobName}' in container '{containerName}'.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set file '{fileId}' in Azure Blob Storage.");
                throw;
            }
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
            TranslateStepModel translateStepModel, EmbeddingConnectionModel embeddingConnectionModel, IKernelMemory kernelMemory)
        {
            var connection = await _dataIngestionHelperService.GetDataSourceConnection(ingestion, ConnectionType.StorageAccount, "ConnectionId");
            var accountName = connection.Content["accountName"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";
            var blobServiceClient = await ConnectStorageAccount(connection, accountName, accessType);

            var containerName = ingestion.Content["ContainerName"];
            var prefix = ingestion.Content.ContainsKey("Prefix") ? ingestion.Content["Prefix"] : string.Empty;

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var docIds = new HashSet<string>();

            int i = 0;
            var blobs = containerClient.GetBlobs(prefix: prefix).ToList();

            foreach (var blob in blobs)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Processing blob '{blob.Name}' [{i}/{blobs.Count}]");

                var contentHash = blob.Properties.ContentHash != null
                    ? Convert.ToBase64String(blob.Properties.ContentHash)
                    : ComputeHash(blob.Name + blob.Properties.LastModified);

                var docId = ($"{accountName}/{containerName}/{blob.Name}").UniqueId();
                docIds.Add(docId);

                var fileInDatabase = _documentMetadataProcessor.Get(docId);
                if (fileInDatabase != null && fileInDatabase.Url?.Split("#").LastOrDefault() == contentHash)
                {
                    var search = await kernelMemory.SearchAsync("",
                        embeddingConnectionModel.IndexName,
                        filter: new MemoryFilter().ByDocument(docId));
                    if (search.Results.Count > 0) // If the file already exists in the vector DB, skip re-upload
                        continue;
                }

                var file = fileInDatabase ?? new DocumentMetadataModel(docId)
                {
                    IngestionId = ingestion.IngestionId,
                    CreatedTime = DateTime.UtcNow,
                };
                file.Name = blob.Name;
                file.Url = $"https://{accountName}.blob.core.windows.net/{containerName}/{blob.Name}#{contentHash}";

                await _documentMetadataProcessor.Set(file);

                if (fileInDatabase != null)
                {
                    await _fileIngestionClient.Delete(embeddingConnectionModel, docId);
                    await Task.Delay(TimeSpan.FromSeconds(DelayOnReUploadSeconds));
                }

                var blobClient = containerClient.GetBlobClient(blob.Name);
                var blobDownloadInfo = await blobClient.DownloadAsync();

                await _fileIngestionClient.Upload(
                    embeddingConnectionModel, docId, blob.Name,
                    blobDownloadInfo.Value.Content,
                    ingestion.Tags.ToTagDictionary(),
                    translateStepModel,
                    blob.Properties.ContentLength ?? 0);

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

        private string ComputeHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            var hashBytes = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hashBytes);
        }
    }

    public interface IAzureBlobStorageIngestionService : IDataIngestionWorker
    {
    }
}
