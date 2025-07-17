using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.Services.ProcessingServices
{
    internal class TaskProcessingHostedService : IHostedService, IDisposable
    {
        private const int CheckIntervalSeconds = 5;
        private const int MaxConcurrentTasks = 2;

        private readonly IServiceProvider _serviceProvider;
        private readonly IInstanceSync _instanceSync;
        private readonly ILogger<TaskProcessingHostedService> _logger;

        private readonly object _sync = new();
        private readonly List<int> _activities = new();
        
        private Timer? _timer;

        public TaskProcessingHostedService(
            IServiceProvider serviceProvider,
            IInstanceSync instanceSync,
            ILogger<TaskProcessingHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _instanceSync = instanceSync;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(Process, null, TimeSpan.Zero, TimeSpan.FromSeconds(CheckIntervalSeconds));
            return Task.CompletedTask;
        }

        public async void Process(object? state)
        {
            // Check if this is the main instance, only the main instance should process Ingestion tasks
            if (!_instanceSync.IsMainInstance)
                return;

            try
            {
                var taskProcessor =
                    _serviceProvider.GetService<ITaskProcessor>() ??
                    throw new InvalidOperationException($"'{nameof(ITaskProcessor)}' service not found.");
                var tasks = taskProcessor.GetNew();
                foreach (var task in tasks)
                {
                    if (TryLockActivity(task.IngestionId))
                    {
                        try
                        {
                            await ProcessTask(taskProcessor, task);
                            return;
                        }
                        finally
                        {
                            UnlockActivity(task.IngestionId);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Task processing service error.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return Task.CompletedTask;
        }

        private async Task ProcessTask(ITaskProcessor taskProcessor, TaskModel task)
        {
            await SetTaskState(TaskState.InProgress);
            try
            {
                var dataServiceFactory =
                    _serviceProvider.GetService<IIngestionDataServiceFactory>() ??
                    throw new InvalidOperationException($"'{nameof(IIngestionDataServiceFactory)}' service not found.");

                var service = dataServiceFactory.GetService(task);
                var payload = "";
                if (task.Context is { Count: > 0 })
                {
                    payload = task.Context.ToDictionary(key => key.Key, value => value.Value.ToString()).ToJson();
                }
                await service.Process(task.IngestionId, task.TaskId, payload ?? "");

                await SetTaskState(TaskState.Completed);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to '{task.Type}' data source '{task.IngestionId}'.");
                await SetTaskFailed(e.Message);
            }

            async Task SetTaskState(TaskState state)
            {
                task.State = state;
                await taskProcessor.Set(task);
            }

            async Task SetTaskFailed(string errorMessage)
            {
                task.State = TaskState.Failed;
                task.ErrorMessage = errorMessage;
                await taskProcessor.Set(task);
            }
        }

        private bool TryLockActivity(int activityId)
        {
            lock (_sync)
            {
                if (_activities.Contains(activityId) ||
                    (MaxConcurrentTasks > 0 && MaxConcurrentTasks <= _activities.Count))
                    return false;
                _activities.Add(activityId);
                return true;
            }
        }

        private bool UnlockActivity(int activityId)
        {
            lock (_sync)
            {
                return _activities.Remove(activityId);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}