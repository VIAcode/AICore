using AiCoreApi.Authorization;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Security.Claims;

namespace AiCoreApi.Common
{
    public class UserContextAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        private readonly ILoginProcessor _loginProcessor;

        public static AsyncLocal<int?> AsyncScheduledLoginId = new();
        private int? _loginId;
        private List<TagModel>? _tags;

        public UserContextAccessor(IHttpContextAccessor httpContextAccessor, ILoginProcessor loginProcessor)
        {
            _httpContextAccessor = httpContextAccessor;
            _loginProcessor = loginProcessor;
        }

        public async Task<int?> GetLoginIdAsync()
        {
            if (!_loginId.HasValue)
            {
                await LoadUserData();
            }
            return _loginId;
        }

        public async Task<List<TagModel>> GetTagsAsync()
        {
            if (_tags == null)
                await LoadUserData();
            return _tags;
        }

        public void SetLoginId(int? loginId)
        {
            _loginId = loginId;
        }

        public void SetTags(List<TagModel> tags)
        {
            _tags = tags;
        }

        private readonly Dictionary<string, bool> _roles = new();
        public bool HasRole(string role)
        {
            if (!_roles.ContainsKey(role))
                _roles[role] = _httpContextAccessor?.HttpContext?.User.FindFirstValue(ClaimTypes.Role) == role;
            return _roles[role];
        }

        private async Task LoadUserData()
        {
            if (_httpContextAccessor.HttpContext == null)
                return;
            if (_httpContextAccessor.HttpContext.User.Identity is not ClaimsIdentity identity)
                return;
            var claims = identity.Claims.ToDictionary(key => key.Type, value => value.Value);
            claims.TryGetValue(ClaimTypes.NameIdentifier, out var login);

            var loginType = claims.TryGetValue(IdTokenClaims.LoginType, out var loginTypeClaimValue)
                ? Enum.Parse<LoginTypeEnum>(loginTypeClaimValue)
                : LoginTypeEnum.Password;
            if (login == null)
                return;
            var userData = await _loginProcessor.GetByLogin(login, loginType);
            if(userData == null)
                return;
            _loginId = userData.LoginId;
            _tags = userData.Tags;
        }
    }
}
