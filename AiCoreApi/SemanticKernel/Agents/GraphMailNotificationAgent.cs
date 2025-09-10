using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.Graph.Models;
using AiCoreApi.Common.Monitoring;
using Microsoft.AspNetCore.StaticFiles;
using System.Text.RegularExpressions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class GraphMailNotificationAgent : BaseAgent, IGraphMailNotificationAgent
    {
        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Recipient = "recipient";
            public const string Cc = "cc";
            public const string From = "from";
            public const string Subject = "subject";
            public const string Body = "body";
            public const string Attachments = "attachments";
        }

        private static Regex _recipientRegex = new Regex(@"^(?:""?(?<name>[^""]+)""?\s*)?<(?<email>[^>]+)>$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private string _debugMessageSenderName = nameof(GraphMailNotificationAgent);

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GraphMailNotificationAgent> _logger;

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
            _logger = logger;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            var debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var recipient = await GetParameterValueAsync(AgentContentParameters.Recipient);
            var from = await GetParameterValueAsync(AgentContentParameters.From);
            var cc = await GetParameterValueAsync(AgentContentParameters.Cc);
            var subject = await GetParameterValueAsync(AgentContentParameters.Subject);
            var body = await GetParameterValueAsync(AgentContentParameters.Body);
            var attachmentsRaw = await GetParameterValueAsync(AgentContentParameters.Attachments);

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

            var message = ComposeMessage(to: recipient,
                                         from: from,
                                         cc: cc,
                                         subject: subject,
                                         messageText: body,
                                         attachmentsRaw: attachmentsRaw);

            await graphClient.Me.SendMail.PostAsync(new Microsoft.Graph.Me.SendMail.SendMailPostRequestBody() { Message = message, SaveToSentItems = true });

            _responseAccessor.AddDebugMessage(debugMessageSenderName, "DoCall Response", $"Email message sent.");

            return "Email message sent.";
        }

        private Message ComposeMessage(string to, string? from, string? cc, string subject, string messageText, string? attachmentsRaw)
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

            message.ToRecipients = ParseRecipientList(to);

            if (ParseRecipient(from, out var fromRecipient))
            {
                message.From = fromRecipient;
            }
            if (!string.IsNullOrWhiteSpace(cc))
            {
                message.CcRecipients = ParseRecipientList(cc);
            }
            if (!string.IsNullOrWhiteSpace(attachmentsRaw))
            {
                message.Attachments = ParseAttachments(attachmentsRaw).ToList();
            }

            return message;
        }

        private List<Recipient> ParseRecipientList(string recipients)
        {
            var result = new List<Recipient>();
            foreach (var entry in recipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (ParseRecipient(entry, out var recipient))
                {
                    result.Add(recipient);
                }
            }
            return result;
        }

        private static bool ParseRecipient(string? entry, out Recipient recipient)
        {
            recipient = new Recipient();
            if (string.IsNullOrWhiteSpace(entry))
                return false;

            // Try to match "Name <email@domain.com>"
            var match = _recipientRegex.Match(entry.Trim());
            if (match.Success)
            {
                var name = match.Groups["name"].Value?.Trim();
                var email = match.Groups["email"].Value?.Trim();
                recipient = new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = email,
                        Name = string.IsNullOrWhiteSpace(name) ? null : name
                    }
                };
                return true;
            }
            else
            {
                // Assume it's just an email address
                recipient = new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = entry.Trim(),
                    }
                };
                return true;
            }
        }

        private List<Attachment> ParseAttachments(string attachmentsRaw)
        {
            // Parse attachments (format: "file1.txt:base64data,file2.pdf:base64data")

            var provider = new FileExtensionContentTypeProvider();
            var attachments = new List<Attachment>();

            foreach (var attachmentRaw in attachmentsRaw.Split(','))
            {
                var parts = attachmentRaw.Split(':', 2);
                try
                {
                    var fileName = parts[0].Trim();
                    if (string.IsNullOrEmpty(fileName) || parts.Length < 2)
                    {
                        _logger.LogWarning("Invalid attachment format: {0}", attachmentRaw);
                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Attachment Parse Error", $"Invalid attachment format: {attachmentRaw}");
                        continue;
                    }

                    attachments.Add(new FileAttachment
                    {
                        Name = fileName,
                        ContentBytes = Convert.FromBase64String(parts[1]),
                        ContentType = provider.TryGetContentType(fileName, out var contentType) ? contentType : "application/octet-stream"
                    });
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning($"Attachment Parse Error: Invalid base64 for {parts[0]}: {ex.Message}");
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Attachment Parse Error", $"Invalid base64 for {parts[0]}: {ex.Message}");
                }
            }
            return attachments;
        }
    }
    public interface IGraphMailNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
