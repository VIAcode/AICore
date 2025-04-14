using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class DebugLogProcessor : IDebugLogProcessor
    {
        private readonly Db _db;
        private readonly ExtendedConfig _extendedConfig;

        public DebugLogProcessor(
            Db db, 
            ExtendedConfig extendedConfig)
        {
            _db = db;
            _extendedConfig = extendedConfig;
        }

        public async Task<List<DebugLogModel>> List(DebugLogFilterModel filter, int workspaceId)
        {
            var result = _db.DebugLog.AsNoTracking()
                .OrderByDescending(item => item.DebugLogId)
                .Skip(filter.Skip)
                .Take(filter.Take);
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
                result = result.Where(item => item.WorkspaceId == null);
            return await result
                .ToListAsync();
        }

        public async Task<int> PagesCount(DebugLogFilterModel filter, int workspaceId)
        {
            var result = _db.DebugLog.AsNoTracking();
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
                result = result.Where(item => item.WorkspaceId == null);
            var itemsCount = await result.CountAsync();
            return itemsCount % filter.Take == 0 ? itemsCount / filter.Take : itemsCount / filter.Take + 1;
        }

        public async Task<DebugLogModel> Set(DebugLogModel debugLogModel, int workspaceId)
        {
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
                await _db.DebugLog.AddAsync(debugLogValue);
            }
            else
            {
                debugLogValue = await _db.DebugLog.FirstAsync(item => item.DebugLogId == debugLogModel.DebugLogId);
                debugLogValue.Login = debugLogModel.Login;
                debugLogValue.Date = debugLogModel.Date;
                debugLogValue.Prompt = debugLogModel.Prompt;
                debugLogValue.Result = debugLogModel.Result;
                debugLogValue.DebugMessages = debugLogModel.DebugMessages;
                debugLogValue.Files = debugLogModel.Files;
                debugLogValue.SpentTokens = debugLogModel.SpentTokens;
                _db.DebugLog.Update(debugLogValue);
            }

            await _db.SaveChangesAsync();
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
                        Details = x.Details
                    }).ToList()
                }, workspaceId);
            }
        }

        public async Task Remove(DateTime dateLimit)
        {
            var oldLogs = await _db.DebugLog.FirstOrDefaultAsync(item => item.Date < dateLimit);
            if (oldLogs == null)
                return;
            _db.DebugLog.Remove(oldLogs);
            await _db.SaveChangesAsync();
        }

    }

    public interface IDebugLogProcessor
    {
        Task<List<DebugLogModel>> List(DebugLogFilterModel filter, int workspaceId);
        Task<int> PagesCount(DebugLogFilterModel filter, int workspaceId);
        Task<DebugLogModel> Set(DebugLogModel debugLogModel, int workspaceId);
        Task Add(string? login, string? prompt, MessageDialogViewModel messageDialog, int workspaceId);
        Task Remove(DateTime dateLimit);
    }
}
