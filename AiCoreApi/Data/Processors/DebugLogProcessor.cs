using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class DebugLogProcessor : IDebugLogProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;
        private readonly ExtendedConfig _extendedConfig;

        public DebugLogProcessor(
            IDbContextFactory<Db> dbFactory,
            ExtendedConfig extendedConfig)
        {
            _dbFactory = dbFactory;
            _extendedConfig = extendedConfig;
        }

        public async Task<List<DebugLogModel>> List(DebugLogFilterModel filter, int workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = db.DebugLog.AsNoTracking();
            if (!string.IsNullOrEmpty(filter.Login))
                result = result.Where(item => item.Login.Contains(filter.Login));
            if (!string.IsNullOrEmpty(filter.Result))
                result = result.Where(item => item.Result.Contains(filter.Result));
            if (!string.IsNullOrEmpty(filter.Prompt))
                result = result.Where(item => item.Prompt.Contains(filter.Prompt));
            if (filter.DateFrom != null)
                result = result.Where(item => item.Date > filter.DateFrom);
            if (workspaceId != 0)
                result = result.Where(item => item.WorkspaceId == workspaceId);
            else
                result = result.Where(item => item.WorkspaceId == null || item.WorkspaceId == 0);

            return await result
                .OrderByDescending(item => item.DebugLogId)
                .Skip(filter.Skip)
                .Take(filter.Take)
                .Select(item => new DebugLogModel
                {
                    DebugLogId = item.DebugLogId,
                    Login = item.Login,
                    Date = item.Date,
                    Prompt = item.Prompt,
                    Result = item.Result,
                    Files = item.Files,
                    SpentTokens = item.SpentTokens,
                    WorkspaceId = item.WorkspaceId,
                    DebugMessages = item.DebugMessages == null ? null : new List<DebugMessage>()
                })
                .ToListAsync();
        }

        public async Task<int> PagesCount(DebugLogFilterModel filter, int workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = db.DebugLog.AsNoTracking();
            if (!string.IsNullOrEmpty(filter.Login))
                result = result.Where(item => item.Login.Contains(filter.Login));
            if (!string.IsNullOrEmpty(filter.Result))
                result = result.Where(item => item.Result.Contains(filter.Result));
            if (!string.IsNullOrEmpty(filter.Prompt))
                result = result.Where(item => item.Prompt.Contains(filter.Prompt));
            if (filter.DateFrom != null)
                result = result.Where(item => item.Date > filter.DateFrom);
            if (workspaceId != 0)
                result = result.Where(item => item.WorkspaceId == workspaceId);
            else
                result = result.Where(item => item.WorkspaceId == null || item.WorkspaceId == 0);
            var itemsCount = await result.CountAsync();
            return itemsCount % filter.Take == 0 ? itemsCount / filter.Take : itemsCount / filter.Take + 1;
        }

        public async Task<DebugLogModel> Set(DebugLogModel debugLogModel, int workspaceId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            DebugLogModel? debugLogValue;
            if (debugLogModel.DebugLogId == 0)
            {
                debugLogValue = new DebugLogModel
                {
                    Login = debugLogModel.Login,
                    Date = debugLogModel.Date,
                    Prompt = debugLogModel.Prompt,
                    Result = debugLogModel.Result,
                    DebugMessages = debugLogModel.DebugMessages,
                    Files = debugLogModel.Files,
                    SpentTokens = debugLogModel.SpentTokens,
                    WorkspaceId = workspaceId,
                };
                await db.DebugLog.AddAsync(debugLogValue);
            }
            else
            {
                debugLogValue = await db.DebugLog.FirstAsync(item => item.DebugLogId == debugLogModel.DebugLogId);
                debugLogValue.Login = debugLogModel.Login;
                debugLogValue.Date = debugLogModel.Date;
                debugLogValue.Prompt = debugLogModel.Prompt;
                debugLogValue.Result = debugLogModel.Result;
                debugLogValue.DebugMessages = debugLogModel.DebugMessages;
                debugLogValue.Files = debugLogModel.Files;
                debugLogValue.SpentTokens = debugLogModel.SpentTokens;
                db.DebugLog.Update(debugLogValue);
            }

            await db.SaveChangesAsync();
            return debugLogValue;
        }

        public async Task Add(string? login, string? prompt, MessageDialogViewModel messageDialog, int workspaceId)
        {
            if (_extendedConfig.DebugMessagesStorageEnabled)
            {
                await Set(new DebugLogModel
                {
                    Login = login ?? "",
                    Prompt = prompt ?? "",
                    Result = messageDialog.Messages.Last().Text,
                    Files = messageDialog.Messages.Last().Files?.Select(x => x.Name).ToList(),
                    SpentTokens = messageDialog.Messages.Last().SpentTokens?.ToDictionary(
                        key => key.Key,
                        value => new TokensSpent { Request = value.Value.Request, Response = value.Value.Response }),
                    Date = DateTime.UtcNow,
                    DebugMessages = messageDialog.Messages.Last().DebugMessages?.Select(x => new DebugMessage
                    {
                        Sender = x.Sender,
                        DateTime = x.DateTime,
                        Title = x.Title,
                        Details = x.Details,
                        Level = x.Level
                    }).ToList()
                }, workspaceId);
            }
        }

        public async Task Remove(DateTime dateLimit)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var oldLogs = await db.DebugLog.FirstOrDefaultAsync(item => item.Date < dateLimit);
            if (oldLogs == null)
                return;
            db.DebugLog.Remove(oldLogs);
            await db.SaveChangesAsync();
        }

        public async Task<List<DebugMessage>?> GetDebugMessages(int debugLogId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var debugLog = await db.DebugLog.AsNoTracking()
                .FirstOrDefaultAsync(item => item.DebugLogId == debugLogId);
            return debugLog?.DebugMessages;
        }

    }

    public interface IDebugLogProcessor
    {
        Task<List<DebugLogModel>> List(DebugLogFilterModel filter, int workspaceId);
        Task<int> PagesCount(DebugLogFilterModel filter, int workspaceId);
        Task<DebugLogModel> Set(DebugLogModel debugLogModel, int workspaceId);
        Task Add(string? login, string? prompt, MessageDialogViewModel messageDialog, int workspaceId);
        Task Remove(DateTime dateLimit);
        Task<List<DebugMessage>?> GetDebugMessages(int debugLogId);
    }
}
