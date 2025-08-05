namespace AiCoreApi.Common
{
    public class AgentsHelper: IAgentsHelper
    {
        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly Config _config;

        public AgentsHelper(
            IEntraTokenProvider entraTokenProvider,
            Config config)
        {
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

    }

    public interface IAgentsHelper
    {
        string GetAccessToken(string storageName, string refreshToken, string resource, string? tenantId = null);
        string GetAppUrl();
    }
}
