using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Elastic.Clients.Elasticsearch.Ingest;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class WorkspaceProcessor : IWorkspaceProcessor
{
    private readonly Db _db;

    public WorkspaceProcessor(Db db)
    {
        _db = db;
    }

    public async Task<WorkspaceModel?> Get(int workspaceId)
    {
        return await _db.Workspaces.Include(e => e.Tags).AsNoTracking().FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);
    }

    public async Task<WorkspaceModel?> Set(WorkspaceModel workspaceModel)
    {
        WorkspaceModel workspace;
        if (workspaceModel.WorkspaceId == 0)
        {
            if (workspaceModel.Tags.Count > 0)
            {
                var tags = _db.Tags.ToList();
                var tIds = workspaceModel.Tags.Select(e => e.TagId);
                workspaceModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }
            await _db.Workspaces.AddAsync(workspaceModel);
        }
        else
        {
            workspace = _db.Workspaces.Include(e => e.Tags).FirstOrDefault(item => item.WorkspaceId == workspaceModel.WorkspaceId);
            if (workspace == null) 
                return null;

            if (workspaceModel.Tags.Count > 0)
            {
                var tags = _db.Tags.ToList();
                var tIds = workspaceModel.Tags.Select(e => e.TagId);
                workspace.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }
            workspace.Name = workspaceModel.Name;
            workspace.Description = workspaceModel.Description;
            _db.Workspaces.Update(workspace);
        }
        await _db.SaveChangesAsync();
        return workspaceModel;
    }

    public async Task<List<WorkspaceModel>> ListAsync()
    {
        return await _db.Workspaces.Include(e => e.Tags).AsNoTracking().ToListAsync();
    }

    public async Task<bool> Remove(int workspacesId)
    {
        var workspace = await _db.Workspaces.Include(e => e.Tags).FirstOrDefaultAsync(item => item.WorkspaceId == workspacesId);
        if (workspace != null)
        {
            // Clean up relations
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM agents WHERE workspace_id = {0}", workspacesId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM connection WHERE workspace_id = {0}", workspacesId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM ingestion WHERE workspace_id = {0}", workspacesId);

            _db.Workspaces.Remove(workspace); 
            await _db.SaveChangesAsync();
        }
        return true;
    }
}

public interface IWorkspaceProcessor
{
    Task<WorkspaceModel?> Get(int workspaceId);
    Task<WorkspaceModel?> Set(WorkspaceModel workspaceModel);
    Task<List<WorkspaceModel>> ListAsync();
    Task<bool> Remove(int workspaceId);
}