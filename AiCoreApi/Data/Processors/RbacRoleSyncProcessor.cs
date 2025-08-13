using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class RbacRoleSyncProcessor : IRbacRoleSyncProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public RbacRoleSyncProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }
        
        public async Task<List<RbacRoleSyncModel>> ListAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.RbacRoleSync.Include(e => e.Tags).AsNoTracking().ToListAsync();
        }

        public async Task DeleteAsync(int rbacRoleSyncId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rbacRoleSync = await db.RbacRoleSync.Include(e => e.Tags)
                .FirstOrDefaultAsync(item => item.RbacRoleSyncId == rbacRoleSyncId);
            if (rbacRoleSync == null)
                return;
            db.RbacRoleSync.Remove(rbacRoleSync);
            await db.SaveChangesAsync();
        }

        public async Task<RbacRoleSyncModel> AddAsync(RbacRoleSyncModel rbacRoleSyncModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingRbacRoleSync = await db.RbacRoleSync.Include(e => e.Tags)
                .FirstOrDefaultAsync(item => item.RbacRoleName == rbacRoleSyncModel.RbacRoleName);
            if (existingRbacRoleSync != null)
                return existingRbacRoleSync;

            if (rbacRoleSyncModel.Tags != null && rbacRoleSyncModel.Tags.Count > 0)
            {
                var tags = db.Tags.ToList();
                var tIds = rbacRoleSyncModel.Tags.Select(e => e.TagId);

                rbacRoleSyncModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }

            db.RbacRoleSync.Add(rbacRoleSyncModel);
            await db.SaveChangesAsync();
            return rbacRoleSyncModel;
        }

        public async Task<RbacRoleSyncModel> UpdateAsync(RbacRoleSyncModel rbacRoleSyncModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingRbacRoleSync = await db.RbacRoleSync.Include(e => e.Tags)
                .FirstOrDefaultAsync(item => item.RbacRoleSyncId == rbacRoleSyncModel.RbacRoleSyncId);
            if (existingRbacRoleSync == null)
                return null;
            var createdBy = existingRbacRoleSync.CreatedBy;
            db.Entry(existingRbacRoleSync).CurrentValues.SetValues(rbacRoleSyncModel);
            existingRbacRoleSync.CreatedBy = createdBy;
            if (rbacRoleSyncModel.Tags != null && rbacRoleSyncModel.Tags.Count > 0)
            {
                var tags = db.Tags.ToList();
                var tIds = rbacRoleSyncModel.Tags.Select(e => e.TagId);

                existingRbacRoleSync.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }
            db.RbacRoleSync.Update(existingRbacRoleSync);
            await db.SaveChangesAsync();
            return rbacRoleSyncModel;
        }
    }

    public interface IRbacRoleSyncProcessor
    {
        Task<List<RbacRoleSyncModel>> ListAsync();
        Task DeleteAsync(int rbacRoleSyncId);
        Task<RbacRoleSyncModel> AddAsync(RbacRoleSyncModel rbacRoleSyncModel);
        Task<RbacRoleSyncModel> UpdateAsync(RbacRoleSyncModel rbacRoleSyncModel);
    }
}