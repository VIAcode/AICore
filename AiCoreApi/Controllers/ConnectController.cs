﻿using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.SsoSources;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/v1/connect")]
    public class ConnectController : ControllerBase
    {
        private readonly IConnectService _connectService;
        private readonly ExtendedConfig _config;
        private readonly IMicrosoftSso _microsoftSso;
        private readonly IDistributedCache _distributedCache;
        private const int SsoSessionTimeoutMinutes = 5;
        private const string PermanentAcrValue = "permanent";

        public ConnectController(
            IConnectService connectService,
            ExtendedConfig config,
            IMicrosoftSso microsoftSso,
            IDistributedCache distributedCache)           
        {
            _connectService = connectService;
            _config = config;
            _microsoftSso = microsoftSso;
            _distributedCache = distributedCache;
    }

        [HttpGet("authorize")]
        public async Task<IActionResult> LoginGet(
            [FromQuery(Name = "client_id")] string clientId,
            [FromQuery(Name = "redirect_uri")] string redirectUri,
            [FromQuery(Name = "code_challenge")] string codeChallenge,
            [FromQuery(Name = "code_challenge_method")]
            string codeChallengeMethod,
            [FromQuery(Name = "acr_values")] string acrValues,
            [FromQuery(Name = "scope")] string scope,
            [FromQuery(Name = "response_type")] string responseType,
            [FromQuery(Name = "state")] string? state)
        {
            return await LoginPost(clientId, redirectUri, codeChallenge, codeChallengeMethod, acrValues, scope, responseType, state);
        }

        [HttpPost("authorize")]
        public async Task<IActionResult> LoginPost(
            [FromForm(Name = "client_id")] string clientId,
            [FromForm(Name = "redirect_uri")] string redirectUri,
            [FromForm(Name = "code_challenge")] string codeChallenge,
            [FromForm(Name = "code_challenge_method")] string codeChallengeMethod,
            [FromForm(Name = "acr_values")] string acrValues,
            [FromForm(Name = "scope")] string scope,
            [FromForm(Name = "response_type")] string responseType,
            [FromForm(Name = "state")] string? state)
        {
            if (clientId != _config.AuthIssuer)
                return BadRequest("Invalid client_id");
            if (string.IsNullOrEmpty(redirectUri))
                return BadRequest("Invalid redirect_uri");
            if (string.IsNullOrEmpty(codeChallenge))
                return BadRequest("Invalid code_challenge");
            if (codeChallengeMethod != "S256")
                return BadRequest("Invalid code_challenge_method, S256 is the only supported");
            if (string.IsNullOrEmpty(acrValues))
                return BadRequest("Invalid acr_values");
            if (string.IsNullOrEmpty(scope))
                return BadRequest("Invalid scope, at least openid is mandatory");
            if (responseType != "code")
                return BadRequest("Invalid response_type, code is the only supported");
            
            // Microsoft SSO, save the state and redirect to Microsoft login
            var acrValuesArray = acrValues.Split(' ');
            var isPermanentToken = acrValuesArray.Contains(PermanentAcrValue);
            if (isPermanentToken)
                acrValuesArray = acrValuesArray.Where(x => x != PermanentAcrValue).ToArray();

            if (acrValuesArray.Contains(MicrosoftSso.AcrValueConst))
            {
                var loginProcessViewModel = new LoginProcessViewModel
                {
                    ClientId = clientId,
                    RedirectUri = redirectUri,
                    CodeChallenge = codeChallenge,
                    CodeChallengeMethod = codeChallengeMethod,
                    AcrValues = acrValuesArray.ToList(),
                    Scope = scope,
                    ResponseType = responseType,
                    State = state,
                    IsPermanentToken = isPermanentToken,
                };
                var loginProcessState = Guid.NewGuid().ToString();
                _distributedCache.SetString(loginProcessState, loginProcessViewModel.ToJson(), new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(SsoSessionTimeoutMinutes)
                });
                return Redirect($"{_microsoftSso.GetLoginRedirectUrl(loginProcessState)}");
            }

            if(acrValuesArray.Length != 1 || acrValuesArray[0].Contains(":") == false)
                return BadRequest("Invalid acr_values");
            // Custom login (username/password)
            var acrLoginPassword = acrValuesArray[0].Split(':');
            var loginName = acrLoginPassword[0];
            var password = acrLoginPassword[1];
            
            var scopes = scope.Split(' ', '+');
            var isOfflineMode = scopes.Contains("offline_access");
            var code = await _connectService.GetCodeByCredentials(loginName, password, isOfflineMode, codeChallenge, isPermanentToken);
            if (string.IsNullOrEmpty(code))
                return Unauthorized();

            if(redirectUri.Contains("?"))
                redirectUri += $"&code={code}";
            else
                redirectUri += $"?code={code}";
            if (!string.IsNullOrEmpty(state))
                redirectUri += $"&state={state}";
            return Redirect(redirectUri);
        }

        [HttpPost("token")]
        public async Task<IActionResult> Token([FromForm(Name = "grant_type")] string grantType)
        {
            if (grantType == "refresh_token")
            {
                var refreshToken = Request.Form["refresh_token"].ToString();
                var clientId = Request.Form["client_id"].ToString();
                if (string.IsNullOrEmpty(refreshToken))
                    return BadRequest("Invalid refresh_token");
                if (clientId != _config.AuthIssuer)
                    return BadRequest("Invalid client_id");

                var tokenModel = await _connectService.GetByRefreshToken(refreshToken);
                if (tokenModel != null)
                    return Ok(tokenModel);
            }

            if (grantType == "authorization_code")
            {
                var code = Request.Form["code"].ToString();
                var clientId = Request.Form["client_id"].ToString();
                var redirectUri = Request.Form["redirect_uri"].ToString();
                var codeVerifier = Request.Form["code_verifier"].ToString();
                if (string.IsNullOrEmpty(code))
                    return BadRequest("Invalid code");
                if (clientId != _config.AuthIssuer)
                    return BadRequest("Invalid client_id");
                if (string.IsNullOrEmpty(redirectUri))
                    return BadRequest("Invalid redirect_uri");
                if (string.IsNullOrEmpty(codeVerifier))
                    return BadRequest("Invalid code_verifier");

                var tokenModel = await _connectService.GetByCode(code, codeVerifier);
                if (tokenModel != null)
                    return Ok(tokenModel);
            }
            return Unauthorized();
        }

        [HttpGet("logout")]
        public IActionResult Logout([FromQuery(Name = "id_token_hint")] string idToken, [FromQuery(Name = "post_logout_redirect_uri")] string postLogoutRedirectUri = "")
        {
            _connectService.Logout(idToken);
            if (string.IsNullOrEmpty(postLogoutRedirectUri))
                return Ok();
            return Redirect(postLogoutRedirectUri);
            
        }

        [HttpGet(".well-known/openid-configuration")]
        public IActionResult WellKnownOpenIdConfiguration()
        {
            var host = _config.AppUrl;
            return Ok(new
            {
                issuer = _config.AuthIssuer,
                authorization_endpoint = $"{host}/connect/authorize",
                end_session_endpoint = $"{host}/connect/logout",
                token_endpoint = $"{host}/connect/token",
                jwks_uri = $"{host}/connect/jwks",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[] { "openid", "offline_access" },
                acr_values_supported = new[] { MicrosoftSso.AcrValueConst, PermanentAcrValue },
            });
        }

        // Microsoft SSO callback
        [HttpPost("callback")]
        public async Task<IActionResult> Callback([FromForm(Name = "code")] string? code, [FromForm(Name = "state")] string? state, [FromForm(Name = "error_description")] string? errorDescription)
        {
            // Get the state from cache (created in "authorize")
            var loginProcessStateString = await _distributedCache.GetStringAsync(state);
            if (!string.IsNullOrEmpty(errorDescription))
                return BadRequest(errorDescription);
            if (string.IsNullOrEmpty(code))
                return BadRequest("Code is missing");
            if (string.IsNullOrEmpty(loginProcessStateString))
                return BadRequest("Invalid state");
            await _distributedCache.RemoveAsync(state);
            var loginProcessViewModel = loginProcessStateString.JsonGet<LoginProcessViewModel>();

            var accessToken = await _microsoftSso.GetAccessTokenByCodeAsync(code);

            var scopes = loginProcessViewModel.Scope.Split(' ', '+');
            var isOfflineMode = scopes.Contains("offline_access");
            var aiCoreCode = await _connectService.GetCodeBySsoId(accessToken, isOfflineMode, loginProcessViewModel);
            if (string.IsNullOrEmpty(aiCoreCode))
                return Unauthorized();

            if (loginProcessViewModel.RedirectUri.Contains("?"))
                loginProcessViewModel.RedirectUri += $"&code={aiCoreCode}";
            else
                loginProcessViewModel.RedirectUri += $"?code={aiCoreCode}";
            if (!string.IsNullOrEmpty(state))
                loginProcessViewModel.RedirectUri += $"&state={loginProcessViewModel.State}";
            return Redirect(loginProcessViewModel.RedirectUri);
        }
    }
}
