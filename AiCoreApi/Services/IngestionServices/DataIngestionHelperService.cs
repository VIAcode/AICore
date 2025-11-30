using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.IngestionServices
{
    public class DataIngestionHelperService : IDataIngestionHelperService
    {
        public static class Constants
        {
            public const string EmbeddingConnectionField = "EmbeddingConnection";
            public const string VectorDbConnectionField = "VectorDBConnectionName";
            public const string AutoSyncField = "AutoSync";

            public const string TranslateEnabled = "TranslateStepEnabled";
            public const string TranslateConnection = "TranslateStepConnection";
            public const string TranslateTargetLanguage = "TranslateStepTargetLanguage";
        }

        private readonly IConnectionManager _connectionManager;
        private readonly Config _config;

        public DataIngestionHelperService(IConnectionManager connectionManager, Config config)
        {
            _connectionManager = connectionManager;
            _config = config;
        }

        private static int GetIntFromContent(Dictionary<string, string> content, string field)
        {
            if (content.TryGetValue(field, out var raw) && int.TryParse(raw, out var value))
                return value;

            return 0;
        }

        private static bool GetBoolFromContent(Dictionary<string, string> content, string field)
        {
            return content.TryGetValue(field, out var raw)
                   && raw != null
                   && raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetFromContent(Dictionary<string, string> content, string field, out string value)
        {
            if (content.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                value = v;
                return true;
            }

            value = string.Empty;
            return false;
        }
        
        public async Task<ConnectionModel> GetEmbeddingConnection(IngestionModel ingestion)
        {
            var embeddingConnectionId = GetIntFromContent(ingestion.Content, Constants.EmbeddingConnectionField);

            var connection = await _connectionManager.GetConnectionWithParams(
                ingestion.WorkspaceId,
                isEmbeddingConnection: true,
                connectionId: embeddingConnectionId);

            return connection ?? throw new ApplicationException("No embedding connection found");
        }

        public async Task<ConnectionModel> GetDataSourceConnection(IngestionModel ingestion, ConnectionType connectionType, string connectionFieldName)
        {
            var connectionId = GetIntFromContent(ingestion.Content, connectionFieldName);

            var connection = await _connectionManager.GetConnectionWithParams(
                ingestion.WorkspaceId,
                connectionId: connectionId,
                connectionType: connectionType);

            return connection ?? throw new ApplicationException($"No {connectionType} connection found");
        }

        public async Task FillVectorDbConnection(IngestionModel ingestion, EmbeddingConnectionModel embeddingConnection)
        {
            var vectorDbConnectionId = GetIntFromContent(ingestion.Content, Constants.VectorDbConnectionField);

            // Default Qdrant
            if (vectorDbConnectionId == 0)
            {
                embeddingConnection.ConnectionType = ConnectionTypeEnum.Qdrant;
                embeddingConnection.ConnectionString = _config.QdrantUrl;
                return;
            }

            // Specific Azure Search connection
            var connection = await _connectionManager.GetConnectionWithParams(
                ingestion.WorkspaceId,
                connectionId: vectorDbConnectionId,
                connectionType: ConnectionType.AzureAiSearch);

            if (connection == null)
                throw new ApplicationException("No Azure Ai Search connection found");

            var useHybrid = GetBoolFromContent(connection.Content, "useHybridSearch");

            embeddingConnection.ConnectionType = ConnectionTypeEnum.AzureAiSearch;
            embeddingConnection.ConnectionString = $"{connection.Content["resourceName"]};{connection.Content["apiKey"]};{useHybrid}";
        }

        public async Task<TranslateStepModel> GetTranslateStepModel(IngestionModel ingestion)
        {
            // Feature disabled
            if (!GetBoolFromContent(ingestion.Content, Constants.TranslateEnabled))
                return new TranslateStepModel();

            // Target language missing → skip translation
            if (!TryGetFromContent(ingestion.Content, Constants.TranslateTargetLanguage, out var targetLang))
                return new TranslateStepModel();

            var connId = GetIntFromContent(ingestion.Content, Constants.TranslateConnection);

            var connection = await _connectionManager.GetConnectionWithParams(
                ingestion.WorkspaceId,
                connectionId: connId,
                connectionType: ConnectionType.AzureAiTranslator);

            // If connection deleted → return empty model
            if (connection == null)
                return new TranslateStepModel();

            return new TranslateStepModel
            {
                Enabled = true,
                TargetLanguage = targetLang,
                ApiKey = connection.Content["apiKey"],
                Region = connection.Content["region"]
            };
        }
    }


    public interface IDataIngestionHelperService
    {
        Task<ConnectionModel> GetEmbeddingConnection(IngestionModel ingestion);
        Task<ConnectionModel> GetDataSourceConnection(IngestionModel ingestion, ConnectionType connectionType, string connectionFieldName);
        Task FillVectorDbConnection(IngestionModel ingestion, EmbeddingConnectionModel embeddingConnectionModel);
        Task<TranslateStepModel> GetTranslateStepModel(IngestionModel ingestion);
    }
}
