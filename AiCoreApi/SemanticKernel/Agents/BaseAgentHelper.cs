using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;
using AiCoreApi.Data.Processors;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class BaseAgentHelper: IBaseAgentHelper
    {
        public BaseAgentHelper(
            IAgentsProcessor agentsProcessor,
            IDataIngestionWorkerFactory dataIngestionWorkerFactory,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            MonitoringConfig monitoringConfig,
            IIngestionProcessor ingestionProcessor,
            ICacheAccessor cacheAccessor)
        {
            AgentsProcessor = agentsProcessor;
            DataIngestionWorkerFactory = dataIngestionWorkerFactory;
            RequestAccessor = requestAccessor;
            ResponseAccessor = responseAccessor;
            MonitoringConfig = monitoringConfig;
            IngestionProcessor = ingestionProcessor;
            CacheAccessor = cacheAccessor;
        }

        public IDataIngestionWorkerFactory DataIngestionWorkerFactory { get; }
        public IAgentsProcessor AgentsProcessor { get; }
        public RequestAccessor RequestAccessor { get; }
        public ResponseAccessor ResponseAccessor { get; }
        public MonitoringConfig MonitoringConfig { get; }
        public IIngestionProcessor IngestionProcessor { get; }
        public ICacheAccessor CacheAccessor { get; }
    }

    public interface IBaseAgentHelper
    {
        IDataIngestionWorkerFactory DataIngestionWorkerFactory { get; }
        IAgentsProcessor AgentsProcessor { get; }
        RequestAccessor RequestAccessor { get; }
        ResponseAccessor ResponseAccessor { get; }
        MonitoringConfig MonitoringConfig { get; }
        IIngestionProcessor IngestionProcessor { get; }
        ICacheAccessor CacheAccessor { get; }
    }
}
