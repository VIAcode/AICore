using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class TaskProcessor : ITaskProcessor
    {
        private readonly Db _db;
        private readonly ExtendedConfig _config;

        public TaskProcessor(Db db, ExtendedConfig config)
        {
            _db = db;
            _config = config;
        }

        public List<TaskModel> GetNew()
        {
            return _db.Tasks.AsNoTracking()
                .Where(t => t.State == TaskState.New)
                .OrderBy(t => t.Created).ToList();
        }

        public List<TaskModel> GetByIngestion(int ingestionId)
        {
            return _db.Tasks.AsNoTracking()
                .Where(t => t.IngestionId == ingestionId).ToList();
        }

        public async Task<TaskModel> ScheduleTask(TaskModel taskModel)
        {
            if (taskModel.TaskId != 0)
            {
                throw new ArgumentException("Value should be 0.", nameof(TaskModel.TaskId));
            }

            var active = _db.Tasks.AsNoTracking()
                .FirstOrDefault(t =>
                    t.IngestionId == taskModel.IngestionId && 
                    t.Type == taskModel.Type && 
                    t.State == TaskState.New);
            if (active != null)
            {
                return active;
            }

            return (await Set(taskModel))!;
        }

        public async Task<TaskModel?> Set(TaskModel taskModel)
        {
            TaskModel? entity;
            if (taskModel.TaskId == 0)
            {
                entity = taskModel;
                await _db.Tasks.AddAsync(entity);
            }
            else
            {
                entity = _db.Tasks.FirstOrDefault(item => item.TaskId == taskModel.TaskId);
                if (entity == null)
                    return null;

                taskModel.Updated = DateTime.UtcNow;
                _db.Entry(entity).CurrentValues.SetValues(taskModel);
                _db.Tasks.Update(entity);
            }

            await _db.SaveChangesAsync();
            return entity;
        }

        public async Task<TaskModel?> SetMessage(int taskId, string message)
        {
            var entity = _db.Tasks.FirstOrDefault(item => item.TaskId == taskId);
            if (entity == null)
                return null;
            entity.ErrorMessage = message;
            _db.Tasks.Update(entity);
            await _db.SaveChangesAsync();
            return entity;
        }

        public List<TaskModel> List()
        {
            return _db.Tasks.AsNoTracking().ToList();
        }

        public async Task<List<TaskModel>> ListWithIngestion(int workspaceId)
        {
            var qry = _db.Tasks
                .Include(t => t.Ingestion)
                .Select(t => new TaskModel
                {
                    TaskId = t.TaskId,
                    IngestionId = t.IngestionId,
                    Ingestion = new IngestionModel { Name = t.Ingestion.Name },
                    State = t.State,
                    Type = t.Type,
                    Created = t.Created,
                    Updated = t.Updated,
                    ErrorMessage = t.ErrorMessage
                })
                .OrderByDescending(t => t.Updated)
                .AsNoTracking();
            qry = workspaceId == 0 
                ? qry.Where(t => t.Ingestion.WorkspaceId == null) 
                : qry.Where(t => t.Ingestion.WorkspaceId == workspaceId);

            return await qry.ToListAsync();
        }

        public async Task<List<TaskModel>> LastTaskList(List<int> ingestionIds)
        {
            var result = await _db.Tasks
                .Where(t => ingestionIds.Contains(t.IngestionId))
                .GroupBy(t => t.IngestionId)
                .Select(g => g.OrderByDescending(e => e.Updated).First())
                .AsNoTracking()
                .ToListAsync();

            return result;
        }

        public async Task ClearHistory()
        {
            var threshold = 
                DateTime.UtcNow - TimeSpan.FromHours(_config.MaxTaskHistory);

            await _db.Tasks.Where(t =>
                    (t.State == TaskState.Completed || t.State == TaskState.Failed) &&
                    t.Updated < threshold)
                .ExecuteDeleteAsync();
        }

        public async Task ResetUnfinishedTasks()
        {
            await _db.Tasks.Where(t => (t.State == TaskState.InProgress) && t.IsRetriable)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.State, TaskState.New));
        }
    }

    public interface ITaskProcessor
    {
        List<TaskModel> GetNew();
        List<TaskModel> GetByIngestion(int ingestionId);
        Task<TaskModel> ScheduleTask(TaskModel taskModel);
        Task<TaskModel?> Set(TaskModel taskModel);
        Task<TaskModel?> SetMessage(int taskId, string message);
        List<TaskModel> List();
        Task ClearHistory();
        Task<List<TaskModel>> ListWithIngestion(int workspaceId);
        Task<List<TaskModel>> LastTaskList(List<int> ingestionIds);
        Task ResetUnfinishedTasks();
    }
}
