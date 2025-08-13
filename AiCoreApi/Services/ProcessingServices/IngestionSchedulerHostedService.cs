using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.ProcessingServices
{
    public sealed class IngestionSchedulerHostedService : BackgroundService
    {
        private const string ServiceName = "Scheduler";
        private const int CheckIntervalSeconds = 600;
        private const int MaxTasksCount = 2;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IInstanceSync _instanceSync;
        private readonly ILogger<IngestionSchedulerHostedService> _logger;

        public IngestionSchedulerHostedService(IServiceScopeFactory scopeFactory, IInstanceSync instanceSync, ILogger<IngestionSchedulerHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _instanceSync = instanceSync;
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var taskProcessor = scope.ServiceProvider.GetRequiredService<ITaskProcessor>();
            await taskProcessor.ResetUnfinishedTasks();
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));

            try
            {
                await RunOnceAsync(ct);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await RunOnceAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Service} crashed.", ServiceName);
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            if (!_instanceSync.IsMainInstance) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ingestionProcessor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                var taskProcessor = scope.ServiceProvider.GetRequiredService<ITaskProcessor>();

                await ProcessSyncAsync(ingestionProcessor, taskProcessor, ct);

                await taskProcessor.ClearHistory();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                _logger.LogError(e, "{Service} service error.", ServiceName);
            }
        }

        private static async Task ProcessSyncAsync(IIngestionProcessor ingestionProcessor, ITaskProcessor taskProcessor, CancellationToken ct)
        {
            var ingestions = (await ingestionProcessor.GetStale())
               .Take(MaxTasksCount)
               .ToList();

            foreach (var ingestion in ingestions)
            {
                var tasks = await taskProcessor.GetByIngestion(ingestion.IngestionId);
                var active = tasks.FirstOrDefault(t =>
                    t.Type == TaskType.DataSync &&
                    (t.State == TaskState.InProgress || t.State == TaskState.New));

                if (active != null)
                {
                    continue;
                }

                var task = new TaskModel
                {
                    IngestionId = ingestion.IngestionId,
                    Type = TaskType.DataSync,
                    CreatedBy = ServiceName
                };

                await taskProcessor.ScheduleTask(task);
            }
        }
    }
}
