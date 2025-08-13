using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class SpentProcessor : ISpentProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public SpentProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<SpentModel> GetTodayByLoginId(int loginId, string modelName)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Spent.AsNoTracking()
                .FirstOrDefaultAsync(item => item.LoginId == loginId && item.Date == DateTime.UtcNow.Date && item.ModelName == modelName) 
                   ?? new SpentModel{ModelName = modelName, LoginId = loginId, Date = DateTime.UtcNow.Date };
        }

        public async Task<List<SpentModel>> ListLastMonth()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var lastMonth = DateTime.UtcNow.Date.AddDays(-30);
            return await db.Spent.AsNoTracking().Where(item => item.Date >= lastMonth).ToListAsync();
        }

        public async Task Update(SpentModel spentModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingSpent = await db.Spent.FirstOrDefaultAsync(item => item.SpentId == spentModel.SpentId);
            if (existingSpent == null)
            {
                db.Spent.Add(spentModel);
            }
            else
            {
                db.Entry(existingSpent).CurrentValues.SetValues(spentModel);
                db.Spent.Update(existingSpent);
            }
            await db.SaveChangesAsync();
        }
    }

    public interface ISpentProcessor
    {
        Task<List<SpentModel>> ListLastMonth();
        Task<SpentModel> GetTodayByLoginId(int loginId, string modelName);
        Task Update(SpentModel spentModel);
    }
}