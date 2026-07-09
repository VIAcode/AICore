using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class TaskProcessor : ITaskProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;
        private readonly ExtendedConfig _config;

        public TaskProcessor(IDbContextFactory<Db> dbFactory, ExtendedConfig config)
        {
            _dbFactory = dbFactory;
            _config = config;
        }

        public async Task<List<TaskModel>> GetNew()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Tasks.AsNoTracking()
                .Where(t => t.State == TaskState.New && t.LockerTaskId == null)
                .OrderBy(t => t.TaskId).ToListAsync();
        }

        public async Task<List<TaskModel>> GetByIngestion(int ingestionId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Tasks.AsNoTracking()
                .Where(t => t.IngestionId == ingestionId).ToListAsync();
        }

        public async Task<TaskModel> ScheduleTask(TaskModel taskModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (taskModel.TaskId != 0)
            {
                throw new ArgumentException("Value should be 0.", nameof(TaskModel.TaskId));
            }

            var active = db.Tasks.AsNoTracking()
                .FirstOrDefault(t =>
                    t.IngestionId == taskModel.IngestionId && 
                    t.Type == taskModel.Type &&
                    t.State == TaskState.New &&
                    t.LockerTaskId == taskModel.LockerTaskId);
            if (active != null)
            {
                return active;
            }

            return (await Set(taskModel))!;
        }

        public async Task<TaskModel?> Set(TaskModel taskModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            TaskModel? entity;
            if (taskModel.TaskId == 0)
            {
                entity = taskModel;
                await db.Tasks.AddAsync(entity);
            }
            else
            {
                entity = db.Tasks.FirstOrDefault(item => item.TaskId == taskModel.TaskId);
                if (entity == null)
                    return null;

                taskModel.Updated = DateTime.UtcNow;
                db.Entry(entity).CurrentValues.SetValues(taskModel);
                db.Tasks.Update(entity);
            }

            await db.SaveChangesAsync();
            // Unlock all tasks that were locked by this task if it is completed
            if (taskModel.State == TaskState.Completed)
            {
                var tasksToUpdate = db.Tasks
                    .Where(t => t.LockerTaskId == taskModel.TaskId && t.State == TaskState.New)
                    .ToList();
                foreach (var task in tasksToUpdate)
                {
                    task.LockerTaskId = null;
                    task.Updated = DateTime.UtcNow;
                    db.Tasks.Update(task);
                }
                await db.SaveChangesAsync();
            }
            return entity;
        }

        public async Task<TaskModel?> SetMessage(int taskId, string message)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = db.Tasks.FirstOrDefault(item => item.TaskId == taskId);
            if (entity == null)
                return null;
            entity.ErrorMessage = message;
            db.Tasks.Update(entity);
            await db.SaveChangesAsync();
            return entity;
        }

        public async Task<List<TaskModel>> ListWithIngestion(int workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var qry = db.Tasks
                .Include(t => t.Ingestion)
                .Select(t => new TaskModel
                {
                    TaskId = t.TaskId,
                    IngestionId = t.IngestionId,
                    Ingestion = new IngestionModel { Name = t.Ingestion.Name, WorkspaceId = t.Ingestion.WorkspaceId },
                    State = t.State,
                    Type = t.Type,
                    Created = t.Created,
                    Updated = t.Updated,
                    ErrorMessage = t.ErrorMessage
                })
                .OrderByDescending(t => t.Updated)
                .AsNoTracking();
            qry = workspaceId == 0 
                ? qry.Where(t => t.Ingestion.WorkspaceId == null || t.Ingestion.WorkspaceId == 0) 
                : qry.Where(t => t.Ingestion.WorkspaceId == workspaceId);

            return await qry.ToListAsync();
        }

        public async Task<List<TaskModel>> LastTaskList(List<int> ingestionIds)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.Tasks
                .Where(t => ingestionIds.Contains(t.IngestionId))
                .GroupBy(t => t.IngestionId)
                .Select(g => g.OrderByDescending(e => e.Updated).First())
                .AsNoTracking()
                .ToListAsync();

            return result;
        }

        public async Task ClearHistory()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var threshold = 
                DateTime.UtcNow - TimeSpan.FromHours(_config.MaxTaskHistory);

            await db.Tasks.Where(t =>
                    (t.State == TaskState.Completed || t.State == TaskState.Failed) &&
                    t.Updated < threshold)
                .ExecuteDeleteAsync();
        }

        public async Task ResetUnfinishedTasks()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // Any task still marked InProgress at startup was interrupted (the worker that owned it
            // is gone), so it must be re-queued regardless of IsRetriable. Otherwise a non-retriable
            // task stays stuck InProgress forever and blocks the scheduler: GetStale().Take(N) keeps
            // selecting the same stalest data sources, sees their orphaned "active" task and skips
            // them, so no data source is ever synced again.
            await db.Tasks.Where(t => t.State == TaskState.InProgress)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.State, TaskState.New));
        }
    }

    public interface ITaskProcessor
    {
        Task<List<TaskModel>> GetNew();
        Task<List<TaskModel>> GetByIngestion(int ingestionId);
        Task<TaskModel> ScheduleTask(TaskModel taskModel);
        Task<TaskModel?> Set(TaskModel taskModel);
        Task<TaskModel?> SetMessage(int taskId, string message);
        Task ClearHistory();
        Task<List<TaskModel>> ListWithIngestion(int workspaceId);
        Task<List<TaskModel>> LastTaskList(List<int> ingestionIds);
        Task ResetUnfinishedTasks();
    }
}
