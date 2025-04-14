using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.IO.Compression;

namespace AiCoreApi.Services.IngestionServices
{
    public class FileUploadIngestionService : IFileUploadIngestionService
    {
        private readonly ILogger<FileUploadIngestionService> _logger;
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;

        public FileUploadIngestionService(
            ILogger<FileUploadIngestionService> logger,
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IIngestionProcessor ingestionProcessor,
            IDataIngestionHelperService dataIngestionHelperService)
        {
            _logger = logger;
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _ingestionProcessor = ingestionProcessor;
            _dataIngestionHelperService = dataIngestionHelperService;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            var metadata = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
            var file = ingestion.Content["File"];
            var fileName = ingestion.Content["FileName"];
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);

            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            // No file - no processing
            if (string.IsNullOrEmpty(file))
                return;

            var files = await GetFiles(file, fileName);
            foreach (var fileModel in files)
            {
                var documentId = fileModel.FileName.UniqueId();
                // Process File just once
                var documentMetadata = metadata.Find(x => x.DocumentId == documentId);
                if (documentMetadata != null)
                {
                    if (!documentMetadata.ImportFinished)
                        throw new ApplicationException("The file processing failed");
                }
                documentMetadata = new DocumentMetadataModel(documentId)
                {
                    IngestionId = ingestion.IngestionId,
                    Name = fileModel.FileName,
                    Url = "",
                    CreatedTime = DateTime.UtcNow,
                    LastModifiedTime = DateTime.UtcNow,
                };
                await _documentMetadataProcessor.Set(documentMetadata);
                try
                {
                    await _fileIngestionClient.Upload(embeddingConnectionModel, documentId, fileModel.FileName, fileModel.Content, ingestion.Tags.ToTagDictionary(), translateStepModel);
                    documentMetadata.ImportFinished = true;
                    await _documentMetadataProcessor.Set(documentMetadata);
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to import File: {fileModel.FileName}.");
                }
            }
            ingestion.Content["File"] = "";
            ingestion.LastSync = DateTime.UtcNow;
            await _ingestionProcessor.Set(ingestion, null);
        }

        private async Task<List<FileModel>> GetFiles(string base64File, string fileName)
        {
            // parameter started with something like:
            // data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64,BASE-64-FILE-HERE
            var fileParts = base64File.Split(';');
            var content = Convert.FromBase64String(fileParts[1].Remove(0, 7));

            var files = new List<FileModel>();

            if (fileParts[0] == "data:application/zip" || fileParts[0] == "data:application/x-zip" || fileParts[0] == "data:application/x-zip-compressed")
            {
                using (var stream = new MemoryStream(content))
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            using (var entryStream = entry.Open())
                            {
                                var subFileContent = new byte[entry.Length];
                                var totalBytesRead = 0;
                                while (totalBytesRead < content.Length)
                                {
                                    var bytesRead = await entryStream.ReadAsync(content.AsMemory(totalBytesRead, content.Length - totalBytesRead));
                                    if (bytesRead == 0)
                                    {
                                        // End of stream reached, break out of the loop
                                        break;
                                    }
                                    totalBytesRead += bytesRead;
                                }
                                if (totalBytesRead == content.Length)
                                {
                                    files.Add(new FileModel
                                    {
                                        FileName = entry.FullName,
                                        Content = subFileContent,
                                    });
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                files.Add(new FileModel
                {
                    FileName = fileName,
                    Content = content,
                });
            }
            return files;
        }

        public class FileModel
        {
            public string FileName { get; set; } = "";
            public byte[] Content { get; set; } = Array.Empty<byte>();
        }

    }

    public interface IFileUploadIngestionService : IDataIngestionWorker
    {
    }
}
