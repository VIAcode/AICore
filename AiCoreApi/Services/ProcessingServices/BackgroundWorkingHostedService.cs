using System.Runtime;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Services.ProcessingServices.AgentsHandlers;

namespace AiCoreApi.Services.ProcessingServices
{
    public class BackgroundWorkingHostedService : IHostedService
    {
        private readonly ISchedulerAgentService _schedulerAgentService;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IBackgroundWorkerAgentService _backgroundWorkerAgentService;
        private readonly IAzureServiceBusListenerAgentService _azureServiceBusListenerAgentService;
        private readonly IRabbitMqListenerAgentService _rabbitMqListenerAgentService;
        private readonly IImapListenerAgentService _imapListenerAgentService;
        private readonly IDebugLogsProcessingService _debugLogsProcessingService;
        private readonly IGraphMailListenerAgentService _graphMailListenerAgentService;
        private readonly IGraphTeamsListenerAgentService _graphTeamsListenerAgentService;
        private readonly IInstanceSync _instanceSync;
        private readonly Config _config;

        public BackgroundWorkingHostedService(
            IAgentsProcessor agentsProcessor,
            ISchedulerAgentService schedulerAgentService,
            IBackgroundWorkerAgentService backgroundWorkerAgentService,
            IAzureServiceBusListenerAgentService azureServiceBusListenerAgentService,
            IRabbitMqListenerAgentService rabbitMqListenerAgentService,
            IImapListenerAgentService imapListenerAgentService,
            IDebugLogsProcessingService debugLogsProcessingService,
            IGraphMailListenerAgentService graphMailListenerAgentService,
            IGraphTeamsListenerAgentService graphTeamsListenerAgentService,
            IInstanceSync instanceSync,
            Config config)
        {
            _agentsProcessor = agentsProcessor;
            _schedulerAgentService = schedulerAgentService;
            _backgroundWorkerAgentService = backgroundWorkerAgentService;
            _azureServiceBusListenerAgentService = azureServiceBusListenerAgentService;
            _rabbitMqListenerAgentService = rabbitMqListenerAgentService;
            _imapListenerAgentService = imapListenerAgentService;
            _debugLogsProcessingService = debugLogsProcessingService;
            _graphMailListenerAgentService = graphMailListenerAgentService;
            _graphTeamsListenerAgentService = graphTeamsListenerAgentService;
            _instanceSync = instanceSync;
            _config = config;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var agents = await _agentsProcessor.List(null);
                await _azureServiceBusListenerAgentService.ProcessTask(agents);
                await _rabbitMqListenerAgentService.ProcessTask(agents);
                await _debugLogsProcessingService.ProcessTask();
                if (_instanceSync.IsMainInstance)
                {
                    await _backgroundWorkerAgentService.ProcessTask();
                    await _schedulerAgentService.ProcessTask(agents);
                    await _imapListenerAgentService.ProcessTask(agents);
                    await _graphMailListenerAgentService.ProcessTask(agents);
                    await _graphTeamsListenerAgentService.ProcessTask(agents);
                }
                await Task.Run(AutoCompactLargeObjectHeap, cancellationToken);
                // Await all tasks to complete in parallel
                //await Task.WhenAll(backgroundWorkerTask, schedulerTask, azureServiceBusListenerTask, autoCompactTask);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        private void AutoCompactLargeObjectHeap()
        {
            if (_config.AutoCompactLargeObjectHeap)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}