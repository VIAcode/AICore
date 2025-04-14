using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class ConnectionProcessor : IConnectionProcessor
    {
        private readonly Db _db;
        private readonly ExtendedConfig _config;

        public ConnectionProcessor(Db db, ExtendedConfig config)
        {
            _db = db;
            _config = config;
        }

        public async Task<List<ConnectionModel?>> List(int? workspaceId)
        {
            var qry = _db.Connections.OrderBy(item => item.ConnectionId).AsNoTracking();
            if (workspaceId == 0)
            {
                qry = qry.Where(e => e.WorkspaceId == null);
            }
            else if (workspaceId != null && workspaceId > 0)
            {
                qry = qry.Where(e => e.WorkspaceId == workspaceId);
            }
            var data = await qry.ToListAsync();
            return data;
        }

        public async Task<ConnectionModel> Set(ConnectionModel connectionModel, int? workspaceId)
        {
            ConnectionModel? settingValue;
            if (connectionModel.ConnectionId == 0)
            {
                settingValue = new ConnectionModel
                {
                    CreatedBy = connectionModel.CreatedBy,
                    Created = connectionModel.Created,
                    Name = connectionModel.Name,
                    Type = connectionModel.Type,
                    Content = connectionModel.Content,
                    WorkspaceId = workspaceId == 0 ? null : workspaceId,
                };
                await _db.Connections.AddAsync(settingValue);
            }
            else
            {
                settingValue = await _db.Connections.FirstAsync(item => item.ConnectionId == connectionModel.ConnectionId);
                settingValue.Name = connectionModel.Name;
                settingValue.Content = connectionModel.Content;
                _db.Connections.Update(settingValue);
            }

            await _db.SaveChangesAsync();
            return settingValue;
        }

        public async Task Remove(int connectionId)
        {
            var connection = await _db.Connections
                .FirstOrDefaultAsync(item => item.ConnectionId == connectionId);
            if (connection == null)
                return;
            _db.Connections.Remove(connection);
            await _db.SaveChangesAsync();
        }

        public async Task<ConnectionModel?> GetById(int connectionId)
        {
            return await _db.Connections.AsNoTracking().FirstOrDefaultAsync(t => t.ConnectionId == connectionId);
        }
    }

    public interface IConnectionProcessor
    {
        Task<List<ConnectionModel?>> List(int? workspaceId);
        Task<ConnectionModel> Set(ConnectionModel connectionModel, int? workspaceId);
        Task Remove(int connectionId);
        Task<ConnectionModel?> GetById(int connectionId);
    }
}
