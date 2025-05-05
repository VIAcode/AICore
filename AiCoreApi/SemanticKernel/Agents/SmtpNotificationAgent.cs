using System.Net;
using System.Net.Mail;
using System.Web;
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
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;

        public SmtpNotificationAgent(
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ILogger<SmtpNotificationAgent> logger)
            : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var recipient = ApplyParameters(agent.Content[AgentContentParameters.Recipient].Value, parameters);
            var cc = ApplyParameters(agent.Content[AgentContentParameters.Cc].Value, parameters);
            var subject = ApplyParameters(agent.Content[AgentContentParameters.Subject].Value, parameters);
            var body = ApplyParameters(agent.Content[AgentContentParameters.Body].Value, parameters);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"To: {recipient}, Cc: {cc} Subject: {subject}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.Smtp, _debugMessageSenderName, connectionName: connectionName);
            var smtpServer = connection.Content["smtpServer"];
            var smtpPort = int.Parse(connection.Content["smtpPort"]);
            var smtpUser = ApplyParameters(connection.Content["smtpUser"], parameters);
            var smtpPass = ApplyParameters(connection.Content["smtpPassword"], parameters);
            var smtpFrom = ApplyParameters(connection.Content["smtpFrom"], parameters);

            SendEmail(smtpServer, smtpPort, smtpUser, smtpPass, smtpFrom, recipient, cc, subject, body);

            var response = $"Email sent to {recipient}.";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", response);
            return response;
        }

        private void SendEmail(string host, int port, string username, string password, string from, string to, string cc, string subject, string body)
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

            client.Send(message);
        }
    }

    public interface ISmtpNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
