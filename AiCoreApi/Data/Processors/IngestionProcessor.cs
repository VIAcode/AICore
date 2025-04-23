using AiCoreApi.Common;
using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class IngestionProcessor : IIngestionProcessor
    {
        private readonly Db _db;
        private readonly IDbQuery _dbQuery;
        private readonly ExtendedConfig _config;

        public IngestionProcessor(Db db, IDbQuery dbQuery, ExtendedConfig config)
        {
            _db = db;
            _dbQuery = dbQuery;
            _config = config;
        }

        public async Task<IngestionModel?> GetIngestionById(int ingestionId, bool excludeFile = false)
        {
            var result = await _db.Ingestions.Include(e => e.Tags)
                .Where(t => t.IngestionId == ingestionId)
                .Select(item => new IngestionModel
                {
                    Content = item.Content,
                    Created = item.Created,
                    CreatedBy = item.CreatedBy,
                    Updated = item.Updated,
                    IngestionId = item.IngestionId,
                    Note = item.Note,
                    Name = item.Name,
                    Type = item.Type,
                    Tags = item.Tags,
                    LastSync = item.LastSync,
                })
                .AsNoTracking()
                .FirstOrDefaultAsync();
            if(excludeFile && result?.Content.ContainsKey("File") == true)
                result.Content["File"] = "..file content..";
            return result;
        }

        public async Task<List<IngestionModel>> List(int? workspaceId)
        {
            var qry = _db.Ingestions.Include(e => e.Tags)
                .Select(item => new IngestionModel
                {
                    Content = item.Content,
                    Created = item.Created,
                    CreatedBy = item.CreatedBy,
                    Updated = item.Updated,
                    IngestionId = item.IngestionId,
                    Note = item.Note,
                    Name = item.Name,
                    Type = item.Type,
                    Tags = item.Tags,
                    LastSync = item.LastSync,
                    WorkspaceId = item.WorkspaceId,
                })
                .AsNoTracking();

            if (workspaceId == 0)
                qry = qry.Where(t => t.WorkspaceId == null);
            else if (workspaceId != null)
                qry = qry.Where(t => t.WorkspaceId == workspaceId);

            var result = await qry.ToListAsync();
            foreach (var item in result)
            {
                if (item.Content.ContainsKey("File"))
                    item.Content["File"] = "..file content..";
            }
            return result;
        }

        public async Task<IngestionModel> Set(IngestionModel ingestionModel, int? workspaceId)
        {
            IngestionModel? settingValue;

            var itId = ingestionModel.IngestionId;
            var tIds = ingestionModel.Tags.Select(e => e.TagId);

            var tags = tIds.Any()
                ? await _db.Tags.Where(e => tIds.Contains(e.TagId)).ToListAsync()
                : new();

            if (itId == 0)
            {
                settingValue = new IngestionModel
                {
                    CreatedBy = ingestionModel.CreatedBy,
                    Created = ingestionModel.Created,
                    Note = ingestionModel.Note,
                    Name = ingestionModel.Name,
                    Type = ingestionModel.Type,
                    Content = ingestionModel.Content,
                    WorkspaceId = workspaceId == 0 ? null : workspaceId,
                    Tags = tags,

                };
                await _db.Ingestions.AddAsync(settingValue);
            }
            else
            {
                settingValue = await _db.Ingestions
                    .Include(e => e.Tags)
                    .FirstAsync(item => item.IngestionId == itId);

                settingValue.Updated = DateTime.UtcNow;
                settingValue.Note = ingestionModel.Note;
                settingValue.Name = ingestionModel.Name;
                settingValue.Content = ingestionModel.Content;
                settingValue.Tags = tags;

                _db.Ingestions.Update(settingValue);
            }

            await _db.SaveChangesAsync();
            return settingValue;
        }

        public async Task<IngestionModel> SetSyncTime(int ingestionId, DateTime syncTime)
        {
            var settingValue =
                await _db.Ingestions.FirstAsync(item => item.IngestionId == ingestionId);

            settingValue.LastSync = syncTime;

            _db.Ingestions.Update(settingValue);

            await _db.SaveChangesAsync();
            return settingValue;
        }

        public List<IngestionModel> GetStale()
        {
            var delayThreshold =
                DateTime.UtcNow - TimeSpan.FromHours(_config.IngestionDelay);

            return _db.Ingestions.Include(e => e.Tags).AsNoTracking()
                .Where(i => i.LastSync < delayThreshold)
                .OrderBy(i => i.LastSync).ToList();
        }

        public async Task Remove(int ingestionId)
        {
            var ingestion = await _db.Ingestions
                .FirstOrDefaultAsync(item => item.IngestionId == ingestionId);
            if (ingestion == null)
                return;
            _db.Ingestions.Remove(ingestion);
            await _db.SaveChangesAsync();
        }

        public async Task<List<int>> GetActiveConnectionIds(int? workspaceId)
        {
            return (await List(workspaceId))
                .Where(e => e.Content.ContainsKey("ConnectionId"))
                .Select(e => Convert.ToInt32(e.Content["ConnectionId"]))
                .Distinct()
                .ToList();
        }
    }

    public interface IIngestionProcessor
    {
        Task<List<IngestionModel>> List(int? workspaceId);
        Task<IngestionModel?> GetIngestionById(int ingestionId, bool excludeFile = false);
        Task<IngestionModel> Set(IngestionModel ingestionModel, int? workspaceId);
        Task<IngestionModel> SetSyncTime(int ingestionId, DateTime syncTime);
        List<IngestionModel> GetStale();
        Task Remove(int ingestionId);
        Task<List<int>> GetActiveConnectionIds(int? workspaceId);
    }
}