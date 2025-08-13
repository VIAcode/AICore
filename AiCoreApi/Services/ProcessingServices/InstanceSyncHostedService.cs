using AiCoreApi.Common;

namespace AiCoreApi.Services.ProcessingServices
{
    public sealed class InstanceSyncHostedService : BackgroundService
    {
        private readonly IInstanceSync _instanceSync;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<InstanceSyncHostedService> _logger;

        public InstanceSyncHostedService(
            IInstanceSync instanceSync,
            IHostApplicationLifetime lifetime,
            ILogger<InstanceSyncHostedService> logger)
        {
            _instanceSync = instanceSync;
            _lifetime = lifetime;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var periodSeconds = Math.Max(5, InstanceSync.TtlInSeconds - 5);
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSeconds));

            try
            {
                SafeHeartbeat();

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    SafeHeartbeat();

                    if (_instanceSync.IsRestartNeeded())
                    {
                        _logger.LogWarning("InstanceSync requested application restart.");
                        _lifetime.StopApplication();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InstanceSyncHostedService failed.");
            }
        }

        private void SafeHeartbeat()
        {
            try
            {
                _instanceSync.SendHeartbeat();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendHeartbeat failed.");
            }
        }
    }
}
