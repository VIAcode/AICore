using System.Net;
using System.Net.Mail;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class SmtpNotificationAgent : BaseAgent, ISmtpNotificationAgent
    {
        private string _debugMessageSenderName = "SmtpNotificationAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string Recipient = "recipient";
            public const string Cc = "cc";
            public const string Subject = "subject";
            public const string Body = "body";
            public const string Attachments = "attachments";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public SmtpNotificationAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<SmtpNotificationAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var recipient = await GetParameterValueAsync(AgentContentParameters.Recipient);
            var cc = await GetParameterValueAsync(AgentContentParameters.Cc);
            var subject = await GetParameterValueAsync(AgentContentParameters.Subject);
            var body = await GetParameterValueAsync(AgentContentParameters.Body);
            var attachmentsRaw = await GetParameterValueAsync(AgentContentParameters.Attachments);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"To: {recipient}, Cc: {cc}, Subject: {subject}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.Smtp, _debugMessageSenderName, connectionName: connectionName);
            var smtpServer = connection.Content["smtpServer"];
            var smtpPort = int.Parse(connection.Content["smtpPort"]);
            var smtpUser = await ApplyParametersAsync(connection.Content["smtpUser"]);
            var smtpPass = await ApplyParametersAsync(connection.Content["smtpPassword"]);
            var smtpFrom = await ApplyParametersAsync(connection.Content["smtpFrom"]);

            // Parse attachments (format: "file1.txt:base64data,file2.pdf:base64data")
            var attachments = new List<(string FileName, byte[] Content)>();
            if (!string.IsNullOrWhiteSpace(attachmentsRaw))
            {
                var pairs = attachmentsRaw.Split(',');
                foreach (var pair in pairs)
                {
                    var parts = pair.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        try
                        {
                            var fileName = parts[0].Trim();
                            var fileContent = Convert.FromBase64String(parts[1]);
                            attachments.Add((fileName, fileContent));
                        }
                        catch (FormatException ex)
                        {
                            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Attachment Parse Error", $"Invalid base64 for {parts[0]}: {ex.Message}");
                        }
                    }
                }
            }

            SendEmail(smtpServer, smtpPort, smtpUser, smtpPass, smtpFrom, recipient, cc, subject, body, attachments);

            var response = $"Email sent to {recipient}.";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", response);
            return response;
        }

        private void SendEmail(
            string host,
            int port,
            string username,
            string password,
            string from,
            string to,
            string cc,
            string subject,
            string body,
            List<(string FileName, byte[] Content)> attachments)
        {
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(username, password)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };

            message.To.Add(to);

            if (!string.IsNullOrWhiteSpace(cc))
            {
                var ccAddresses = cc.Split(',');
                foreach (var ccAddress in ccAddresses)
                {
                    message.CC.Add(ccAddress.Trim());
                }
            }

            var streams = new List<MemoryStream>();
            try
            {
                foreach (var (fileName, content) in attachments)
                {
                    var stream = new MemoryStream(content);
                    streams.Add(stream);
                    var attachment = new Attachment(stream, fileName);
                    message.Attachments.Add(attachment);
                }
                client.Send(message);
            }
            finally
            {
                foreach (var stream in streams)
                {
                    stream.Dispose();
                }
            }
        }
    }

    public interface ISmtpNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
