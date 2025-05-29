using AiCoreApi.Models.DbModels;
namespace AiCoreApi.Services.IngestionServices
{
    internal class DataIngestionWorkerFactory : IDataIngestionWorkerFactory
    {
        private readonly ISharePointIngestionService _sharePointIngestionService;
        private readonly IFileUploadIngestionService _fileUploadIngestionService;
        private readonly IWebUrlIngestionService _webUrlIngestionService;
        private readonly IAzDoWikiIngestionService _azDoWikiIngestionService;
        private readonly IAzureBlobStorageIngestionService _blobStorageIngestionService;

        public DataIngestionWorkerFactory(
            ISharePointIngestionService sharePointIngestionService,
            IFileUploadIngestionService fileUploadIngestionService,
            IWebUrlIngestionService webUrlIngestionService,
            IAzDoWikiIngestionService azDoWikiIngestionService,
            IAzureBlobStorageIngestionService blobStorageIngestionService)
        {
            _sharePointIngestionService = sharePointIngestionService;
            _fileUploadIngestionService = fileUploadIngestionService;
            _webUrlIngestionService = webUrlIngestionService;
            _azDoWikiIngestionService = azDoWikiIngestionService;
            _blobStorageIngestionService = blobStorageIngestionService;
        }

        public IDataIngestionWorker GetService(IngestionModel ingestion) => GetService(ingestion.Type);
        public IDataIngestionWorker GetService(IngestionType ingestionType)
        {
            switch (ingestionType)
            {
                case IngestionType.SharePoint:
                    return _sharePointIngestionService;
                case IngestionType.WebUrl:
                    return _webUrlIngestionService;
                case IngestionType.UploadFile:
                    return _fileUploadIngestionService;
                case IngestionType.AzDoWiki:
                    return _azDoWikiIngestionService;
                case IngestionType.AzureBlobStorage:
                    return _blobStorageIngestionService;
                default:
                    throw new InvalidOperationException($"Unsupported data source '{ingestionType}'.");
            }
        }
    }

    public interface IDataIngestionWorkerFactory
    {
        IDataIngestionWorker GetService(IngestionModel ingestion);
        IDataIngestionWorker GetService(IngestionType ingestionType);
    }

    public interface IDataIngestionWorker
    {
        Task Process(IngestionModel ingestion, int taskId);

        Task<List<string>> GetAutoComplete(string parameterName, IngestionModel ingestionModel) => Task.FromResult(new List<string>());
    }
}
