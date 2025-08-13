using AiCoreApi.Common.Data;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class LoginProcessor : ILoginProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public LoginProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<LoginModel?> GetByCredentials(string login, string password)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var loginModel = await db.Login.Include(e => e.Tags).AsNoTracking()
                .FirstOrDefaultAsync(item => item.Login == login && item.LoginType == LoginTypeEnum.Password);
            return loginModel == null || !loginModel.IsEnabled || loginModel.PasswordHash != password.GetHash()
                ? null
                : loginModel;
        }

        public async Task<List<LoginModel>> List()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Login.Include(e => e.Tags).AsNoTracking().ToListAsync();
        }

        public async Task<List<LoginWithSpentModel>> ListWithSpent()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var loginModelExtended = await db.Login
                .Include(e => e.Tags)
                .GroupJoin(
                    db.Spent
                        .Where(x => x.Date == DateTime.UtcNow.Date)
                        .GroupBy(x => x.LoginId)
                        .Select(group => new
                        {
                            LoginId = (int?)group.Key,
                            TokensIncoming = (int?)group.Sum(x => x.TokensIncoming), 
                            TokensOutgoing = (int?)group.Sum(x => x.TokensOutgoing)
                        }),
                    login => login.LoginId,
                    spent => spent.LoginId,
                    (login, spent) => new { login, spent }
                )
                .SelectMany(
                    x => x.spent.DefaultIfEmpty(),
                    (x, spent) => new LoginWithSpentModel(x.login)
                    {
                        TokensSpent = spent != null 
                            ? spent.TokensIncoming + spent.TokensOutgoing 
                            : 0
                    }
                )
                .ToListAsync();
            loginModelExtended = loginModelExtended.OrderBy(item => item.LoginId).ToList();
            return loginModelExtended;
        }

        public async Task<LoginModel?> GetById(int id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var login = await db.Login.AsNoTracking()
                .Include(e => e.Tags)
                .Include(e => e.Groups).AsNoTracking()
                .FirstOrDefaultAsync(item => item.LoginId == id);
            return login;
        }

        public async Task<LoginModel?> GetByLogin(string login, LoginTypeEnum loginType)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.Login.AsNoTracking()
                .Include(e => e.Tags).AsNoTracking()
                .Include(e => e.Groups).AsNoTracking()
                .FirstOrDefaultAsync(item => item.Login == login && item.LoginType == loginType);
        }

        public async Task<List<TagModel>> GetTagsByLogin(string login, LoginTypeEnum loginType)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var loginModel = await db.Login.AsNoTracking()
                .Include(e => e.Tags)
                .Include(e => e.Groups).ThenInclude(e => e.Tags)
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Login == login && e.LoginType == loginType);
                
            var tags = new List<TagModel>(loginModel.Tags)
                .Union(loginModel.Groups.SelectMany(e => e.Tags))
                .DistinctBy(e => e.TagId)
                .Select(e => new TagModel
                {
                    TagId = e.TagId,
                    Name = e.Name,
                    Description = e.Description,
                    Created = e.Created,
                    CreatedBy = e.CreatedBy,
                    Color = e.Color
                })
                .ToList();

            return tags;
        }

        public async Task Update(LoginModel loginModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingLogin = await db.Login
                .Include(e => e.Tags)
                .Include(e => e.Groups)
                .FirstOrDefaultAsync(item => item.Login == loginModel.Login && loginModel.LoginType == item.LoginType);

            if (existingLogin == null)
                return;

            var tIds = loginModel.Tags.Select(e => e.TagId);
            var gIds = loginModel.Groups.Select(e => e.GroupId);

            var tags = tIds.Any()
                ? await db.Tags.Where(e => tIds.Contains(e.TagId)).ToListAsync()
                : new List<TagModel>();

            var groups = gIds.Any()
                ? await db.Groups.Where(e => gIds.Contains(e.GroupId)).ToListAsync()
            : new List<GroupModel>();

            db.Entry(existingLogin).CurrentValues.SetValues(loginModel);

            existingLogin.Tags = tags;
            existingLogin.Groups = groups;

            db.Login.Update(existingLogin);
            await db.SaveChangesAsync();
        }

        public async Task Delete(int id)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var login = await db.Login.FirstOrDefaultAsync(item => item.LoginId == id);
            if (login == null) return;
            db.Login.Remove(login);
            await db.SaveChangesAsync();
        }

        public async Task<LoginModel> Add(LoginModel loginModel)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingLogin = await db.Login
                .Include(e => e.Tags)
                .Include(e => e.Groups)
                .FirstOrDefaultAsync(item => item.Login == loginModel.Login && loginModel.LoginType == item.LoginType);

            if (existingLogin != null) return existingLogin;

            if(loginModel.Tags != null && loginModel.Tags.Count > 0)
            {
                var tags = db.Tags.ToList();
                var tIds = loginModel.Tags.Select(e => e.TagId);

                loginModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }

            if (loginModel.Groups != null && loginModel.Groups.Count > 0)
            {
                var groups = db.Groups.ToList();
                var gIds = loginModel.Groups.Select(e => e.GroupId);

                loginModel.Groups = groups.Where(e => gIds.Contains(e.GroupId)).ToList();
            }

            await db.Login.AddAsync(loginModel);
            await db.SaveChangesAsync();
            return loginModel;
        }
    }

    public interface ILoginProcessor
    {
        Task<LoginModel?> GetByCredentials(string login, string password);
        Task<List<LoginModel>> List();
        Task<List<LoginWithSpentModel>> ListWithSpent();
        Task<LoginModel?> GetById(int id);
        Task<LoginModel?> GetByLogin(string login, LoginTypeEnum loginType);
        Task<List<TagModel>> GetTagsByLogin(string login, LoginTypeEnum loginType);
        Task Update(LoginModel login);
        Task Delete(int id);
        Task<LoginModel> Add(LoginModel login);
    }
}