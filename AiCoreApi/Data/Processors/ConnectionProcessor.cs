using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class ConnectionProcessor : IConnectionProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public ConnectionProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<List<ConnectionModel?>> List(int? workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var qry = db.Connections.OrderBy(item => item.ConnectionId).AsNoTracking();
            if (workspaceId == 0)
            {
                qry = qry.Where(e => e.WorkspaceId == null || e.WorkspaceId == 0);
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
            await using var db = await _dbFactory.CreateDbContextAsync();
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
                await db.Connections.AddAsync(settingValue);
            }
            else
            {
                settingValue = await db.Connections.FirstAsync(item => item.ConnectionId == connectionModel.ConnectionId);
                settingValue.Name = connectionModel.Name;
                settingValue.Content = connectionModel.Content;
                db.Connections.Update(settingValue);
            }

            await db.SaveChangesAsync();
            return settingValue;
        }

        public async Task Remove(int connectionId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var connection = await db.Connections
                .FirstOrDefaultAsync(item => item.ConnectionId == connectionId);
            if (connection == null)
                return;
            db.Connections.Remove(connection);
            await db.SaveChangesAsync();
        }

        public async Task<ConnectionModel?> GetById(int connectionId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Connections.AsNoTracking().FirstOrDefaultAsync(t => t.ConnectionId == connectionId);
        }

        public async Task<ConnectionModel?> GetByName(string connectionName, int? workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var qry = db.Connections.AsNoTracking();
            if (workspaceId == 0)
            {
                qry = qry.Where(e => e.Name == connectionName && (e.WorkspaceId == null || e.WorkspaceId == 0));
            }
            else if (workspaceId != null && workspaceId > 0)
            {
                qry = qry.Where(e => e.Name == connectionName && e.WorkspaceId == workspaceId);
            }
            return await qry.FirstOrDefaultAsync();
        }
    }

    public interface IConnectionProcessor
    {
        Task<List<ConnectionModel?>> List(int? workspaceId);
        Task<ConnectionModel> Set(ConnectionModel connectionModel, int? workspaceId);
        Task Remove(int connectionId);
        Task<ConnectionModel?> GetById(int connectionId);
        Task<ConnectionModel?> GetByName(string connectionName, int? workspaceId);
    }
}
