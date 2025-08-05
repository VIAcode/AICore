using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.Graph.Models;
using System.IdentityModel.Tokens.Jwt;

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

            public const string AttachmentId = "attachmentId";
            public const string AttachmentName = "attachmentName";
            public const string AttachmentContentType = "attachmentContentType";
            public const string AttachmentContentUrl = "attachmentContentUrl";
            public const string AttachmentContent = "attachmentContent";
        }

        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;

        public GraphTeamsNotificationAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            ILogger<GraphTeamsNotificationAgent> logger)
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
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var targetType = await GetParameterValueAsync(AgentContentParameters.TargetType);
            var userEmail = await GetParameterValueAsync(AgentContentParameters.UserEmail);
            var meetingTitle = await GetParameterValueAsync(AgentContentParameters.MeetingTitle);
            var channelId = await GetParameterValueAsync(AgentContentParameters.ChannelId);
            var teamId = await GetParameterValueAsync(AgentContentParameters.TeamId);
            var body = await GetParameterValueAsync(AgentContentParameters.Body);

            var attachmentId = await GetParameterValueAsync(AgentContentParameters.AttachmentId);
            var attachmentName = await GetParameterValueAsync(AgentContentParameters.AttachmentName);
            var attachmentContentType = await GetParameterValueAsync(AgentContentParameters.AttachmentContentType);
            var attachmentContent = await GetParameterValueAsync(AgentContentParameters.AttachmentContent);
            var attachmentContentUrl = await GetParameterValueAsync(AgentContentParameters.AttachmentContentUrl);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"TargetType: {targetType}, TargetId: {userEmail}{meetingTitle}{teamId}{channelId}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.GraphApi, _debugMessageSenderName, connectionName: connectionName);

            var resourceName = connection.Content["resourceName"];
            var accessType = connection.Content.GetValueOrDefault("accessType") ?? EntraTokenProvider.DefaultStorageName;

            var hasRefreshToken = connection.Content.TryGetValue("refreshToken", out var refreshToken);
            var accessToken = hasRefreshToken
                ? await _entraTokenProvider.GetAccessTokenByRefreshTokenAsync(accessType, refreshToken, resourceName)
                : await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, resourceName);

            var jsonToken = new JwtSecurityTokenHandler().ReadToken(accessToken.Token) as JwtSecurityToken;
            var myId = jsonToken.Payload["oid"].ToString();

            var tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
            var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var graphClient = new GraphServiceClient(httpClient, tokenCredential);

            var message = ComposeChatMessage(
                messageText: body,
                attachmentId: attachmentId,  
                attachmentContentType: attachmentContentType,
                attachmentContentUrl: attachmentContentUrl,
                attachmentContent: attachmentContent,
                attachmentName: attachmentName);

            await SendTeamsMessageAsync(graphClient, targetType, userEmail, meetingTitle, channelId, teamId, message, myId);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"Teams message sent.");
            return "Teams message sent.";
        }

        private ChatMessage ComposeChatMessage(
            string messageText, 
            string? attachmentId,
            string? attachmentName,
            string? attachmentContentType, 
            string? attachmentContentUrl, 
            string? attachmentContent)
        {
            var message = new ChatMessage
            {
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = messageText
                }
            };

            if (!string.IsNullOrWhiteSpace(attachmentId))
                message.Attachments = new List<ChatMessageAttachment>
                {
                    new ChatMessageAttachment
                    {
                        Id = attachmentId,
                        ContentType = attachmentContentType,
                        ContentUrl = attachmentContentUrl,
                        Content = attachmentContent,
                        Name = attachmentName,                        
                    }
                };

            return message;
        }

        private async Task SendTeamsMessageAsync(GraphServiceClient graphClient, string targetType, string? userEmail, string? meetingTitle, string? channelId, 
            string? teamId, ChatMessage message, string myId)
        {
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
