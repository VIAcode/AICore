using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.Graph.Models;
using System.IdentityModel.Tokens.Jwt;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class GraphTeamsNotificationAgent : BaseAgent, IGraphTeamsNotificationAgent
    {
        private string _debugMessageSenderName = "GraphTeamsNotificationAgent";

        private static class AgentContentParameters
        {
            public const string ConnectionName = "connectionName";
            public const string TargetType = "targetType";
            public const string UserEmail = "userEmail";
            public const string MeetingTitle = "meetingTitle";
            public const string ChannelId = "channelId";
            public const string TeamId = "teamId";
            public const string Body = "body";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public GraphTeamsNotificationAgent(
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            MonitoringConfig monitoringConfig,
            ILogger<GraphTeamsNotificationAgent> logger)
            : base(responseAccessor, requestAccessor, monitoringConfig, logger)
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
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var targetType = agent.Content.ContainsKey(AgentContentParameters.TargetType) ? ApplyParameters(agent.Content[AgentContentParameters.TargetType].Value, parameters) : string.Empty;
            var userEmail = agent.Content.ContainsKey(AgentContentParameters.UserEmail) ? ApplyParameters(agent.Content[AgentContentParameters.UserEmail].Value, parameters) : string.Empty;
            var meetingTitle = agent.Content.ContainsKey(AgentContentParameters.MeetingTitle) ? ApplyParameters(agent.Content[AgentContentParameters.MeetingTitle].Value, parameters) : string.Empty;
            var channelId = agent.Content.ContainsKey(AgentContentParameters.ChannelId) ? ApplyParameters(agent.Content[AgentContentParameters.ChannelId].Value, parameters) : string.Empty;
            var teamId = agent.Content.ContainsKey(AgentContentParameters.TeamId) ? ApplyParameters(agent.Content[AgentContentParameters.TeamId].Value, parameters) : string.Empty;
            var body = agent.Content.ContainsKey(AgentContentParameters.Body) ? ApplyParameters(agent.Content[AgentContentParameters.Body].Value, parameters) : string.Empty;

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"TargetType: {targetType}, TargetId: {userEmail}{meetingTitle}{teamId}{channelId}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.GraphApi, _debugMessageSenderName, connectionName: connectionName);

            var resourceName = connection.Content["resourceName"];
            var accessType = connection.Content.GetValueOrDefault("accessType") ?? EntraTokenProvider.DefaultStorageName;

            var hasRefreshToken = connection.Content.TryGetValue("refreshToken", out var refreshToken);
            var accessToken = hasRefreshToken
                ? await _entraTokenProvider.GetAccessTokenByRefreshTokenAsync(accessType, refreshToken, resourceName)
                : await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, resourceName);

            var jsonToken = new JwtSecurityTokenHandler().ReadToken(accessToken.Token) as JwtSecurityToken;
            var myId = jsonToken.Payload["oid"].ToString();

            var tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
            using var httpClient = _httpClientFactory.CreateClient("NoRetryClient");
            var graphClient = new GraphServiceClient(httpClient, tokenCredential);

            await SendTeamsMessageAsync(graphClient, targetType, userEmail, meetingTitle, channelId, teamId, body, myId);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"Teams message sent.");
            return "Teams message sent.";
        }

        private async Task SendTeamsMessageAsync(GraphServiceClient graphClient, string targetType, string? userEmail, string? meetingTitle, string? channelId, string? teamId, string messageText, string myId)
        {
            var message = new ChatMessage
            {
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = messageText
                }
            };
            if (targetType == "channel")
            {
                await graphClient.Teams[teamId].Channels[channelId].Messages.PostAsync(message);
                return;
            }
            var chat = await FindChatAsync(graphClient, targetType, userEmail, meetingTitle);
            if (chat == null && targetType == "user") // create new chat
            { 
                chat = await CreateOneOnOneChatAsync(graphClient, userEmail, myId);
            }
            if (chat != null)
            {
                await graphClient.Chats[chat.Id].Messages.PostAsync(message);
                return;
            }
            throw new ExceptionHandlingMiddleware.AiCoreUiException($"Unable to send chat message for: {userEmail}{meetingTitle}{teamId}{channelId}.");
        }

        private async Task<Chat> CreateOneOnOneChatAsync(GraphServiceClient graphClient, string userEmail, string myId)
        {
            // Resolve userId from email
            var user = await graphClient.Users[userEmail].GetAsync();
            if (user == null || string.IsNullOrWhiteSpace(user.Id))
                throw new Exception($"User not found: {userEmail}");


            var chat = new Chat
            {
                ChatType = ChatType.OneOnOne,
                Members = new List<ConversationMember>
                {
                    new AadUserConversationMember
                    {
                        Roles = new List<string> { "owner" },
                        AdditionalData = new Dictionary<string, object>
                        {
                            { "user@odata.bind", $"https://graph.microsoft.com/v1.0/users('{myId}')" }
                        }
                    },
                    new AadUserConversationMember
                    {
                        Roles = new List<string> { "owner" },
                        AdditionalData = new Dictionary<string, object>
                        {
                            { "user@odata.bind", $"https://graph.microsoft.com/v1.0/users('{user.Id}')" }
                        }
                    }
                }
            };

            return await graphClient.Chats.PostAsync(chat);
        }

        private async Task<Chat?> FindChatAsync(GraphServiceClient graphClient, string targetType, string? userEmail, string? meetingTitle)
        {
            var allChats = new List<Chat>();
            var response = await graphClient.Me.Chats
                .GetAsync(config =>
                {
                    config.QueryParameters.Top = 50; // Maximum allowed value
                    config.QueryParameters.Select = new[] { "id", "topic", "chatType" };
                    config.QueryParameters.Expand = new[] { "members" };
                });

            var chat = response?.Value.FirstOrDefault(item =>
                targetType == "user" && item.ChatType == ChatType.OneOnOne && item.Members.Any(member => 
                    member is AadUserConversationMember && (member as AadUserConversationMember).Email.ToUpper() == userEmail.ToUpper()) ||
                targetType == "meeting" && item.ChatType == ChatType.Meeting && item.Topic.ToUpper() == meetingTitle.ToUpper());
            if (chat != null)
                return chat;

            if (response?.Value != null)
                allChats.AddRange(response.Value);

            // Handle pagination
            while (!string.IsNullOrEmpty(response?.OdataNextLink))
            {
                response = await graphClient.Me.Chats
                    .WithUrl(response.OdataNextLink)
                    .GetAsync();

                var paginationChat = response?.Value.FirstOrDefault(item =>
                    targetType == "user" && item.ChatType == ChatType.OneOnOne && item.Members.Any(member =>
                        member is AadUserConversationMember && (member as AadUserConversationMember).Email.ToUpper() == userEmail.ToUpper()) ||
                    targetType == "meeting" && item.ChatType == ChatType.Meeting && item.Topic?.ToUpper() == meetingTitle.ToUpper());
                if (paginationChat != null)
                    return paginationChat;

                if (response?.Value != null)
                    allChats.AddRange(response.Value);
            }
            return null;
        }

    }
    public interface IGraphTeamsNotificationAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
