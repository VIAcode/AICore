using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class RabbitMqListenerAgentService : AgentServiceBase, IRabbitMqListenerAgentService
    {
        private static readonly Dictionary<string, IModel> RabbitMqChannels = new();
        private static readonly Dictionary<string, IConnection> RabbitMqConnections = new();
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IConnectionProcessor _connectionProcessor;

        public RabbitMqListenerAgentService(
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor, 
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceProvider serviceProvider,
            IConnectionProcessor connectionProcessor)
            : base(loginProcessor, debugLogProcessor, extendedConfig, serviceProvider)
        {
            _agentsProcessor = agentsProcessor;
            _connectionProcessor = connectionProcessor;
        }

        public async Task ProcessTask()
        {
            var agents = await _agentsProcessor.List(null);
            var schedulerAgents = agents.Where(agent => agent.Type == AgentType.RabbitMqListener).ToList();
            var processedAgents = new List<string>();

            foreach (var agent in schedulerAgents)
            {
                if (!agent.IsEnabled)
                    continue;

                if (!agent.Content.ContainsKey("lastResult"))
                    agent.Content.Add("lastResult", new ConfigurableSetting { Value = "", Code = "lastResult", Name = "Last Result" });

                if (!agent.Content.ContainsKey("lastRun"))
                    agent.Content.Add("lastRun", new ConfigurableSetting { Value = "Never", Code = "lastRun", Name = "Last Run" });

                var connectionName = agent.Content["connectionName"].Value;
                var queueName = agent.Content["queueName"].Value;
                var agentToCall = agent.Content["agentToCall"].Value;
                var runAs = Convert.ToInt32(agent.Content["runAs"].Value);

                var key = $"{connectionName}|{queueName}|{agentToCall}|{runAs}".GetHash();
                processedAgents.Add(key);

                if (RabbitMqChannels.ContainsKey(key))
                    continue;

                var connections = await _connectionProcessor.List(agent.WorkspaceId);
                var connection = connections.FirstOrDefault(conn => conn.Type == ConnectionType.RabbitMq && conn.Name == connectionName);
                if (connection == null)
                {
                    agent.Content["lastResult"].Value = $"Connection not found: {connectionName}";
                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                    await _agentsProcessor.Update(agent);
                    continue;
                }

                var rabbitMqConnectionString = connection.Content["rabbitMqConnectionString"];

                var factory = new ConnectionFactory
                {
                    Uri = new Uri(rabbitMqConnectionString)
                };

                try
                {
                    var mqConnection = RabbitMqConnections.ContainsKey(rabbitMqConnectionString)
                        ? RabbitMqConnections[rabbitMqConnectionString]
                        : factory.CreateConnection();
                    RabbitMqConnections[rabbitMqConnectionString] = mqConnection;
                    var channel = mqConnection.CreateModel();
                    RabbitMqChannels[key] = channel;

                    var consumer = new EventingBasicConsumer(channel);
                    consumer.Received += async (model, ea) => await ProcessMessageAsync(ea, runAs, agent.Name, agentToCall);

                    channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
                }
                catch(Exception e)
                {
                    agent.Content["lastResult"].Value = $"Error: {e.Message}";
                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                    await _agentsProcessor.Update(agent);
                }
            }

            // Remove old channels
            var currentRabbitMqChannels = RabbitMqChannels.ToList();
            foreach (var channel in currentRabbitMqChannels)
            {
                if (!processedAgents.Contains(channel.Key))
                {
                    channel.Value.Close();
                    RabbitMqChannels.Remove(channel.Key);
                }
            }
        }

        private async Task ProcessMessageAsync(BasicDeliverEventArgs ea, int runAs, string currentAgentName, string agentToCallName)
        {
            using var scope = ServiceProvider.CreateScope();
            var agentsProcessor = scope.ServiceProvider.GetRequiredService<IAgentsProcessor>();
            var agents = await agentsProcessor.List(null);
            var agent = agents.FirstOrDefault(item => item.Name == currentAgentName);
            try
            {
                var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                var parametersValues = new Dictionary<string, string>
                {
                    {"parameter1", body}
                };
                var availableAgents = await _agentsProcessor.List(agent.WorkspaceId);
                await RunAgent("RabbitMq", availableAgents, agent, agentToCallName, runAs, parametersValues);
                await agentsProcessor.Update(agent);
                RabbitMqChannels.Values.First().BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                agent.Content["lastResult"].Value = $"Error: {ex.Message}";
                agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                await agentsProcessor.Update(agent);
            }
        }
    }

    public interface IRabbitMqListenerAgentService
    {
        Task ProcessTask();
    }
}
