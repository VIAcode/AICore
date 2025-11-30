using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Common
{
    public class ConnectionManager: IConnectionManager
    {
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IParametersHelper _parametersHelper;

        public ConnectionManager(
            IConnectionProcessor connectionProcessor, 
            IParametersHelper parametersHelper)
        {
            _connectionProcessor = connectionProcessor;
            _parametersHelper = parametersHelper;
        }

        private volatile List<ConnectionModel>? _cache;
        private readonly object _cacheLock = new();

        public async Task<List<ConnectionModel>> GetConnectionModels()
        {
            var existing = _cache;
            if (existing != null)
                return existing;

            lock (_cacheLock)
            {
                if (_cache != null)
                    return _cache;
            }
            var loaded = await _connectionProcessor.ListAll();
            lock (_cacheLock)
            {
                if (_cache == null)
                    _cache = loaded;
            }
            return _cache!;
        }

        public async Task<ConnectionModel?> GetConnectionWithParams(int? workspaceId = null, int? connectionId = null, ConnectionType? connectionType = null, bool isLlmConnection = false, bool isEmbeddingConnection = false, string? connectionName = null)
        {
            var connection = await GetConnection(workspaceId, connectionId, connectionType, isLlmConnection, isEmbeddingConnection,  connectionName);
            if (connection == null)
                return null;
            var connectionCopy = new ConnectionModel
            {
                ConnectionId = connection.ConnectionId,
                Created = connection.Created,
                CreatedBy = connection.CreatedBy,
                Name = connection.Name,
                Type = connection.Type,
                WorkspaceId = connection.WorkspaceId,
                Content = new Dictionary<string, string>(connection.Content),
            };
            return await _parametersHelper.ApplySecrets(connectionCopy);
        }

        public async Task<ConnectionModel?> GetConnection(int? workspaceId = null, int? connectionId = null, ConnectionType? connectionType = null, bool isLlmConnection = false, bool isEmbeddingConnection = false, string? connectionName = null)
        {
            var connections = await GetConnectionModels();
            var connection = connections.FirstOrDefault(c =>
                     (connectionId == null || c.ConnectionId == connectionId)
                     && (workspaceId == null || workspaceId == 0 || c.WorkspaceId == workspaceId)
                     && (connectionType == null || c.Type == connectionType)
                     && (!isLlmConnection || c.Type.IsLlmConnection())
                     && (!isEmbeddingConnection || c.Type.IsEmbeddingConnection())
                     && (string.IsNullOrEmpty(connectionName) || c.Name == connectionName)
                 )
                 ?? null;
            return connection;
        }

        public async Task<List<ConnectionModel?>> List(int? workspaceId = null)
        {
            var connections = await GetConnectionModels();
            var filteredConnections = connections
                .Where(c => (workspaceId == null || workspaceId == 0 || c.WorkspaceId == workspaceId))
                .Cast<ConnectionModel?>()
                .ToList();
            return filteredConnections;
        }
    }

    public interface IConnectionManager
    {
        Task<List<ConnectionModel>> GetConnectionModels();
        Task<ConnectionModel?> GetConnectionWithParams(int? workspaceId = null, int? connectionId = null, ConnectionType? connectionType = null, bool isLlmConnection = false, bool isEmbeddingConnection = false, string? connectionName = null);
        Task<ConnectionModel?> GetConnection(int? workspaceId = null, int? connectionId = null, ConnectionType? connectionType = null, bool isLlmConnection = false, bool isEmbeddingConnection = false, string? connectionName = null);
        Task<List<ConnectionModel?>> List(int? workspaceId = null);
    }
}
