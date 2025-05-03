using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Azure.Core;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class GraphMailListenerAgentService : AgentServiceBase, IGraphMailListenerAgentService
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly IHttpClientFactory _httpClientFactory;

        public GraphMailListenerAgentService(
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceProvider serviceProvider,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            IEntraTokenProvider entraTokenProvider)
            : base(loginProcessor, debugLogProcessor, extendedConfig, serviceProvider)
        {
            _agentsProcessor = agentsProcessor;
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _httpClientFactory = httpClientFactory;
        }

        public async Task ProcessTask()
        {
            var agents = await _agentsProcessor.List(null);
            var graphAgents = agents.Where(a => a.Type == AgentType.GraphMail && a.IsEnabled).ToList();

            foreach (var agent in graphAgents)
            {
                var connName = agent.Content["connectionName"].Value;
                var folderName = agent.Content.GetValueOrDefault("folderName")?.Value ?? "Inbox";
                var agentToCall = agent.Content["agentToCall"].Value;
                var emailAddress = agent.Content["emailAddress"].Value;
                var runAs = int.Parse(agent.Content["runAs"].Value);
                var secondsBetweenChecks = int.Parse(agent.Content.ContainsKey("secondsBetweenChecks") ? agent.Content["secondsBetweenChecks"].Value : "30");
                var now = DateTime.UtcNow;
                var lastRun = agent.Content.ContainsKey("lastRun")
                    ? DateTime.Parse(agent.Content["lastRun"].Value)
                    : DateTime.UtcNow.AddMinutes(-5);

                var checkDelay = TimeSpan.FromSeconds(secondsBetweenChecks);
                var nextAllowedRun = lastRun + checkDelay;
                if (now < nextAllowedRun)
                    continue; // Skip if delay hasn't passed
                
                var connList = await _connectionProcessor.List(agent.WorkspaceId);
                var conn = connList.FirstOrDefault(c => c.Type == ConnectionType.GraphApi && c.Name == connName);
                if (conn == null)
                {
                    agent.Content["lastResult"].Value = $"Connection not found: {connName}";
                    agent.Content["lastRun"].Value = now.ToString("o");
                    await _agentsProcessor.Update(agent);
                    continue;
                }

                var resourceName = conn.Content["resourceName"];
                var accessType = conn.Content.GetValueOrDefault("accessType") ?? EntraTokenProvider.DefaultStorageName;
                
                try
                {
                    var hasRefreshToken = conn.Content.TryGetValue("refreshToken", out var refreshToken);
                    var accessToken = hasRefreshToken
                        ? await _entraTokenProvider.GetAccessTokenByRefreshTokenAsync(accessType, refreshToken, resourceName)
                        : await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, resourceName);

                    var messages = await GetNewMessages(emailAddress, folderName, accessToken, lastRun);

                    if (messages.Count == 0)
                    {
                        agent.Content["lastRun"].Value = now.ToString("o");
                        await _agentsProcessor.Update(agent);
                    }
                    else
                    {
                        foreach (var message in messages)
                        {
                            var parameters = new Dictionary<string, string>
                            {
                                { "subject", message.Subject },
                                { "body", message.Body?.Content ?? "" },
                                { "from", message.From?.EmailAddress?.Address ?? "" },
                                { "receivedDateTime", message.ReceivedDateTime?.ToString("o") ?? "" },
                                { "messageId", message.Id },
                            };

                            var allAgents = await _agentsProcessor.List(agent.WorkspaceId);
                            var parametersValues = new Dictionary<string, string>
                            {
                                {"parameter1", parameters.ToJson()}
                            };
                            await RunAgent("GraphMailListener", allAgents, agent, agentToCall, runAs, parametersValues);

                            agent.Content["lastResult"].Value = $"Last email processed: {message.Subject}";
                            agent.Content["lastRun"].Value = now.ToString("o");
                            await _agentsProcessor.Update(agent);
                        }
                    }
                }
                catch (Exception ex)
                {
                    agent.Content["lastResult"].Value = $"Graph API error: {ex.Message}";
                    agent.Content["lastRun"].Value = now.ToString("o");
                    await _agentsProcessor.Update(agent);
                }
            }
        }

        private async Task<List<Message>> GetNewMessages(string userEmail, string folderName, AccessToken accessToken, DateTime sinceTime)
        {
            var tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
            
            using var httpClient = _httpClientFactory.CreateClient("NoRetryClient");
            var graphClient = new GraphServiceClient(httpClient, tokenCredential);

            var response = await graphClient.Users[userEmail]
                .MailFolders[folderName]
                .Messages
                .GetAsync(config =>
                {
                    config.QueryParameters.Filter = $"receivedDateTime gt {sinceTime:O}";
                    config.QueryParameters.Orderby = new[] { "receivedDateTime asc" };
                    config.QueryParameters.Select = new[] { "subject", "body", "from", "receivedDateTime" };
                    config.QueryParameters.Top = 100;
                });

            return response?.Value?.ToList() ?? new List<Message>();
        }
    }

    public interface IGraphMailListenerAgentService
    {
        Task ProcessTask();
    }
}
