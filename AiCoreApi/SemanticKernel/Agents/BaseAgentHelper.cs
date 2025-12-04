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
            IParametersHelper parametersHelper
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
        public IParametersHelper ParametersHelper { get; }
    }

    public interface IBaseAgentHelper
    {
        IAgentsProcessor AgentsProcessor { get; }
        RequestAccessor RequestAccessor { get; }
        ResponseAccessor ResponseAccessor { get; }
        MonitoringConfig MonitoringConfig { get; }
        IParametersHelper ParametersHelper { get; }
    }
}
