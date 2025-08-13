using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.IngestionServices
{
    internal class DataIngestionService : IDataIngestionService
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDataIngestionWorkerFactory _ingestionWorkerFactory;

        public DataIngestionService(
            IIngestionProcessor ingestionProcessor,
            IDataIngestionWorkerFactory ingestionWorkerFactory)
        {
            _ingestionProcessor = ingestionProcessor;
            _ingestionWorkerFactory = ingestionWorkerFactory;
        }

        public async Task Process(int ingestionId, int taskId, string payload)
        {
            var ingestion =
                await _ingestionProcessor.GetIngestionById(ingestionId) ??
                throw new InvalidOperationException($"Data source '{ingestionId}' not found.");

            var service = _ingestionWorkerFactory.GetService(ingestion);
            if (!ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.AutoSyncField) ||
                ingestion.Content[DataIngestionHelperService.Constants.AutoSyncField].ToLower() == "true")
            {
                await service.Process(ingestion, taskId);
            }
            await _ingestionProcessor.SetSyncTime(ingestionId, DateTime.UtcNow);
        }

        public async Task<List<string>> GetAutoComplete(string parameterName, IngestionModel ingestionModel)
        {
            var service = _ingestionWorkerFactory.GetService(ingestionModel.Type);
            return await service.GetAutoComplete(parameterName, ingestionModel);
        }
    }

    public interface IDataIngestionService : IIngestionDataService
    {
        Task<List<string>> GetAutoComplete(string parameterName, IngestionModel ingestionViewModel);
    }
}
