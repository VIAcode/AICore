using AiCoreApi.Authorization;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using Microsoft.SemanticKernel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OAuthTokenAgent : BaseAgent, IOAuthTokenAgent
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILoginProcessor _loginProcessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ICacheAccessor _cacheAccessor;
        private readonly IDebugLogProcessor _debugLogProcessor;

        private string _debug = "OAuthTokenAgent";
        private const int PkceStateTtlMinutes = 15;

        private static class Parameters
        {
            public const string ConnectionName = "connectionName";
            public const string Action = "action";

            public const string IssuerUrl = "issuerUrl";
            public const string AuthorizeUrl = "authorizeUrl";
            public const string TokenUrl = "tokenUrl";
            public const string ClientId = "clientId";
            public const string ClientSecret = "clientSecret";
            public const string RedirectUri = "redirectUri";
            public const string Scope = "scope";
            public const string Audience = "audience";
            public const string RunAgentOnFinish = "runAgentOnFinish";

            public const string AcrValues = "acrValues";
            public const string RefreshToken = "refreshToken";
        }

        private static class AuthType
        {
            public const string PkceFlow = "fullPkceFlow";
            public const string Refresh = "refreshAccessToken";
            public const string BuildAuthorizationUrl = "buildAuthorizationUrl";
        }

        public OAuthTokenAgent(
            IServiceScopeFactory scopeFactory,
            ILoginProcessor loginProcessor,
            ExtendedConfig extendedConfig,
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ICacheAccessor cacheAccessor,
            ILogger<OAuthTokenAgent> logger,
            IDebugLogProcessor debugLogProcessor)
            : base(baseAgentHelper, logger)
        {
            _scopeFactory = scopeFactory;
            _loginProcessor = loginProcessor;
            _extendedConfig = extendedConfig;
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _cacheAccessor = cacheAccessor;
            _debugLogProcessor = debugLogProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debug = $"{agent.Name} ({agent.Type})";

            var action = agent.Content[Parameters.Action].Value;

            var runAgentOnFinish = await GetParameterValueAsync(Parameters.RunAgentOnFinish);
            var connectionName = await GetParameterValueAsync(Parameters.ConnectionName);

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var c = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.OAuth, _debug, connectionName: connectionName);

            string get(string key) => c.Content.GetValueOrDefault(key, "");
            var issuerUrl = get(Parameters.IssuerUrl);
            var authorizeUrl = get(Parameters.AuthorizeUrl);
            var tokenUrl = get(Parameters.TokenUrl);
            var clientId = get(Parameters.ClientId);
            var clientSecret = get(Parameters.ClientSecret);
            var redirectUri = get(Parameters.RedirectUri);
            var scope = get(Parameters.Scope);
            var audience = get(Parameters.Audience);

            // Try to discover endpoints from issuer URL if not explicitly provided
            if (!string.IsNullOrWhiteSpace(issuerUrl) && (string.IsNullOrWhiteSpace(authorizeUrl) || string.IsNullOrWhiteSpace(tokenUrl)))
            {
                var discoveryConfig = await GetConfigurationAsync(issuerUrl);
                if (discoveryConfig != null)
                {
                    if (string.IsNullOrWhiteSpace(authorizeUrl) && !string.IsNullOrWhiteSpace(discoveryConfig.AuthorizationEndpoint))
                        authorizeUrl = discoveryConfig.AuthorizationEndpoint;
                    
                    if (string.IsNullOrWhiteSpace(tokenUrl) && !string.IsNullOrWhiteSpace(discoveryConfig.TokenEndpoint))
                        tokenUrl = discoveryConfig.TokenEndpoint;
                    
                    _responseAccessor.AddDebugMessage(_debug, "OpenID Discovery", 
                        $"Discovered endpoints from {issuerUrl}: auth={authorizeUrl}, token={tokenUrl}");
                }
            }

            _responseAccessor.AddDebugMessage(_debug, "OAuth Request",
                $"Action={action}, authUrl={authorizeUrl}, tokenUrl={tokenUrl}, redirect={redirectUri}");

            return action switch
            {
                AuthType.PkceFlow => await PkceFlow(authorizeUrl, tokenUrl, clientId, clientSecret, redirectUri, scope, runAgentOnFinish, false),
                AuthType.BuildAuthorizationUrl => await PkceFlow(authorizeUrl, tokenUrl, clientId, clientSecret, redirectUri, scope, runAgentOnFinish, true),
                AuthType.Refresh => await RefreshAccessToken(tokenUrl, clientId, clientSecret, scope, audience),
                _ => throw new ArgumentException($"Unsupported action: {action}")
            };
        }

        private HttpClient GetHttpClient()
        {
            var httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                Proxy = string.IsNullOrEmpty(_extendedConfig.Proxy)
                    ? null
                    : new WebProxy(new Uri(_extendedConfig.Proxy)),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            return new HttpClient(httpClientHandler);
        }

        private string GetCacheKey(string state) => $"oauth_pkce_state|{state}";

        private async Task<string> PkceFlow(string authorizeUrl, string tokenUrl, string clientId, string clientSecret, string redirectUri, string scope, string runAgentOnFinish, bool returnUrl)
        {
            var acrValues = await GetParameterValueAsync(Parameters.AcrValues);

            var verifier = Pkce.GenerateCodeVerifier();
            var challenge = Pkce.GenerateCodeChallenge(verifier);
            var state = Guid.NewGuid().ToString("N");
            var cacheKey = GetCacheKey(state);

            using var http = GetHttpClient();
            var authorizeForm = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["scope"] = scope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            };
            if (!string.IsNullOrEmpty(acrValues))
            {
                authorizeForm["acr_values"] = acrValues;
            }

            _responseAccessor.AddDebugMessage(_debug, "PKCE Authorize Request", JsonSerializer.Serialize(authorizeForm));

            using var authResp = await http.PostAsync(authorizeUrl, new FormUrlEncodedContent(authorizeForm));
            var authContent = await authResp.Content.ReadAsStringAsync();

            if (!authResp.IsSuccessStatusCode && authResp.StatusCode != HttpStatusCode.Found)
                throw new AiCoreUiException($"Authorization failed: {authResp.StatusCode} - {authContent}");

            string? code = null;
            var loginId = await _requestAccessor.UserContext.GetLoginIdAsync();
            var pkceCacheModel = new PkceCacheModel
            {
                LoginId = loginId ?? 0,
                Login = _requestAccessor.Login ?? "",
                Verifier = verifier,
                RunAgentOnFinish = runAgentOnFinish,
                WorkspaceId = _requestAccessor.WorkspaceId ?? 0,
                ClientId = clientId,
                ClientSecret = clientSecret,
                RedirectUri = redirectUri,
                TokenUrl = tokenUrl
            };

            if (authResp.StatusCode == HttpStatusCode.Found && authResp.Headers.Location != null)
            {
                var locationUrl = authResp.Headers.Location.ToString();
                if (returnUrl)
                {
                    _cacheAccessor.SetCacheValue(cacheKey, JsonSerializer.Serialize(pkceCacheModel), PkceStateTtlMinutes * 60 * 60);
                    _responseAccessor.AddDebugMessage(_debug, "ResponseRedirect", locationUrl);
                    return JsonSerializer.Serialize(new { uri = locationUrl },
                        new JsonSerializerOptions { WriteIndented = true });
                }

                _responseAccessor.AddDebugMessage(_debug, "PKCE Location Header", locationUrl);
                var match = System.Text.RegularExpressions.Regex.Match(locationUrl, @"[?&]code=([A-Za-z0-9_\-\.]+)");
                if (match.Success)
                    code = match.Groups[1].Value;
            }

            // Fallback 1: parse JSON from answer
            if (string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(authContent))
            {
                try
                {
                    using var doc = JsonDocument.Parse(authContent);
                    if (doc.RootElement.TryGetProperty("code", out var codeEl))
                        code = codeEl.GetString();
                }
                catch
                {
                    // Fallback 2: parse text
                    var match = System.Text.RegularExpressions.Regex.Match(authContent, @"code=([A-Za-z0-9_\-\.]+)");
                    if (match.Success)
                        code = match.Groups[1].Value;
                }
            }

            if (string.IsNullOrWhiteSpace(code))
                throw new AiCoreUiException($"Authorization code not found in response: {authContent}");

            return await GetTokensByCode(code, pkceCacheModel);
        }

        private async Task<string> GetTokensByCode(string code, PkceCacheModel pkceCacheModel)
        {
            using var http = GetHttpClient();
            var tokenForm = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = pkceCacheModel.ClientId,
                ["redirect_uri"] = pkceCacheModel.RedirectUri,
                ["code"] = code,
                ["code_verifier"] = pkceCacheModel.Verifier
            };
            if (!string.IsNullOrEmpty(pkceCacheModel.ClientSecret))
                tokenForm["client_secret"] = pkceCacheModel.ClientSecret;

            using var tokenResp = await http.PostAsync(pkceCacheModel.TokenUrl, new FormUrlEncodedContent(tokenForm));
            var tokenJson = await tokenResp.Content.ReadAsStringAsync();

            if (!tokenResp.IsSuccessStatusCode)
                throw new AiCoreUiException($"Token exchange failed: {tokenResp.StatusCode} - {tokenJson}");

            try
            {
                using var doc = JsonDocument.Parse(tokenJson);
                var resultDictionary = new Dictionary<string, object?>();

                foreach (var p in doc.RootElement.EnumerateObject())
                    resultDictionary[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number => p.Value.GetDouble(),
                        _ => p.Value.ToString()
                    };

                if (resultDictionary.TryGetValue("access_token", out var at) && at is string tokenStr)
                {
                    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                    if (handler.CanReadToken(tokenStr))
                    {
                        var jwt = handler.ReadJwtToken(tokenStr);
                        resultDictionary["decoded"] = jwt.Payload;
                    }
                }

                var result = JsonSerializer.Serialize(resultDictionary, new JsonSerializerOptions { WriteIndented = true });
                _responseAccessor.AddDebugMessage(_debug, "Response", result);
                return result;
            }
            catch
            {
                _responseAccessor.AddDebugMessage(_debug, "Error", tokenJson);
                return tokenJson;
            }
        }

        private async Task<string> RefreshAccessToken(string tokenUrl, string clientId, string clientSecret, string scope, string audience)
        {
            var refreshToken = await GetParameterValueAsync(Parameters.RefreshToken);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            if (!string.IsNullOrWhiteSpace(scope)) form["scope"] = scope;
            if (!string.IsNullOrWhiteSpace(audience)) form["audience"] = audience;

            return await SendTokenRequest(tokenUrl, form, "Refresh Token");
        }

        private async Task<string> SendTokenRequest(string tokenUrl, Dictionary<string, string> form, string label)
        {
            using var http = GetHttpClient();
            using var resp = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
            var json = await resp.Content.ReadAsStringAsync();

            var safeForm = new Dictionary<string, string>(form);
            if (safeForm.ContainsKey("client_secret")) safeForm["client_secret"] = "***";

            _responseAccessor.AddDebugMessage(_debug, $"{label} Request", JsonSerializer.Serialize(safeForm));
            _responseAccessor.AddDebugMessage(_debug, $"{label} Response", json);

            if (!resp.IsSuccessStatusCode)
                throw new AiCoreUiException($"OAuth request failed ({resp.StatusCode}): {json}");

            return json;
        }

        private async Task<OidcDiscoveryConfig?> GetConfigurationAsync(string issuerUrl)
        {
            if (string.IsNullOrWhiteSpace(issuerUrl))
                return null;

            issuerUrl = issuerUrl.TrimEnd('/');
            var wellKnownUrl = $"{issuerUrl}/.well-known/openid-configuration";

            _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery", $"Fetching {wellKnownUrl}");

            try
            {
                using var http = GetHttpClient();
                using var resp = await http.GetAsync(wellKnownUrl);
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery Error", $"Failed with {resp.StatusCode}: {json}");
                    return null;
                }
                _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery Raw", json);
                var result = JsonSerializer.Deserialize<OidcDiscoveryConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (result == null)
                {
                    _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery", "Failed to parse discovery JSON.");
                    return null;
                }
                _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery Parsed", $"issuer={result.Issuer}, auth={result.AuthorizationEndpoint}, token={result.TokenEndpoint}, end_session={result.EndSessionEndpoint}");
                return result;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debug, "OIDC Discovery Exception", ex.Message);
                return null;
            }
        }

        public async Task<string> ProcessCallBack(string code, string state)
        {
            var result = "";
            var currentMessage = new MessageDialogViewModel.Message();
            var cacheValue = _cacheAccessor.GetCacheValue(GetCacheKey(state));
            if (string.IsNullOrEmpty(cacheValue))
                throw new AiCoreUiException("PKCE state not found or expired.");
            var pkceCacheModel = JsonSerializer.Deserialize<PkceCacheModel>(cacheValue);
            if (pkceCacheModel == null)
                throw new AiCoreUiException("Invalid PKCE cache data.");
            try
            {
                var tokens = await GetTokensByCode(code, pkceCacheModel);
                if (!string.IsNullOrEmpty(pkceCacheModel.RunAgentOnFinish))
                {
                    var runAsUser = await _loginProcessor.GetById(pkceCacheModel.LoginId);
                    if (runAsUser == null)
                        return "User not found";

                    await using (var scope = _scopeFactory.CreateAsyncScope())
                    {
                        var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
                        var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                        var agentExecutor = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
                        requestAccessor.MessageDialog = new MessageDialogViewModel
                        {
                            Messages = new List<MessageDialogViewModel.Message>
                            {
                                new()
                                {
                                    Text = $"{_debug} task",
                                    Sender = _debug,
                                }
                            }
                        };
                        requestAccessor.Login = runAsUser.Login;
                        requestAccessor.LoginTypeString = runAsUser.LoginType.ToString();
                        requestAccessor.TagsString = string.Join(",", runAsUser.Tags.Select(tag => tag.TagId));
                        requestAccessor.WorkspaceId = pkceCacheModel.WorkspaceId;
                        if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                        {
                            requestAccessor.UseDebug = true;
                        }

                        userContextAccessor.SetLoginId(pkceCacheModel.LoginId);
                        UserContextAccessor.AsyncScheduledLoginId.Value = pkceCacheModel.LoginId;

                        result = await agentExecutor.ExecuteAsync(pkceCacheModel.RunAgentOnFinish,
                            new List<string> { pkceCacheModel.Login, tokens });
                    }
                    return result;
                }
                return tokens;
            }
            finally
            {
                if (_extendedConfig.AllowDebugMode && _extendedConfig.DebugMessagesStorageEnabled)
                {
                    await _debugLogProcessor.Add(
                        pkceCacheModel.Login,
                        $"Agent ({_debug}): {pkceCacheModel.RunAgentOnFinish}{Environment.NewLine}Parameters:{Environment.NewLine}{cacheValue}",
                        new MessageDialogViewModel
                        {
                            Messages = new List<MessageDialogViewModel.Message>
                            {
                                new()
                                {
                                    Text = result,
                                    SpentTokens = currentMessage.SpentTokens,
                                    DebugMessages = currentMessage.DebugMessages
                                }
                            }
                        }, pkceCacheModel.WorkspaceId);
                }
            }
        }

        private class OidcDiscoveryConfig
        {
            [JsonPropertyName("issuer")]
            public string? Issuer { get; set; }

            [JsonPropertyName("authorization_endpoint")]
            public string? AuthorizationEndpoint { get; set; }

            [JsonPropertyName("token_endpoint")]
            public string? TokenEndpoint { get; set; }

            [JsonPropertyName("end_session_endpoint")]
            public string? EndSessionEndpoint { get; set; }

            [JsonPropertyName("jwks_uri")]
            public string? JwksUri { get; set; }

            [JsonPropertyName("response_types_supported")]
            public string[]? ResponseTypesSupported { get; set; }

            [JsonPropertyName("grant_types_supported")]
            public string[]? GrantTypesSupported { get; set; }

            [JsonPropertyName("code_challenge_methods_supported")]
            public string[]? CodeChallengeMethodsSupported { get; set; }

            [JsonPropertyName("scopes_supported")]
            public string[]? ScopesSupported { get; set; }

            [JsonPropertyName("acr_values_supported")]
            public string[]? AcrValuesSupported { get; set; }
        }

        public class PkceCacheModel
        {
            [JsonPropertyName("login")]
            public string Login { get; set; } = "";

            [JsonPropertyName("loginId")]
            public int LoginId { get; set; } = 0;

            [JsonPropertyName("verifier")]
            public string Verifier { get; set; } = "";

            [JsonPropertyName("runAgentOnFinish")]
            public string RunAgentOnFinish { get; set; } = "";

            [JsonPropertyName("workspaceId")]
            public int WorkspaceId { get; set; } = 0;

            [JsonPropertyName("clientId")]
            public string ClientId { get; set; } = "";

            [JsonPropertyName("clientSecret")]
            public string ClientSecret { get; set; } = "";

            [JsonPropertyName("redirectUri")]
            public string RedirectUri { get; set; } = "";

            [JsonPropertyName("tokenUrl")]
            public string TokenUrl { get; set; } = "";
        }
    }

    public interface IOAuthTokenAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> ProcessCallBack(string code, string state);
    }
}
