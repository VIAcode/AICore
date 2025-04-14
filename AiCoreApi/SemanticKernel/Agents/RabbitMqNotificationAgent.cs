using System.Text;
using RabbitMQ.Client;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Web;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class RabbitMqNotificationAgent : BaseAgent, IRabbitMqNotificationAgent
    {
        private string _debugMessageSenderName = "RabbitMqNotificationAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string QueueOrTopicName = "queueOrTopicName";
            public const string NotificationPayload = "notificationPayload";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public RabbitMqNotificationAgent(
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<RabbitMqNotificationAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var queueOrTopicName = ApplyParameters(agent.Content[AgentContentParameters.QueueOrTopicName].Value, parameters);
            var notificationPayload = ApplyParameters(agent.Content[AgentContentParameters.NotificationPayload].Value, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", notificationPayload);
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.RabbitMq, _debugMessageSenderName, connectionName: connectionName);
            var rabbitMqConnectionString = connection.Content["rabbitMqConnectionString"];

            SendNotification(rabbitMqConnectionString, queueOrTopicName, notificationPayload);

            var responseMessage = $"Message sent to {queueOrTopicName}.";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", responseMessage);
            return responseMessage;
        }

        private void SendNotification(string connectionString, string queueOrTopicName, string payload)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                DispatchConsumersAsync = true
            };

            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();

            channel.QueueDeclare(queue: queueOrTopicName,
                                 durable: true,
                                 exclusive: false,
                                 autoDelete: false,
                                 arguments: null);

            var body = Encoding.UTF8.GetBytes(payload);

            var properties = channel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.MessageId = Guid.NewGuid().ToString();

            channel.BasicPublish(exchange: "",
                                 routingKey: queueOrTopicName,
                                 basicProperties: properties,
                                 body: body);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Message Sent", payload);
        }
    }

    public interface IRabbitMqNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
