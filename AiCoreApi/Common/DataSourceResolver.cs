using AiCoreApi.Data.Processors;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.Common
{
    public class DataSourceResolver : IDataSourceResolver
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDataIngestionWorkerFactory _workerFactory;
        private readonly RequestAccessor _requestAccessor;

        public DataSourceResolver(
            IIngestionProcessor ingestionProcessor,
            IDataIngestionWorkerFactory workerFactory,
            RequestAccessor requestAccessor)
        {
            _ingestionProcessor = ingestionProcessor;
            _workerFactory = workerFactory;
            _requestAccessor = requestAccessor;
        }

        public async Task<string?> ResolveFileAsync(string dsName, string path)
        {
            var ingestion = await _ingestionProcessor.Get(dsName, _requestAccessor.WorkspaceId);
            if (ingestion == null)
                return null;

            var worker = _workerFactory.GetService(ingestion);
            return await worker.GetFileByPath(ingestion, path);
        }
    }

    public interface IDataSourceResolver
    {
        Task<string?> ResolveFileAsync(string dsName, string path);
    }


}
