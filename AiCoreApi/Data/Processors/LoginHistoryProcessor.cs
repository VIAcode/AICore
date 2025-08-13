using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class LoginHistoryProcessor : ILoginHistoryProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public LoginHistoryProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<LoginHistoryModel> Add(LoginHistoryModel loginHistory)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.LoginHistory.AddAsync(loginHistory);
            await db.SaveChangesAsync();
            return loginHistory;
        }

        public async Task<LoginHistoryModel?> GetByRefreshToken(string refreshToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (string.IsNullOrEmpty(refreshToken))
                return null;
            var result = await db.LoginHistory.AsNoTracking().FirstOrDefaultAsync(item => item.RefreshToken == refreshToken);
            if (result == null || result.ValidUntilTime < DateTime.UtcNow)
                return null;
            return result;
        }

        public async Task<LoginHistoryModel?> GetByCode(string code)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            if (string.IsNullOrEmpty(code))
                return null;
            var result = await db.LoginHistory.AsNoTracking().FirstOrDefaultAsync(item => item.Code == code);
            if (result == null || result.ValidUntilTime < DateTime.UtcNow)
                return null;
            return result;
        }

        public async Task<LoginHistoryModel?> GetBySessionId(int sessionId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.LoginHistory.AsNoTracking().FirstOrDefaultAsync(item => item.LoginHistoryId == sessionId);
            if (result == null || result.ValidUntilTime < DateTime.UtcNow)
                return null;
            return result;
        }

        public async Task Update(LoginHistoryModel loginHistory)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingLoginHistory = await db.LoginHistory.FirstOrDefaultAsync(item => item.LoginHistoryId == loginHistory.LoginHistoryId);
            if (existingLoginHistory == null)
                return;
            db.Entry(existingLoginHistory).CurrentValues.SetValues(loginHistory);
            db.LoginHistory.Update(existingLoginHistory);
            await db.SaveChangesAsync();
        }
    }

    public interface ILoginHistoryProcessor
    {
        Task<LoginHistoryModel> Add(LoginHistoryModel loginHistory);
        Task<LoginHistoryModel?> GetByRefreshToken(string refreshToken);
        Task<LoginHistoryModel?> GetByCode(string code);
        Task<LoginHistoryModel?> GetBySessionId(int sessionId);
        Task Update(LoginHistoryModel login);
    }
}