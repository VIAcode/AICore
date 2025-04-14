using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Azure.Messaging.ServiceBus;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class AzureServiceBusListenerAgentService : AgentServiceBase, IAzureServiceBusListenerAgentService
    {
        private static readonly Dictionary<string, ServiceBusProcessor> ServiceBusProcessors = new();
        private static readonly Dictionary<string, ServiceBusClient> ServiceBusClients = new();
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly IAgentsProcessor _agentsProcessor; 
        private readonly IConnectionProcessor _connectionProcessor;

        public AzureServiceBusListenerAgentService(
            IEntraTokenProvider entraTokenProvider,
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor,
            IServiceProvider serviceProvider,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IConnectionProcessor connectionProcessor)
            : base(loginProcessor, debugLogProcessor, extendedConfig, serviceProvider)
        {
            _entraTokenProvider = entraTokenProvider;
            _agentsProcessor = agentsProcessor;
            _connectionProcessor = connectionProcessor;
        }

        public async Task ProcessTask()
        {
            var agents = await _agentsProcessor.List(null);
            var schedulerAgents = agents.Where(agent => agent.Type == AgentType.AzureServiceBusListener).ToList();
            var processedAgents = new List<string>();
            foreach (var agent in schedulerAgents)
            {
                if(!agent.IsEnabled)
                    continue;
                if (!agent.Content.ContainsKey("lastResult"))
                    agent.Content.Add("lastResult", new ConfigurableSetting { Value = "", Code = "lastResult", Name = "Last Result" });
                if (!agent.Content.ContainsKey("lastRun"))
                    agent.Content.Add("lastRun", new ConfigurableSetting { Value = "Never", Code = "lastRun", Name = "Last Run" });

                var connectionName = agent.Content["connectionName"].Value;
                var queueOrTopicName = agent.Content["queueOrTopicName"].Value;
                var agentToCall = agent.Content["agentToCall"].Value;
                var runAs = Convert.ToInt32(agent.Content["runAs"].Value);

                var key = $"{connectionName}|{queueOrTopicName}|{agentToCall}|{runAs}".GetHash();
                processedAgents.Add(key);

                if (ServiceBusProcessors.ContainsKey(key))
                    continue;

                var connections = await _connectionProcessor.List(agent.WorkspaceId);
                var connection = connections.FirstOrDefault(conn => conn.Type == ConnectionType.AzureServiceBus && conn.Name == connectionName);
                if (connection == null)
                {
                    agent.Content["lastResult"].Value = $"Connection not found: {connectionName}";
                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                    await _agentsProcessor.Update(agent);
                    continue;
                }

                var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";
                var serviceBusConnectionString = connection.Content.ContainsKey("serviceBusConnectionString") ? connection.Content["serviceBusConnectionString"] : "";
                var serviceBusNamespace = connection.Content.ContainsKey("serviceBusNamespace") ? connection.Content["serviceBusNamespace"] : "";

                try
                {
                    ServiceBusClient client;
                    var serviceBusClientsKey = $"{serviceBusConnectionString}|{serviceBusNamespace}";
                    if (ServiceBusClients.ContainsKey(serviceBusClientsKey))
                    {
                        client = ServiceBusClients[serviceBusConnectionString];
                    }
                    else
                    {
                        if (accessType == "apiKey")
                        {
                            client = new ServiceBusClient(serviceBusConnectionString);
                        }
                        else
                        {
                            var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://servicebus.azure.net/.default");
                            if (!serviceBusNamespace.StartsWith("https://"))
                                serviceBusNamespace = $"https://{serviceBusNamespace}";
                            client = new ServiceBusClient(serviceBusNamespace, new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn));
                        }
                    }
                    ServiceBusClients[serviceBusConnectionString] = client;

                    var processor = client.CreateProcessor(queueOrTopicName);
                    ServiceBusProcessors.Add(key, processor);
                    processor.ProcessMessageAsync += async args => await ProcessMessageAsync(args, runAs, agent.Name, agentToCall);
                    processor.ProcessErrorAsync += async args => await ProcessErrorAsync(args, agent.Name);
                    await processor.StartProcessingAsync();
                }
                catch (Exception e)
                {
                    agent.Content["lastResult"].Value = $"Error: {e.Message}";
                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                    await _agentsProcessor.Update(agent);
                }
            }
            // Remove old processors
            var currentServiceBusProcessors = ServiceBusProcessors.ToList();
            foreach (var processor in currentServiceBusProcessors)
            {
                if (!processedAgents.Contains(processor.Key))
                {
                    await processor.Value.StopProcessingAsync();
                }
            }
        }

        private async Task ProcessErrorAsync(ProcessErrorEventArgs args, string currentAgentName)
        {
            using var scope = ServiceProvider.CreateScope();
            var agentsProcessor = scope.ServiceProvider.GetRequiredService<IAgentsProcessor>();

            var agents = await agentsProcessor.List(null);
            var agent = agents.FirstOrDefault(item => item.Name == currentAgentName);

            agent.Content["lastResult"].Value = $"ProcessError: {args.Exception.Message}";
            agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
            await agentsProcessor.Update(agent);
        }

        private async Task ProcessMessageAsync(ProcessMessageEventArgs args, int runAs, string currentAgentName, string agentToCallName)
        {
            using var scope = ServiceProvider.CreateScope();
            var agentsProcessor = scope.ServiceProvider.GetRequiredService<IAgentsProcessor>();

            var agents = await agentsProcessor.List(null);
            var agent = agents.FirstOrDefault(item => item.Name == currentAgentName);

            try
            {
                var body = System.Text.Encoding.UTF8.GetString(args.Message.Body.ToArray());
                var parametersValues = new Dictionary<string, string>
                {
                    {"parameter1", body}
                };

                var cts = new CancellationTokenSource();
                var renewalTask = RenewLockAsync(args.Message, args, cts.Token);
                var availableAgents = await _agentsProcessor.List(agent.WorkspaceId);
                await RunAgent("AzureServiceBus", availableAgents, agent, agentToCallName, runAs, parametersValues);
                await agentsProcessor.Update(agent);
                await args.CompleteMessageAsync(args.Message);
                cts.Cancel();
                await renewalTask;
            }
            catch (Exception ex)
            {
                agent.Content["lastResult"].Value = $"Error: {ex.Message}";
                agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                await agentsProcessor.Update(agent);
            }
        }

        private async Task RenewLockAsync(ServiceBusReceivedMessage message, ProcessMessageEventArgs args, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), token);
                    await args.RenewMessageLockAsync(message);
                }
                catch (Exception ex) when (token.IsCancellationRequested)
                {
                    // Ignore exceptions if cancellation is requested
                }
            }
        }
    }

    public interface IAzureServiceBusListenerAgentService
    {
        public Task ProcessTask();
    }
}