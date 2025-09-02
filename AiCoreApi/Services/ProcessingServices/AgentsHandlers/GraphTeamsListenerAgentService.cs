using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph.Models;
using Microsoft.Graph;
using System.Collections.Concurrent;
using AiCoreApi.Common.Extensions;
using System.IdentityModel.Tokens.Jwt;

namespace AiCoreApi.Services.ProcessingServices.AgentsHandlers
{
    public class GraphTeamsListenerAgentService : AgentServiceBase, IGraphTeamsListenerAgentService
    {
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private bool _isInProgress;
        private static readonly ConcurrentDictionary<string, string> _chatIdByEmailCache = new();

        public GraphTeamsListenerAgentService(
            ILoginProcessor loginProcessor,
            IAgentsProcessor agentsProcessor,
            IDebugLogProcessor debugLogProcessor,
            ExtendedConfig extendedConfig,
            IServiceScopeFactory scopeFactory,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            IEntraTokenProvider entraTokenProvider)
            : base(loginProcessor, debugLogProcessor, extendedConfig, scopeFactory)
        {
            _agentsProcessor = agentsProcessor;
            _connectionProcessor = connectionProcessor;
            _entraTokenProvider = entraTokenProvider;
            _httpClientFactory = httpClientFactory;
        }



        public async Task ProcessTask(List<AgentModel> agents)
        {
            if (_isInProgress)
                return;
            try
            {
                _isInProgress = true;
                await ProcessTaskLocked(agents);
            }
            finally
            {
                _isInProgress = false;
            }
        }

        private async Task ProcessTaskLocked(List<AgentModel> agents)
        {
            var teamsAgents = agents.Where(a => a.Type == AgentType.GraphTeamsListener && a.IsEnabled).ToList();

            foreach (var agent in teamsAgents)
            {
                var connectionName = agent.Content["connectionName"].Value;
                var agentToCall = agent.Content["agentToCall"].Value;
                var runAs = int.Parse(agent.Content["runAs"].Value);
                var secondsBetweenChecks = int.Parse(agent.Content.GetValueOrDefault("secondsBetweenChecks")?.Value ?? "30");

                var now = DateTime.UtcNow;
                var lastRun = agent.Content.TryGetValue("lastRun", out var lastRunValue) && lastRunValue.Value != "Never"
                    ? DateTime.Parse(lastRunValue.Value)
                    : now.AddMinutes(-5);

                if (now < lastRun + TimeSpan.FromSeconds(secondsBetweenChecks))
                    continue;

                var connList = await _connectionProcessor.List(agent.WorkspaceId);
                var conn = connList.FirstOrDefault(c => c.Type == ConnectionType.GraphApi && c.Name == connectionName);
                if (conn == null) continue;

                var resourceName = conn.Content["resourceName"];
                var tenantId = conn.Content.GetValueOrDefault("tenantId");
                var accessType = conn.Content.GetValueOrDefault("accessType") ?? EntraTokenProvider.DefaultStorageName;

                try
                {
                    var hasRefreshToken = conn.Content.TryGetValue("refreshToken", out var refreshToken);
                    var accessToken = hasRefreshToken
                        ? await _entraTokenProvider.GetAccessTokenByRefreshTokenAsync(accessType, refreshToken, resourceName, tenantId)
                        : await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, resourceName);

                    var jsonToken = new JwtSecurityTokenHandler().ReadToken(accessToken.Token) as JwtSecurityToken;
                    var myId = jsonToken.Payload["oid"].ToString();

                    var tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
                    var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                    var graphClient = new GraphServiceClient(httpClient, tokenCredential);

                    var messageFromList = agent.Content["messageFrom"].Value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(email => email.ToUpperInvariant())
                        .ToHashSet();

                    // Step 1: Filter out already cached chatIds
                    var unresolvedEmails = messageFromList
                        .Where(email => !_chatIdByEmailCache.ContainsKey($"{connectionName}_{email}"))
                        .ToHashSet();

                    // Step 2: Only call Graph API if there are unresolved chat IDs
                    if (unresolvedEmails.Any())
                    {
                        var chatPage = await graphClient.Me.Chats.GetAsync(config =>
                        {
                            config.QueryParameters.Filter = "chatType eq 'oneOnOne'";
                            config.QueryParameters.Select = new[] { "id", "chatType" };
                            config.QueryParameters.Top = 50;
                        });

                        var allChats = new List<Chat>();
                        while (chatPage != null)
                        {
                            if (chatPage.Value != null)
                                allChats.AddRange(chatPage.Value);

                            if (string.IsNullOrEmpty(chatPage.OdataNextLink)) break;

                            chatPage = await graphClient.Me.Chats.WithUrl(chatPage.OdataNextLink).GetAsync();
                        }

                        foreach (var chat in allChats)
                        {
                            var members = await graphClient.Chats[chat.Id].Members.GetAsync();
                            foreach (var member in members.Value.OfType<AadUserConversationMember>())
                            {
                                var email = member.Email?.ToUpperInvariant();
                                if (!string.IsNullOrWhiteSpace(email) && unresolvedEmails.Contains(email))
                                {
                                    _chatIdByEmailCache.TryAdd($"{connectionName}_{email}", chat.Id);
                                }
                            }
                        }
                    }

                    // Step 3: Process available chats from cache
                    foreach (var email in messageFromList)
                    {
                        if (!_chatIdByEmailCache.TryGetValue($"{connectionName}_{email}", out var chatId)) continue;

                        var messagePage = await graphClient.Chats[chatId].Messages.GetAsync();

                        foreach (var message in messagePage.Value ?? new List<ChatMessage>())
                        {
                            // Skip if the message is older than lastRun
                            if (message.CreatedDateTime < lastRun)
                                continue;
                            // Skip messages sent by me
                            if (message.From?.User?.Id == myId)
                                continue; 

                            var parameters = new Dictionary<string, string>
                            {
                                { "messageText", message.Body?.Content ?? "" },
                                { "displayName", message.From?.User?.DisplayName ?? "" },
                                { "email", email },
                                { "createdDateTime", message.CreatedDateTime?.ToString("o") ?? "" },
                                { "messageId", message.Id },
                                { "chatId", chatId }
                            };

                            var allAgents = await _agentsProcessor.List(agent.WorkspaceId);
                            var callParams = new Dictionary<string, string>
                            {
                                { "parameter1", parameters.ToJson() }
                            };
                            await RunAgent("GraphTeamsListener", allAgents, agent, agentToCall, runAs, callParams);
                        }
                    }
                    agent.Content["lastRun"].Value = now.ToString("o");
                    await _agentsProcessor.Update(agent);
                }
                catch (Exception ex)
                {
                    agent.Content["lastResult"].Value = $"Graph error: {ex.Message}";
                    agent.Content["lastRun"].Value = now.ToString("o");
                    await _agentsProcessor.Update(agent);
                }
            }
        }
    }
    public interface IGraphTeamsListenerAgentService
    {
        Task ProcessTask(List<AgentModel> agents);
    }
}