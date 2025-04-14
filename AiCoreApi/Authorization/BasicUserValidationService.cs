using System.Security.Claims;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AspNetCore.Authentication.Basic;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AiCoreApi.Authorization;

public class BasicUserValidationService : IBasicUserValidationService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BasicUserValidationService> _logger;
    private readonly ILoginProcessor _loginProcessor;
    private readonly ExtendedConfig _extendedConfig;

    public BasicUserValidationService(
        IHttpContextAccessor httpContextAccessor,
        ILogger<BasicUserValidationService> logger,
        ILoginProcessor loginProcessor,
        ExtendedConfig extendedConfig)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _loginProcessor = loginProcessor;
        _extendedConfig = extendedConfig;
    }

    public async Task<bool> IsValidAsync(string username, string password)
    {
        try
        {
            if(!_extendedConfig.AllowBasicAuth)
                return false;
            var login = await _loginProcessor.GetByCredentials(username, password);
            var result = login != null;
            if (result && _httpContextAccessor.HttpContext != null)
            {
                _httpContextAccessor.HttpContext.Items.Add(ClaimTypes.Role, nameof(login.Role));
            }
            return result;
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            throw;
        }
    }
}

public class CombinedAuthorizeAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.Items.ContainsKey(ClaimTypes.Role))
        {
            context.HttpContext.User.AddIdentity(new ClaimsIdentity(new List<Claim>
            {
                new(ClaimTypes.Role, context.HttpContext.Items[ClaimTypes.Role]?.ToString() ?? nameof(RoleEnum.User)),
            }));
        }
    }
}
