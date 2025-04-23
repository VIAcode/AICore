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
        private string _debugMessageSenderName = "AzureServiceBusNotificationAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string QueueOrTopicName = "queueOrTopicName";
            public const string NotificationPayload = "notificationPayload";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public AzureServiceBusNotificationAgent(
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<AzureServiceBusNotificationAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var queueOrTopicName = ApplyParameters(agent.Content[AgentContentParameters.QueueOrTopicName].Value, parameters);
            var notificationPayload = ApplyParameters(agent.Content[AgentContentParameters.NotificationPayload].Value, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", notificationPayload);
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureServiceBus, _debugMessageSenderName, connectionName: connectionName);

            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";
            ServiceBusClient serviceBusClient;

            if (accessType == "apiKey")
            {
                var serviceBusConnectionString = connection.Content["serviceBusConnectionString"];
                serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
            }
            else
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://servicebus.azure.net/.default");
                var serviceBusNamespace = connection.Content["serviceBusNamespace"];
                if (!serviceBusNamespace.StartsWith("https://"))
                    serviceBusNamespace = $"https://{serviceBusNamespace}";

                serviceBusClient = new ServiceBusClient(serviceBusNamespace, new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn));
            }

            await SendNotification(serviceBusClient, queueOrTopicName, notificationPayload);

            var responseMessage = $"Message sent to {queueOrTopicName}.";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", responseMessage);
            return responseMessage;
        }

        private async Task SendNotification(ServiceBusClient client, string queueOrTopicName, string payload)
        {
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
