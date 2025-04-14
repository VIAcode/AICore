using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.IngestionServices
{
    public class DataIngestionHelperService : IDataIngestionHelperService
    {
        public static class Constants
        {
            public const string EmbeddingConnectionField = "EmbeddingConnection";
            public const string VectorDbConnectionField = "VectorDBConnectionName";
        }
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly Config _config;

        public DataIngestionHelperService(
            IConnectionProcessor connectionProcessor,
            Config config)
        {
            _connectionProcessor = connectionProcessor;
            _config = config;
        }

        private List<ConnectionModel>? _connectionModels;
        private async Task<List<ConnectionModel>> GetConnections(int? workspaceId) => _connectionModels ?? (_connectionModels = await _connectionProcessor.List(workspaceId));

        public async Task<ConnectionModel> GetEmbeddingConnection(IngestionModel ingestion)
        {
            var embeddingConnection = ingestion.Content.ContainsKey(Constants.EmbeddingConnectionField) ? ingestion.Content[Constants.EmbeddingConnectionField] : "";
            var connections = await GetConnections(ingestion.WorkspaceId);
            if (connections.Count == 0)
                throw new ApplicationException("No connections found");
            var connection = connections.Find(x => x.Type.IsEmbeddingConnection() && x.ConnectionId.ToString() == embeddingConnection);
            if (string.IsNullOrEmpty(embeddingConnection))
            {
                connection = connections.First(x => x.Type.IsEmbeddingConnection())!;
                ingestion.Content[Constants.EmbeddingConnectionField] = connection.Name;
            }
            if (connection == null)
                throw new ApplicationException("No connection found");
            return connection;
        }

        public async Task FillVectorDbConnection(IngestionModel ingestion, EmbeddingConnectionModel embeddingConnectionModel)
        {
            var vectorDbConnectionName = ingestion.Content.ContainsKey(Constants.VectorDbConnectionField) ? ingestion.Content[Constants.VectorDbConnectionField] : "";
            if (string.IsNullOrEmpty(vectorDbConnectionName) || vectorDbConnectionName == "0")
            {
                embeddingConnectionModel.ConnectionType = ConnectionTypeEnum.Qdrant;
                embeddingConnectionModel.ConnectionString = _config.QdrantUrl;
                return;
            }
            var connections = await GetConnections(ingestion.WorkspaceId);
            if (connections.Count == 0)
                throw new ApplicationException("No connections found");
            var connection = connections.Find(x => x.Type == ConnectionType.AzureAiSearch && x.ConnectionId.ToString() == vectorDbConnectionName);
            if (connection == null)
                throw new ApplicationException("No connection found");
            embeddingConnectionModel.ConnectionType = ConnectionTypeEnum.AzureAiSearch;
            var useHybridSearch = connection.Content.ContainsKey("useHybridSearch") && connection.Content["useHybridSearch"].ToLower() == "true";
            embeddingConnectionModel.ConnectionString = $"{connection.Content["resourceName"]};{connection.Content["apiKey"]};{useHybridSearch}";
        }

        public async Task<TranslateStepModel> GetTranslateStepModel(IngestionModel ingestion)
        {
            if (!ingestion.Content.ContainsKey("TranslateStepEnabled"))
                return new TranslateStepModel();

            var result = new TranslateStepModel();
            result.Enabled = ingestion.Content["TranslateStepEnabled"].ToLower() == "true";
            if (!result.Enabled)
                return result;

            result.TargetLanguage = ingestion.Content["TranslateStepTargetLanguage"];
            if(string.IsNullOrEmpty(result.TargetLanguage))
                return new TranslateStepModel();

            var connections = await GetConnections(ingestion.WorkspaceId);
            if (connections.Count == 0)
                throw new ApplicationException("No connections found");
            var aiTranslatorConnectionName = ingestion.Content["TranslateStepConnection"];
            var connection = connections.FirstOrDefault(x => x.Type == ConnectionType.AzureAiTranslator && x.ConnectionId.ToString() == aiTranslatorConnectionName);
            if(connection == null)
                return new TranslateStepModel(); // Connection not found => connection was deleted => return empty model
            result.ApiKey = connection.Content["apiKey"];
            result.Region = connection.Content["region"];
            return result;
        }
    }

    public interface IDataIngestionHelperService
    {
        Task<ConnectionModel> GetEmbeddingConnection(IngestionModel ingestion);
        Task FillVectorDbConnection(IngestionModel ingestion, EmbeddingConnectionModel embeddingConnectionModel);
        Task<TranslateStepModel> GetTranslateStepModel(IngestionModel ingestion);
    }
}
