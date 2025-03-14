using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Web;
using AiCoreApi.Data.Processors;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class AzureServiceBusNotificationAgent : BaseAgent, IAzureServiceBusNotificationAgent
    {
        private const string DebugMessageSenderName = "AzureServiceBusNotificationAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string QueueOrTopicName = "queueOrTopicName";
            public const string NotificationPayload = "notificationPayload";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public AzureServiceBusNotificationAgent(
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<AzureServiceBusNotificationAgent> logger) : base(requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var queueOrTopicName = ApplyParameters(agent.Content[AgentContentParameters.QueueOrTopicName].Value, parameters);
            var notificationPayload = ApplyParameters(agent.Content[AgentContentParameters.NotificationPayload].Value, parameters);

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Request", notificationPayload);
            var connections = await _connectionProcessor.List();
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureServiceBus, DebugMessageSenderName, connectionName: connectionName);
            var serviceBusConnectionString = connection.Content["serviceBusConnectionString"];

            await SendNotification(serviceBusConnectionString, queueOrTopicName, notificationPayload);

            var responseMessage = $"Message sent to {queueOrTopicName}.";
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "DoCall Response", responseMessage);
            return responseMessage;
        }

        private async Task SendNotification(string connectionString, string queueOrTopicName, string payload)
        {
            await using var client = new ServiceBusClient(connectionString);
            var sender = client.CreateSender(queueOrTopicName);
            try
            {
                var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(payload))
                {
                    ContentType = "application/json",
                    MessageId = Guid.NewGuid().ToString()
                };
                await sender.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to send message to Service Bus: {ex.Message}", ex);
            }
            finally
            {
                await sender.DisposeAsync();
            }
        }
    }

    public interface IAzureServiceBusNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
