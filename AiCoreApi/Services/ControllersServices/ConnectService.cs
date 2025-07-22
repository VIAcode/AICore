using AiCoreApi.Authorization;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.ViewModels;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.SsoSources;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.Services.ControllersServices
{
    public class ConnectService : IConnectService
    {
        private readonly ILoginProcessor _loginProcessor;
        private readonly ILoginHistoryProcessor _loginHistoryProcessor;
        private readonly IClientSsoProcessor _clientSsoProcessor;
        private readonly IMicrosoftSso _microsoftSso;
        private readonly IGroupsProcessor _groupsProcessor;
        private readonly IRbacGroupSyncProcessor _rbacGroupSyncProcessor;
        private readonly IRbacRoleSyncProcessor _rbacRoleSyncProcessor;
        private readonly ILogger<ConnectService> _logger;
        private readonly ExtendedConfig _config;
        private readonly MonitoringConfig _monitoringConfig;

        public ConnectService(
            ILoginProcessor loginProcessor,
            ILoginHistoryProcessor loginHistoryProcessor,
            IClientSsoProcessor clientSsoProcessor,
            IMicrosoftSso microsoftSso,
            IGroupsProcessor groupsProcessor,
            IRbacGroupSyncProcessor rbacGroupSyncProcessor,
            IRbacRoleSyncProcessor rbacRoleSyncProcessor,
            ILogger<ConnectService> logger,
            ExtendedConfig config,
            MonitoringConfig monitoringConfig)
        {
            _loginProcessor = loginProcessor;
            _loginHistoryProcessor = loginHistoryProcessor;
            _clientSsoProcessor = clientSsoProcessor;
            _microsoftSso = microsoftSso;
            _groupsProcessor = groupsProcessor;
            _rbacGroupSyncProcessor = rbacGroupSyncProcessor;
            _rbacRoleSyncProcessor = rbacRoleSyncProcessor;
            _logger = logger;
            _config = config;
            _monitoringConfig = monitoringConfig;
        }

        public async Task<string?> GetCodeByCredentials(string loginName, string password, bool isOfflineMode, string codeChallenge, bool isPermanentToken)
        {
            var login = await _loginProcessor.GetByCredentials(loginName, password);
            if (login == null)
                return null;

            var loginHistory = new LoginHistoryModel
            {
                LoginId = login.LoginId,
                Login = login.Login,
                Code = Guid.NewGuid().ToString(),
                IsOffline = isOfflineMode,
                CodeChallenge = codeChallenge,
                ValidUntilTime = isPermanentToken
                    ? DateTime.UtcNow.AddDays(_config.PermanentTokenExpirationTimeDays)
                    : DateTime.UtcNow.AddMinutes(_config.TokenExpirationTimeMinutes)
            };
            _loginHistoryProcessor.Add(loginHistory);
            return loginHistory.Code;
        }


        public async Task<string> GetCodeBySsoId(ExtendedTokenModel extendedTokenModel, bool isOfflineMode,
            LoginProcessViewModel loginProcessViewModel)
        {
            if (loginProcessViewModel.AcrValues.Contains(MicrosoftSso.AcrValueConst))
                return await GetMicrosoftCodeBySsoId(extendedTokenModel, isOfflineMode, loginProcessViewModel);
            if (loginProcessViewModel.AcrValues.Contains(GoogleSso.AcrValueConst))
                return await GetGoogleCodeBySsoId(extendedTokenModel, isOfflineMode, loginProcessViewModel);
            return "Error: Unsupported SSO type";
        }

        public async Task<string> GetGoogleCodeBySsoId(ExtendedTokenModel extendedTokenModel, bool isOfflineMode, LoginProcessViewModel loginProcessViewModel)
        {
            var login = await _loginProcessor.GetByLogin(extendedTokenModel.Email, LoginTypeEnum.SsoGoogle);
            if (login == null)
            {
                var userDomain = extendedTokenModel.Email.Split('@')[1].ToLower();
                var clientSsoList = (await _clientSsoProcessor
                    .List())
                    .Where(sso => 
                        sso.LoginType == LoginTypeEnum.SsoGoogle &&
                        (!sso.Settings.ContainsKey(GoogleSso.Parameters.Domain)
                            || string.IsNullOrEmpty(sso.Settings[GoogleSso.Parameters.Domain])
                            || sso.Settings[GoogleSso.Parameters.Domain] == userDomain) &&
                        (
                            !sso.Settings.ContainsKey(GoogleSso.Parameters.EmailRegex) ||
                            string.IsNullOrWhiteSpace(sso.Settings[GoogleSso.Parameters.EmailRegex]) ||
                            sso.Settings[GoogleSso.Parameters.EmailRegex].Split(';').Any(e => Regex.IsMatch(extendedTokenModel.Email, e))
                        ))
                    .ToList();

                // If the user is not in the allowed domain return null
                if (clientSsoList.Count == 0)
                    return "Error: User not in allowed domain or not match the criteria.";
                // Get all attached groups
                var groups = clientSsoList.SelectMany(e => e.Groups).DistinctBy(e => e.GroupId).ToList();
                var autoAdmin = clientSsoList.Any(sso => sso.Settings.ContainsKey(GoogleSso.Parameters.AutoAdmin) && sso.Settings[GoogleSso.Parameters.AutoAdmin] == "True");
                var dailyTokenLimitSetting = clientSsoList.FirstOrDefault(sso => sso.Settings.ContainsKey(GoogleSso.Parameters.DailyTokenLimit))?.Settings[MicrosoftSso.Parameters.DailyTokenLimit];
                int dailyTokenLimit = 0;
                if (!string.IsNullOrEmpty(dailyTokenLimitSetting) && int.TryParse(dailyTokenLimitSetting, out var parsedDailyTokenLimit))
                {
                    dailyTokenLimit = parsedDailyTokenLimit;
                }
                login = await _loginProcessor.Add(new LoginModel
                {
                    Login = extendedTokenModel.Email,
                    Email = extendedTokenModel.Email,
                    FullName = extendedTokenModel.Name,
                    Role = autoAdmin ? RoleEnum.Admin : RoleEnum.User,
                    LoginType = LoginTypeEnum.SsoGoogle,
                    IsEnabled = true,
                    Created = DateTime.UtcNow,
                    CreatedBy = "system",
                    TokensLimit = dailyTokenLimit,
                    Groups = groups
                });
            }

            if (!login.IsEnabled)
                return "Error: Login is disabled";

            var loginHistory = new LoginHistoryModel
            {
                LoginId = login.LoginId,
                Login = login.Login,
                Code = Guid.NewGuid().ToString(),
                IsOffline = isOfflineMode,
                CodeChallenge = loginProcessViewModel.CodeChallenge,
                ValidUntilTime = loginProcessViewModel.IsPermanentToken
                    ? DateTime.UtcNow.AddDays(_config.PermanentTokenExpirationTimeDays)
                    : DateTime.UtcNow.AddMinutes(_config.TokenExpirationTimeMinutes)
            };
            _loginHistoryProcessor.Add(loginHistory);
            return loginHistory.Code;
        }

        public async Task<string> GetMicrosoftCodeBySsoId(ExtendedTokenModel extendedTokenModel, bool isOfflineMode, LoginProcessViewModel loginProcessViewModel)
        {
            var login = await _loginProcessor.GetByLogin(extendedTokenModel.Email, LoginTypeEnum.SsoMicrosoft);
            var userGroups = await _microsoftSso.GetUserGroups(extendedTokenModel);
            int dailyTokenLimit = 0;

            // If the user is not found, check if the user is in the allowed domain & group and create a new login
            if (login == null)
            {
                var userDomain = extendedTokenModel.Email.Split('@')[1].ToLower();
                var clientSsoList = (await _clientSsoProcessor
                    .List())
                    .Where(sso =>
                        sso.LoginType == LoginTypeEnum.SsoMicrosoft
                        && (!sso.Settings.ContainsKey(MicrosoftSso.Parameters.Domain)
                            || string.IsNullOrEmpty(sso.Settings[MicrosoftSso.Parameters.Domain])
                            || sso.Settings[MicrosoftSso.Parameters.Domain] == userDomain)
                        && (!sso.Settings.ContainsKey(MicrosoftSso.Parameters.Group)
                            || string.IsNullOrWhiteSpace(sso.Settings[MicrosoftSso.Parameters.Group])
                            || userGroups.Contains(sso.Settings[MicrosoftSso.Parameters.Group])))
                    .ToList();

                // If the user is not in the allowed domain & group, return null
                if (clientSsoList.Count == 0)
                    return "Error: User not in allowed domain or group. Or Add Registration permissions for domain/group are not set.";

                // Get all attached groups
                var groups = clientSsoList.SelectMany(e => e.Groups).DistinctBy(e => e.GroupId).ToList();
                var autoAdmin = clientSsoList.Any(sso => sso.Settings.ContainsKey(MicrosoftSso.Parameters.AutoAdmin) && sso.Settings[MicrosoftSso.Parameters.AutoAdmin] == "True");

                var dailyTokenLimitSetting = clientSsoList.FirstOrDefault(sso => sso.Settings.ContainsKey(MicrosoftSso.Parameters.DailyTokenLimit))?.Settings[MicrosoftSso.Parameters.DailyTokenLimit];
                if (!string.IsNullOrEmpty(dailyTokenLimitSetting) && int.TryParse(dailyTokenLimitSetting, out var parsedDailyTokenLimit))
                {
                    dailyTokenLimit = parsedDailyTokenLimit;
                }

                login = await _loginProcessor.Add(new LoginModel
                {
                    Login = extendedTokenModel.Email,
                    Email = extendedTokenModel.Email,
                    FullName = extendedTokenModel.Name,
                    Role = autoAdmin ? RoleEnum.Admin : RoleEnum.User,
                    LoginType = LoginTypeEnum.SsoMicrosoft,
                    IsEnabled = true,
                    Created = DateTime.UtcNow,
                    CreatedBy = "system",
                    TokensLimit = dailyTokenLimit,
                    Groups = groups
                });
            }

            await SyncRbacUserGroups(extendedTokenModel.Email, userGroups);
            await SyncRbacUserRoles(extendedTokenModel);

            if (!login.IsEnabled)
                return "Error: Login is disabled";

            var loginHistory = new LoginHistoryModel
            {
                LoginId = login.LoginId,
                Login = login.Login,
                Code = Guid.NewGuid().ToString(),
                IsOffline = isOfflineMode,
                CodeChallenge = loginProcessViewModel.CodeChallenge,
                ValidUntilTime = loginProcessViewModel.IsPermanentToken
                    ? DateTime.UtcNow.AddDays(_config.PermanentTokenExpirationTimeDays)
                    : DateTime.UtcNow.AddMinutes(_config.TokenExpirationTimeMinutes)
            };
            _loginHistoryProcessor.Add(loginHistory);
            return loginHistory.Code;
        }

        private async Task SyncRbacUserGroups(string email, List<string> userRbacGroups)
        {
            // Sync RBAC for Microsoft SSO users only
            var login = await _loginProcessor.GetByLogin(email, LoginTypeEnum.SsoMicrosoft);
            if (login == null)
                return;

            userRbacGroups = userRbacGroups.Select(e => e.ToLower()).ToList();
            var usersDomain = email.Split('@')[1].ToLower();
            var rbacGroupSyncList = await _rbacGroupSyncProcessor.ListAsync();

            foreach (var rbacGroupSync in rbacGroupSyncList)
            {
                // RbacGroupName format: domain\group (i.e. viacode.com\Admins), domain is optional
                var rbacGroupNameParts = rbacGroupSync.RbacGroupName.ToLower().Split('\\');
                var rbacGroupName = rbacGroupNameParts.Length > 1 ? rbacGroupNameParts[1] : rbacGroupNameParts[0];
                var rbacDomain = rbacGroupNameParts.Length > 1 ? rbacGroupNameParts[0] : string.Empty;
                if (!string.IsNullOrEmpty(rbacDomain) && rbacDomain != usersDomain)
                    continue;

                var aiUserGroup = login.Groups.FirstOrDefault(group => group.Name.ToLower() == rbacGroupSync.AiCoreGroupName.ToLower());
                if (aiUserGroup == null)
                {
                    // Add missing groups
                    if (!userRbacGroups.Contains(rbacGroupName))
                        continue;
                    var group = await _groupsProcessor.Get(rbacGroupSync.AiCoreGroupName);
                    if (group == null)
                        continue;
                    login.Groups.Add(group);
                }
                else
                {
                    // Remove excluded groups
                    if (userRbacGroups.Contains(rbacGroupName))
                        continue;
                    login.Groups.Remove(aiUserGroup);
                }
            }
            await _loginProcessor.Update(login);
        }

        private async Task SyncRbacUserRoles(ExtendedTokenModel extendedTokenModel)
        {
            // Sync RBAC for Microsoft SSO users only
            var login = await _loginProcessor.GetByLogin(extendedTokenModel.Email, LoginTypeEnum.SsoMicrosoft);
            if (login == null)
                return;

            var usersDomain = extendedTokenModel.Email.Split('@')[1].ToLower();
            var rbacRoleSyncList = await _rbacRoleSyncProcessor.ListAsync();

            foreach (var rbacRoleSync in rbacRoleSyncList)
            {
                // RbacRoleName format: domain\role (i.e. viacode.com\Admin), domain is optional
                var rbacRoleNameParts = rbacRoleSync.RbacRoleName.ToLower().Split('\\');
                var rbacRoleName = rbacRoleNameParts.Length > 1 ? rbacRoleNameParts[1] : rbacRoleNameParts[0];
                var rbacDomain = rbacRoleNameParts.Length > 1 ? rbacRoleNameParts[0] : string.Empty;
                if (!string.IsNullOrEmpty(rbacDomain) && rbacDomain != usersDomain)
                    continue;

                var roleUsers = await _microsoftSso.GetRoleUsers(rbacRoleName, extendedTokenModel);
                if (roleUsers.Contains(extendedTokenModel.Email.ToLower()))
                {
                    var newTags = rbacRoleSync.Tags
                        .Where(e => !login.Tags.Select(t => t.TagId).Contains(e.TagId))
                        .ToList();
                    login.Tags.AddRange(newTags);
                }
                else
                {
                    var tagsToRemove = login.Tags
                        .Where(e => rbacRoleSync.Tags.Select(t => t.TagId).Contains(e.TagId))
                        .ToList();
                    login.Tags = login.Tags.Where(e => !tagsToRemove.Select(t => t.TagId).Contains(e.TagId))
                        .ToList();
                }
            }
            await _loginProcessor.Update(login);
        }

        public async Task<TokenModel?> GetByCode(string code, string codeVerifier)
        {
            var loginHistory = _loginHistoryProcessor.GetByCode(code);
            if (loginHistory == null)
                return null;

            if (loginHistory.CodeChallenge != Pkce.GenerateCodeChallenge(codeVerifier))
                return null;

            return await GetTokenModel(loginHistory);
        }

        public async Task<TokenModel?> GetByRefreshToken(string refreshToken)
        {
            var loginHistory = _loginHistoryProcessor.GetByRefreshToken(refreshToken);
            if (loginHistory == null)
                return null;
            return await GetTokenModel(loginHistory);
        }

        private static string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private async Task<TokenModel> GetTokenModel(LoginHistoryModel loginHistory)
        {
            if (string.IsNullOrEmpty(loginHistory.RefreshToken))
            {
                var newRefreshToken = loginHistory.IsOffline ? GenerateRefreshToken() : null;
                loginHistory.RefreshToken = newRefreshToken;
            }
            loginHistory.Code = null;
            loginHistory.CodeChallenge = null;
            var isPermanentToken = loginHistory.ValidUntilTime > DateTime.UtcNow.AddMinutes(_config.TokenExpirationTimeMinutes);
            loginHistory.ValidUntilTime = isPermanentToken
                ? DateTime.UtcNow.AddDays(_config.PermanentTokenExpirationTimeDays)
                : DateTime.UtcNow.AddMinutes(_config.TokenExpirationTimeMinutes);
            _loginHistoryProcessor.Update(loginHistory);

            var login = await _loginProcessor.GetById(loginHistory.LoginId);

            var now = DateTime.UtcNow;
            var accessToken = CreateAccessToken(login, now);
            var idToken = CreateIdToken(login, now, loginHistory, accessToken);

            if (_monitoringConfig.LogLoginLogout)
                _logger.LogCritical("[{DateTime}][User Login] Login: {Login}, Session id: {sessionId}", DateTime.UtcNow.ToString("g"), loginHistory.Login, loginHistory.LoginHistoryId);

            return new TokenModel
            {
                AccessToken = accessToken,
                RefreshToken = loginHistory.RefreshToken,
                ExpiresIn = _config.TokenExpirationTimeMinutes * 60,
                TokenType = "Bearer",
                Scope = "openid" + (loginHistory.IsOffline ? " offline_access" : string.Empty),
                IdToken = idToken
            };
        }

        private string CreateAccessToken(LoginModel login, DateTime now)
        {
            var accessTokenClaims = new List<Claim>
            {
                new(AccessTokenClaims.FullName, login.FullName),
                new(AccessTokenClaims.Role, login.Role.ToString()),
                new(AccessTokenClaims.LoginType, login.LoginType.ToString()),
                new(AccessTokenClaims.Sub, login.Login),
            };
            var jwtAccessToken = new JwtSecurityToken(
                issuer: _config.AuthIssuer,
                audience: _config.AuthAudience,
                claims: accessTokenClaims,
                notBefore: now,
                expires: now.Add(TimeSpan.FromMinutes(_config.TokenExpirationTimeMinutes)),
                signingCredentials: new SigningCredentials(_config.AuthSecurityKey.GetSymmetricSecurityKey(), SecurityAlgorithms.HmacSha256));

            var accessToken = new JwtSecurityTokenHandler().WriteToken(jwtAccessToken);
            return accessToken;
        }

        public async Task<LoginModel?> CheckAccessToken(string accessToken)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _config.AuthIssuer,
                ValidateAudience = true,
                ValidAudience = _config.AuthAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _config.AuthSecurityKey.GetSymmetricSecurityKey(),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };         
            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = await tokenHandler.ValidateTokenAsync(accessToken, tokenValidationParameters);
            if (!principal.IsValid)
                return null;

            var loginName = ((JwtSecurityToken)principal.SecurityToken).Subject;
            var loginTypeString = principal.Claims[AccessTokenClaims.LoginType].ToString();
            if (loginName == null || loginTypeString == null || !Enum.TryParse(loginTypeString, out LoginTypeEnum loginType))
                return null;
            var login = await _loginProcessor.GetByLogin(loginName, loginType);

            if (_monitoringConfig.LogAccessTokenCheck)
                _logger.LogCritical("[{DateTime}]Access Token Check] Login: {Login}", DateTime.UtcNow.ToString("g"), loginName);
            return login;
        }

        private string CreateIdToken(LoginModel login, DateTime now, LoginHistoryModel loginHistory, string accessToken)
        {
            var authTime = new DateTimeOffset(loginHistory.Created).ToUnixTimeSeconds();
            var idTokenClaims = new List<Claim>
            {
                new(IdTokenClaims.AtHash, accessToken.Sha256()),
                new(IdTokenClaims.SessionId, loginHistory.LoginHistoryId.ToString()),
                new(IdTokenClaims.AuthTime, authTime.ToString(), ClaimValueTypes.Integer),
                new(IdTokenClaims.Iat, authTime.ToString(), ClaimValueTypes.Integer),
                new(IdTokenClaims.Role, login.Role.ToString()),
                new(IdTokenClaims.LoginType, login.LoginType.ToString()),
                new(IdTokenClaims.Sub, login.Login),
                new(IdTokenClaims.FullName, login.FullName),
            };
            var idTokenAccessToken = new JwtSecurityToken(
                issuer: _config.AuthIssuer,
                audience: _config.AuthAudience,
                claims: idTokenClaims,
                notBefore: now,
                expires: now.Add(TimeSpan.FromMinutes(_config.TokenExpirationTimeMinutes)),
                signingCredentials: new SigningCredentials(_config.AuthSecurityKey.GetSymmetricSecurityKey(), SecurityAlgorithms.HmacSha256));

            var idToken = new JwtSecurityTokenHandler().WriteToken(idTokenAccessToken);
            return idToken;
        }

        public void Logout(string idToken)
        {
            var jsonToken = new JwtSecurityTokenHandler().ReadToken(idToken) as JwtSecurityToken;
            var sessionId = Convert.ToInt32(jsonToken?.Payload[IdTokenClaims.SessionId].ToString());
            var loginHistory = _loginHistoryProcessor.GetBySessionId(sessionId);
            if (loginHistory == null)
                return;
            loginHistory.ValidUntilTime = DateTime.UtcNow;
            _loginHistoryProcessor.Update(loginHistory);
            if(_monitoringConfig.LogLoginLogout)
                _logger.LogCritical("[{DateTime}][User Logout] Login: {Login}, Session id: {sessionId}", DateTime.UtcNow.ToString("g"), loginHistory.Login, sessionId);
        }
    }

    public interface IConnectService
    {
        Task<string?> GetCodeByCredentials(string loginName, string password, bool isOfflineMode, string codeChallenge, bool isPermanentToken);
        Task<string> GetCodeBySsoId(ExtendedTokenModel extendedTokenModel, bool isOfflineMode, LoginProcessViewModel loginProcessViewModel);
        Task<TokenModel?> GetByCode(string code, string codeVerifier);
        Task<TokenModel?> GetByRefreshToken(string refreshToken);
        Task<LoginModel?> CheckAccessToken(string accessToken);
        void Logout(string idToken);
    }
}
