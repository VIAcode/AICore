using AiCoreApi.Common;
using AiCoreApi.Common.Monitoring;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class BaseAgentHelper: IBaseAgentHelper
    {
        public BaseAgentHelper(
            IAgentsProcessor agentsProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            MonitoringConfig monitoringConfig,
            IIngestionParametersHelper parametersHelper
            )
        {
            AgentsProcessor = agentsProcessor;
            RequestAccessor = requestAccessor;
            ResponseAccessor = responseAccessor;
            MonitoringConfig = monitoringConfig;
            ParametersHelper = parametersHelper;
        }

        public IAgentsProcessor AgentsProcessor { get; }
        public RequestAccessor RequestAccessor { get; }
        public ResponseAccessor ResponseAccessor { get; }
        public MonitoringConfig MonitoringConfig { get; }
        public IIngestionParametersHelper ParametersHelper { get; }
    }

    public interface IBaseAgentHelper
    {
        IAgentsProcessor AgentsProcessor { get; }
        RequestAccessor RequestAccessor { get; }
        ResponseAccessor ResponseAccessor { get; }
        MonitoringConfig MonitoringConfig { get; }
        IIngestionParametersHelper ParametersHelper { get; }
    }
}
