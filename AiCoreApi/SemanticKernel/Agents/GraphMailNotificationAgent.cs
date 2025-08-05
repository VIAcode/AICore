using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.Graph.Models;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class GraphMailNotificationAgent : BaseAgent, IGraphMailNotificationAgent
    {
        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Recipient = "recipient";
            public const string Cc = "cc";
            public const string Subject = "subject";
            public const string Body = "body";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public GraphMailNotificationAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            MonitoringConfig monitoringConfig,
            ILogger<GraphMailNotificationAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            var debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var recipient = await GetParameterValueAsync(AgentContentParameters.Recipient);
            var cc = await GetParameterValueAsync(AgentContentParameters.Cc);
            var subject = await GetParameterValueAsync(AgentContentParameters.Subject);
            var body = await GetParameterValueAsync(AgentContentParameters.Body);

            _responseAccessor.AddDebugMessage(debugMessageSenderName, "DoCall Request", $"To: {recipient}, Cc: {cc} Subject: {subject}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.GraphApi, debugMessageSenderName, connectionName: connectionName);

            var resourceName = connection.Content["resourceName"];
            var accessType = connection.Content.GetValueOrDefault("accessType") ?? EntraTokenProvider.DefaultStorageName;
            var tenantId = connection.Content.GetValueOrDefault("tenantId");

            var hasRefreshToken = connection.Content.TryGetValue("refreshToken", out var refreshToken);
            var accessToken = hasRefreshToken
                ? await _entraTokenProvider.GetAccessTokenByRefreshTokenAsync(accessType, refreshToken, resourceName, tenantId)
                : await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, resourceName);

            var tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
            var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var graphClient = new GraphServiceClient(httpClient, tokenCredential);

            await SendEmailAsync(graphClient, recipient, cc, subject, body);
            _responseAccessor.AddDebugMessage(debugMessageSenderName, "DoCall Response", $"Email message sent.");
            return "Email message sent.";
        }

        private async Task SendEmailAsync(GraphServiceClient graphClient, string to, string? cc, string subject, string messageText)
        {
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = messageText
                }
            };
            message.ToRecipients = to.Split([',', ';']).Select(s => new Recipient { EmailAddress = new EmailAddress() { Address = s.Trim() } }).ToList();
            if (!string.IsNullOrWhiteSpace(cc))
            {
                message.CcRecipients = cc.Split([',', ';']).Select(s => new Recipient { EmailAddress = new EmailAddress() { Address = s.Trim() } }).ToList();
            }

            await graphClient.Me
                .SendMail.PostAsync(new Microsoft.Graph.Me.SendMail.SendMailPostRequestBody() { Message = message, SaveToSentItems = true });
        }

    }
    public interface IGraphMailNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
