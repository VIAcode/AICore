using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using System.Collections.Concurrent;
using System.Text;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class ImapListenerAgentService : AgentServiceBase, IImapListenerAgentService
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IConnectionManager _connectionManager;
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> ImapListeners = new();

        public ImapListenerAgentService(
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceScopeFactory scopeFactory,
            IConnectionManager connectionManager)
            : base(loginProcessor, debugLogProcessor, extendedConfig, scopeFactory)
        {
            _agentsProcessor = agentsProcessor;
            _connectionManager = connectionManager;
        }

        public async Task ProcessTask(List<AgentModel> agents)
        {
            var imapAgents = agents.Where(a => a.Type == AgentType.Imap && a.IsEnabled).ToList();
            var processedAgents = new List<string>();

            foreach (var agent in imapAgents)
            {
                var connName = agent.Content["connectionName"].Value;
                var folderName = agent.Content.GetValueOrDefault("folderName")?.Value ?? "INBOX";
                var agentToCall = agent.Content["agentToCall"].Value;
                var runAs = int.Parse(agent.Content["runAs"].Value);

                var key = $"{connName}|{folderName}|{agentToCall}|{runAs}".GetHash();
                processedAgents.Add(key);

                if (ImapListeners.ContainsKey(key))
                    continue;

                var connection = await _connectionManager.GetConnectionWithParams(agent.WorkspaceId, connectionName: connName, connectionType: ConnectionType.Imap);
                if (connection == null)
                {
                    agent.Content["lastResult"].Value = $"Connection not found: {connName}";
                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                    await _agentsProcessor.Update(agent);
                    continue;
                }

                var host = connection.Content["imapServer"];
                var port = int.Parse(connection.Content["imapPort"]);
                var user = connection.Content["emailAddress"];
                var pass = connection.Content["password"];
                var useSsl = bool.Parse(connection.Content["useSSL"]);

                var cts = new CancellationTokenSource();
                ImapListeners[key] = cts;

                _ = Task.Run(async () =>
                {
                    using var client = new ImapClient();
                    try
                    {
                        await client.ConnectAsync(host, port, useSsl, cts.Token);
                        await client.AuthenticateAsync(user, pass, cts.Token);
                        var inbox = client.GetFolder(folderName);
                        await inbox.OpenAsync(FolderAccess.ReadWrite);

                        inbox.CountChanged += async (s, e) =>
                        {
                            try
                            {
                                var uid = (await inbox.SearchAsync(SearchQuery.NotSeen, cts.Token)).LastOrDefault();
                                if (uid != null)
                                {
                                    var message = await inbox.GetMessageAsync(uid, cts.Token);
                                    await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken: cts.Token);

                                    var parameters = new Dictionary<string, string>
                                    {
                                        { "subject", message.Subject },
                                        { "bodyBase64",  Convert.ToBase64String(Encoding.UTF8.GetBytes(message.TextBody ?? message.HtmlBody)) },
                                        { "from", message.From.ToString() }
                                    };

                                    var allAgents = await _agentsProcessor.List(agent.WorkspaceId);
                                    await RunAgent("Imap", allAgents, agent, agentToCall, runAs, parameters);

                                    agent.Content["lastResult"].Value = $"Last email processed: {message.Subject}";
                                    agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                                    await _agentsProcessor.Update(agent);
                                }
                            }
                            catch (Exception ex)
                            {
                                agent.Content["lastResult"].Value = $"Error: {ex.Message}";
                                agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                                await _agentsProcessor.Update(agent);
                            }
                        };
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                await client.IdleAsync(cts.Token);
                            }
                            catch (ImapProtocolException)
                            {
                                // Reconnect and re-authenticate if the server drops the connection
                                if (client.IsConnected)
                                    await client.DisconnectAsync(true, cts.Token);

                                await client.ConnectAsync(host, port, useSsl, cts.Token);
                                await client.AuthenticateAsync(user, pass, cts.Token);
                                await inbox.OpenAsync(FolderAccess.ReadWrite, cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Exit the loop if cancellation is requested
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        agent.Content["lastResult"].Value = $"Connection error: {ex.Message}";
                        agent.Content["lastRun"].Value = DateTime.UtcNow.ToString("o");
                        await _agentsProcessor.Update(agent);
                    }
                    finally
                    {
                        if (client.IsConnected)
                            await client.DisconnectAsync(true);
                    }
                }, cts.Token);
            }
            // Cancel old listeners
            foreach (var listener in ImapListeners.ToList())
            {
                if (!processedAgents.Contains(listener.Key))
                {
                    await listener.Value.CancelAsync();
                    ImapListeners.TryRemove(listener.Key, out _);
                }
            }
        }
    }

    public interface IImapListenerAgentService
    {
        Task ProcessTask(List<AgentModel> agents);
    }
}
