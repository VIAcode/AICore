using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class RbacGroupSyncProcessor : IRbacGroupSyncProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public RbacGroupSyncProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }
        
        public async Task<List<RbacGroupSyncModel>> ListAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.RbacGroupSync.AsNoTracking().ToListAsync();
        }

        public async Task DeleteAsync(int rbacGroupSyncId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rbacGroupSync = await db.RbacGroupSync
                .FirstOrDefaultAsync(item => item.RbacGroupSyncId == rbacGroupSyncId);
            if (rbacGroupSync == null)
                return;
            db.RbacGroupSync.Remove(rbacGroupSync);
            await db.SaveChangesAsync();
        }

        public async Task<RbacGroupSyncModel> AddAsync(RbacGroupSyncModel rbacGroupSyncModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingRbacGroupSync = await db.RbacGroupSync
                .FirstOrDefaultAsync(item => item.RbacGroupName == rbacGroupSyncModel.RbacGroupName);
            if (existingRbacGroupSync != null)
                return existingRbacGroupSync;

            db.RbacGroupSync.Add(rbacGroupSyncModel);
            await db.SaveChangesAsync();
            return rbacGroupSyncModel;
        }

        public async Task<RbacGroupSyncModel> UpdateAsync(RbacGroupSyncModel rbacGroupSyncModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingRbacGroupSync = await db.RbacGroupSync
                .FirstOrDefaultAsync(item => item.RbacGroupSyncId == rbacGroupSyncModel.RbacGroupSyncId);
            if (existingRbacGroupSync == null)
                return null;
            var createdBy = existingRbacGroupSync.CreatedBy;
            db.Entry(existingRbacGroupSync).CurrentValues.SetValues(rbacGroupSyncModel);
            existingRbacGroupSync.CreatedBy = createdBy;
            db.RbacGroupSync.Update(existingRbacGroupSync);
            await db.SaveChangesAsync();
            return rbacGroupSyncModel;
        }
    }

    public interface IRbacGroupSyncProcessor
    {
        Task<List<RbacGroupSyncModel>> ListAsync();
        Task DeleteAsync(int rbacGroupSyncId);
        Task<RbacGroupSyncModel> AddAsync(RbacGroupSyncModel rbacGroupSyncModel);
        Task<RbacGroupSyncModel> UpdateAsync(RbacGroupSyncModel rbacGroupSyncModel);
    }
}