using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.Services.ProcessingServices
{
    public sealed class TaskProcessingHostedService : BackgroundService
    {
        private const int CheckIntervalSeconds = 5;
        private const int MaxConcurrentTasks = 2;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IInstanceSync _instanceSync;
        private readonly ILogger<TaskProcessingHostedService> _logger;

        private readonly object _sync = new();
        private readonly HashSet<int> _activities = new();
        private readonly SemaphoreSlim _concurrency = new(MaxConcurrentTasks, MaxConcurrentTasks);

        public TaskProcessingHostedService(
            IServiceScopeFactory scopeFactory,
            IInstanceSync instanceSync,
            ILogger<TaskProcessingHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _instanceSync = instanceSync;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (!_instanceSync.IsMainInstance)
                        continue;

                    using var scope = _scopeFactory.CreateScope();
                    var taskProcessor = scope.ServiceProvider.GetRequiredService<ITaskProcessor>();

                    var tasks = await taskProcessor.GetNew();

                    foreach (var task in tasks)
                    {
                        if (!TryLockActivity(task.IngestionId))
                            continue;

                        _ = ProcessOneAsync(task, ct)
                            .ContinueWith(_ => UnlockActivity(task.IngestionId), TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TaskProcessingHostedService crashed.");
            }
        }

        private async Task ProcessOneAsync(TaskModel task, CancellationToken ct)
        {
            await _concurrency.WaitAsync(ct);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var taskProcessor = scope.ServiceProvider.GetRequiredService<ITaskProcessor>();
                var dataServiceFactory = scope.ServiceProvider.GetRequiredService<IIngestionDataServiceFactory>();

                async Task SetTaskState(TaskState state, string? error = null)
                {
                    task.State = state;
                    task.ErrorMessage = error ?? "";
                    await taskProcessor.Set(task);
                }

                await SetTaskState(TaskState.InProgress);

                try
                {
                    var service = dataServiceFactory.GetService(task);
                    var payload = task.Context is { Count: > 0 }
                        ? task.Context.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty).ToJson()
                        : string.Empty;

                    await service.Process(task.IngestionId, task.TaskId, payload ?? string.Empty);

                    await SetTaskState(TaskState.Completed);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to '{Type}' data source '{IngestionId}'.", task.Type, task.IngestionId);
                    await SetTaskState(TaskState.Failed, e.Message);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* ignore */ }
            catch (Exception e)
            {
                _logger.LogError(e, "{TaskId} task error.", task.TaskId);
            }
            finally
            {
                _concurrency.Release();
            }
        }

        private bool TryLockActivity(int activityId)
        {
            lock (_sync)
            {
                return _activities.Add(activityId);
            }
        }

        private void UnlockActivity(int activityId)
        {
            lock (_sync)
            {
                _activities.Remove(activityId);
            }
        }
    }
}
