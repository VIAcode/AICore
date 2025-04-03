using AiCoreApi.Models.ViewModels;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using AiCoreApi.Common.Extensions;
using Microsoft.Extensions.Caching.Distributed;

namespace AiCoreApi.Common.SsoSources
{
    public class MicrosoftSso : IMicrosoftSso
    {
        private readonly Config _config;
        private readonly ExtendedConfig _extendedConfig;
        private readonly IDistributedCache _distributedCache;
        private readonly IHttpClientFactory _httpClientFactory;

        public MicrosoftSso(
            Config config,
            ExtendedConfig extendedConfig,
            IDistributedCache distributedCache,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _extendedConfig = extendedConfig;
            _distributedCache = distributedCache;
            _httpClientFactory = httpClientFactory;
        }

        public const string AcrValueConst = "microsoft";

        public static class Parameters
        {
            public const string Domain = "Domain";
            public const string Group = "Group";
            public const string AutoAdmin = "AutoAdmin";
        }

        private const string CodeChallenge = "ThisIsntRandomButItNeedsToBe43CharactersLong";
        private string RedirectUrl => $"{_config.AppUrl}/connect/callback";
        private readonly string _defaultTenant = "common";
        // Directory.Read.All,GroupMember.Read.All,Group.Read.All
        private string Scope => $"{HttpUtility.UrlEncode("https://graph.microsoft.com/")}{_extendedConfig.ClientScope}+offline_access+openid+profile";
        private string Tenant => string.IsNullOrWhiteSpace(_extendedConfig.TenantId) 
            ? _defaultTenant 
            : _extendedConfig.TenantId;

        public async Task<ExtendedTokenModel> GetAccessTokenByCodeAsync(string code)
        {
            using var httpClient = GetHttpClient();
            var body = $"client_id={_extendedConfig.ClientId}"
               + $"&scope={Scope}"
               + $"&code={code}"
               + $"&redirect_uri={HttpUtility.UrlEncode(RedirectUrl)}"
               + "&grant_type=authorization_code"
               + $"&code_verifier={CodeChallenge}"
               + (string.IsNullOrEmpty(_extendedConfig.ClientSecret) ? "" : $"&client_secret={_extendedConfig.ClientSecret}");
            var message = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            using var oauthTokenResponse = await httpClient.SendAsync(message).ConfigureAwait(false);
            var oauthTokenContent = await oauthTokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var accessToken = oauthTokenContent.JsonGet<string>("access_token");
            var refreshToken = oauthTokenContent.JsonGet<string>("refresh_token");
            var jsonToken = new JwtSecurityTokenHandler().ReadToken(accessToken) as JwtSecurityToken;
            var jwtPayload = jsonToken!.Payload!;
            return new ExtendedTokenModel
            {
                AccessToken = accessToken!,
                RefreshToken = refreshToken,
                Email = jwtPayload["unique_name"]!.ToString()!.ToLower(),
                Name = jwtPayload["name"].ToString()!,
                Ip = jwtPayload["ipaddr"].ToString()!,
            };
        }

        public string GetLogoutRedirectUrl() => "https://login.microsoftonline.com/logout.srf";

        public string GetLoginRedirectUrl(string state)
        {
            var url = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?";
            var parameters = $"client_id={_extendedConfig.ClientId}"
                + "&client_info=1"
                + "&response_type=code"
                + $"&redirect_uri={HttpUtility.UrlEncode(RedirectUrl)}"
                + "&response_mode=form_post"
                + $"&scope={Scope}"
                + $"&claims={HttpUtility.UrlEncode("{\"access_token\":{\"xms_cc\":{\"values\":[\"CP1\"]}}}")}"
                + "&grant_type=refresh_token"
                + "&code_challenge_method=plain"
                + $"&code_challenge={CodeChallenge}"
                + $"&state={state}";
            var loginRedirectUrl = url + parameters;
            return loginRedirectUrl;
        }

        public async Task<List<string>> GetUserGroups(ExtendedTokenModel extendedTokenModel)
        {
            var content = await GetCachedAsync("https://graph.microsoft.com/v1.0/me/transitiveMemberOf", extendedTokenModel.AccessToken);
            var groups = content.JsonGet<List<UserGroup>>("value") ?? new List<UserGroup>();
            var groupNames = groups
                .Select(userGroup => userGroup.DisplayName)
                .Where(userGroupName => !string.IsNullOrEmpty(userGroupName))
                .ToList();
            var groupIds = groups
                .Select(userGroup => userGroup.Id)
                .Where(userGroupId => !string.IsNullOrEmpty(userGroupId))
                .ToList();
            return groupNames
                .Union(groupIds)
                .ToList();
        }

        public async Task<List<string>> GetRoleUsers(string rbacRoleName, ExtendedTokenModel extendedTokenModel)
        {
            var rolesList = await GetRolesList(extendedTokenModel);
            var role = rolesList.FirstOrDefault(role => role.DisplayName.ToLower() == rbacRoleName.ToLower());
            if (role == null)
                return new List<string>();
            var content = await GetCachedAsync($"https://graph.microsoft.com/v1.0/directoryRoles/{role.Id}/members", extendedTokenModel.AccessToken);
            var users = content.JsonGet<List<User>>("value") ?? new List<User>();
            return users
                .Select(user => user.UserPrincipalName.ToLower())
                .ToList();
        }

        private async Task<List<Role>> GetRolesList(ExtendedTokenModel extendedTokenModel)
        {
            var content = await GetCachedAsync("https://graph.microsoft.com/v1.0/directoryRoles", extendedTokenModel.AccessToken);
            var roles = content.JsonGet<List<Role>>("value") ?? new List<Role>();
            return roles;
        }

        private async Task<string> GetCachedAsync(string url, string accessToken)
        {
            var cacheKey = $"{url}_{accessToken}";
            var cachedContent = await _distributedCache.GetStringAsync(cacheKey);   
            if (cachedContent != null)
                return cachedContent;
            using var httpClient = GetHttpClient();
            var message = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) }
            };
            using var response = await httpClient.SendAsync(message);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Failed to get data from {url}. Status code: {response.StatusCode}. Reason: {response.ReasonPhrase}");
            var content = await response.Content.ReadAsStringAsync();
            await _distributedCache.SetStringAsync(cacheKey, content, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20)
            });
            return content;
        }

        private HttpClient GetHttpClient() => _httpClientFactory.CreateClient("RetryClient");

        public class UserGroup
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
        }
        public class User
        {
            public string UserPrincipalName { get; set; } = string.Empty;
        }
        public class Role
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
        }
    }

    public class ExtendedTokenModel : TokenModel
    {
        public string Email { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
    }

    public interface IMicrosoftSso
    {
        Task<ExtendedTokenModel> GetAccessTokenByCodeAsync(string code);
        string GetLogoutRedirectUrl();
        string GetLoginRedirectUrl(string state);
        Task<List<string>> GetUserGroups(ExtendedTokenModel extendedTokenModel);
        Task<List<string>> GetRoleUsers(string rbacRoleName, ExtendedTokenModel extendedTokenModel); 
    }
}
