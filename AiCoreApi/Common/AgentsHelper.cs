using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Common
{
    public class AgentsHelper: IAgentsHelper
    {
        private readonly INotificationsProcessor _notificationsProcessor;
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly Config _config;

        public AgentsHelper(
            INotificationsProcessor notificationsProcessor,
            IEntraTokenProvider entraTokenProvider,
            Config config)
        {
            _notificationsProcessor = notificationsProcessor;
            _entraTokenProvider = entraTokenProvider;
            _config = config;
        }

        public string GetAppUrl() => _config.AppUrl;

        public string GetAccessToken(string storageName, string refreshToken, string resource, string? tenantId = null)
        {
            if (string.IsNullOrWhiteSpace(storageName) ||
                string.IsNullOrWhiteSpace(refreshToken) ||
                string.IsNullOrWhiteSpace(resource))
            {
                return "";
            }

            try
            {
                var accessToken = _entraTokenProvider
                    .GetAccessTokenByRefreshTokenAsync(storageName, refreshToken, resource, tenantId)
                    .GetAwaiter()
                    .GetResult();

                if (string.IsNullOrWhiteSpace(accessToken.Token))
                {
                    return "";
                }

                return accessToken.Token;
            }
            catch (Exception ex)
            {
                return "";
            }
        }

        public int AddNotification(string userName, string type, string title, string message, bool inProgress, int workspaceId)
        {
            var notification = new NotificationModel
            {
                Message = message,
                User = userName,
                Type = type,
                Title = title,
                CreatedAt = DateTime.UtcNow,
                InProgress = inProgress,
                WorkspaceId = workspaceId
            };
            var result = _notificationsProcessor
                .Add(notification)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return result.NotificationId;
        }

        public int UpdateNotification(int notificationId, string userName, string type, string title, string message, bool inProgress, int workspaceId)
        {
            var notification = new NotificationModel
            {
                NotificationId = notificationId,
                Message = message,
                User = userName,
                Type = type,
                Title = title,
                CreatedAt = DateTime.UtcNow,
                InProgress = inProgress,
                WorkspaceId = workspaceId
            };
            var result = _notificationsProcessor
                .Update(notification)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return result.NotificationId;
        }
    }

    public interface IAgentsHelper
    {
        string GetAccessToken(string storageName, string refreshToken, string resource, string? tenantId = null);
        string GetAppUrl();
        public int AddNotification(string userName, string type, string title, string message, bool inProgress, int workspaceId);
        public int UpdateNotification(int notificationId, string userName, string type, string title, string message, bool inProgress, int workspaceId);
    }
}
