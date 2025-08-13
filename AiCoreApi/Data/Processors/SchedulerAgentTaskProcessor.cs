using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class SchedulerAgentTaskProcessor : ISchedulerAgentTaskProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public SchedulerAgentTaskProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<SchedulerAgentTaskModel?> GetNext()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SchedulerAgentTasks
                .Where(x => x.SchedulerAgentTaskState == SchedulerAgentTaskState.New)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync();
        }

        public async Task<SchedulerAgentTaskModel?> GetByGuid(string schedulerAgentTaskGuid)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SchedulerAgentTasks
                .FirstOrDefaultAsync(x => x.SchedulerAgentTaskGuid == schedulerAgentTaskGuid);
        }

        public async Task<SchedulerAgentTaskModel> Update(SchedulerAgentTaskModel schedulerAgentTaskModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingAgent = await db.SchedulerAgentTasks
                .FirstOrDefaultAsync(item => item.SchedulerAgentTaskId == schedulerAgentTaskModel.SchedulerAgentTaskId);
            if (existingAgent == null)
                return await Add(schedulerAgentTaskModel);
            db.Entry(existingAgent).CurrentValues.SetValues(schedulerAgentTaskModel);
            db.SchedulerAgentTasks.Update(existingAgent);
            await db.SaveChangesAsync();
            return existingAgent;
        }

        public async Task<SchedulerAgentTaskModel> Add(SchedulerAgentTaskModel schedulerAgentTaskModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingAgent = await db.SchedulerAgentTasks
                .FirstOrDefaultAsync(item => item.SchedulerAgentTaskId == schedulerAgentTaskModel.SchedulerAgentTaskId);
            if (existingAgent != null)
                return existingAgent;
            db.SchedulerAgentTasks.Add(schedulerAgentTaskModel);
            await db.SaveChangesAsync();
            return schedulerAgentTaskModel;
        }

        public async Task RemoveExpired()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var expiredAgents = await db.SchedulerAgentTasks
                .Where(x => x.ValidTill < DateTime.UtcNow)
                .ToListAsync();
            db.SchedulerAgentTasks.RemoveRange(expiredAgents);
            await db.SaveChangesAsync();
        }
    }

    public interface ISchedulerAgentTaskProcessor
    {
        Task<SchedulerAgentTaskModel?> GetNext();
        Task<SchedulerAgentTaskModel?> GetByGuid(string schedulerAgentTaskGuid);
        Task<SchedulerAgentTaskModel> Update(SchedulerAgentTaskModel schedulerAgentTaskModel);
        Task<SchedulerAgentTaskModel> Add(SchedulerAgentTaskModel schedulerAgentTaskModel);
        Task RemoveExpired();
    }
}

