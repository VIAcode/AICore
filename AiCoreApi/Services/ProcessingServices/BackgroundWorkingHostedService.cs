using System.Runtime;
using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;
using AiCoreApi.Data.Processors;
using AiCoreApi.Services.ProcessingServices.AgentsHandlers;

namespace AiCoreApi.Services.ProcessingServices
{
    public sealed class BackgroundWorkingHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IInstanceSync _instanceSync;
        private readonly Config _config;
        private readonly ILogger<BackgroundWorkingHostedService> _logger;
        private DateTime _lastSettingsResetTime = DateTime.MinValue;
        private const int SettingsResetIntervalSeconds = 15;

        public BackgroundWorkingHostedService(
            IServiceScopeFactory scopeFactory,
            IInstanceSync instanceSync,
            Config config,
            ILogger<BackgroundWorkingHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _instanceSync = instanceSync;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    using var scope = _scopeFactory.CreateScope();

                    var monitoringConfig = scope.ServiceProvider.GetRequiredService<MonitoringConfig>();
                    var extendedConfig = scope.ServiceProvider.GetRequiredService<ExtendedConfig>();
                    var settingsProcessor = scope.ServiceProvider.GetRequiredService<ISettingsProcessor>();
                    var agentsProcessor = scope.ServiceProvider.GetRequiredService<IAgentsProcessor>();
                    var schedulerAgentService = scope.ServiceProvider.GetRequiredService<ISchedulerAgentService>();
                    var backgroundWorkerAgentService = scope.ServiceProvider.GetRequiredService<IBackgroundWorkerAgentService>();
                    var azureServiceBusListenerService = scope.ServiceProvider.GetRequiredService<IAzureServiceBusListenerAgentService>();
                    var rabbitListenerService = scope.ServiceProvider.GetRequiredService<IRabbitMqListenerAgentService>();
                    var imapListenerService = scope.ServiceProvider.GetRequiredService<IImapListenerAgentService>();
                    var debugLogsProcessingService = scope.ServiceProvider.GetRequiredService<IDebugLogsProcessingService>();
                    var graphMailListenerAgentService = scope.ServiceProvider.GetRequiredService<IGraphMailListenerAgentService>();
                    var graphTeamsListenerAgentService = scope.ServiceProvider.GetRequiredService<IGraphTeamsListenerAgentService>();

                    var agents = await agentsProcessor.List(null);

                    var tasks = new List<Task>
                    {
                        azureServiceBusListenerService.ProcessTask(agents),
                        rabbitListenerService.ProcessTask(agents),
                        debugLogsProcessingService.ProcessTask()
                    };

                    if (_instanceSync.IsMainInstance)
                    {
                        tasks.Add(backgroundWorkerAgentService.ProcessTask());
                        tasks.Add(schedulerAgentService.ProcessTask(agents));
                        tasks.Add(imapListenerService.ProcessTask(agents));
                        tasks.Add(graphMailListenerAgentService.ProcessTask(agents));
                        tasks.Add(graphTeamsListenerAgentService.ProcessTask(agents));
                    }

                    await Task.WhenAll(tasks);

                    MaybeCompactLoh();

                    if ((DateTime.UtcNow - _lastSettingsResetTime).TotalSeconds > SettingsResetIntervalSeconds)
                    {
                        extendedConfig.Reset(settingsProcessor);
                        monitoringConfig.Reset(settingsProcessor);
                        _lastSettingsResetTime = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {  }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackgroundWorkingHostedService crashed.");
            }
        }

        private DateTime _lastLohCompactUtc = DateTime.MinValue;
        private void MaybeCompactLoh()
        {
            if (!_config.AutoCompactLargeObjectHeap) 
                return;
            if ((DateTime.UtcNow - _lastLohCompactUtc) < TimeSpan.FromMinutes(5)) 
                return;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            _lastLohCompactUtc = DateTime.UtcNow;
        }
    }
}
