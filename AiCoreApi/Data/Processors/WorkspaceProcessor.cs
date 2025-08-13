using AiCoreApi.Common.Data;
using AiCoreApi.Migrations;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class WorkspaceProcessor : IWorkspaceProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public WorkspaceProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkspaceModel?> Get(int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Workspaces.Include(e => e.Tags).AsNoTracking().FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);
    }

    public async Task<WorkspaceModel?> Set(WorkspaceModel workspaceModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        WorkspaceModel workspace;
        if (workspaceModel.WorkspaceId == 0)
        {
            if (workspaceModel.Tags.Count > 0)
            {
                var tags = db.Tags.ToList();
                var tIds = workspaceModel.Tags.Select(e => e.TagId);
                workspaceModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }
            await db.Workspaces.AddAsync(workspaceModel);
        }
        else
        {
            workspace = db.Workspaces.Include(e => e.Tags).FirstOrDefault(item => item.WorkspaceId == workspaceModel.WorkspaceId);
            if (workspace == null) 
                return null;

            if (workspaceModel.Tags.Count > 0)
            {
                var tags = db.Tags.ToList();
                var tIds = workspaceModel.Tags.Select(e => e.TagId);
                workspace.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
            }
            workspace.Name = workspaceModel.Name;
            workspace.Description = workspaceModel.Description;
            db.Workspaces.Update(workspace);
        }
        await db.SaveChangesAsync();
        return workspaceModel;
    }

    public async Task<List<WorkspaceModel>> ListAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Workspaces.Include(e => e.Tags).AsNoTracking().ToListAsync();
    }

    public async Task<bool> Remove(int workspaceId)
    {
        if (workspaceId <= 0)
            return false;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var workspace = await db.Workspaces.Include(e => e.Tags).FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId);
        if (workspace != null)
        {
            // Clean up relations
            await db.Database.ExecuteSqlRawAsync("DELETE FROM agents WHERE workspace_id = {0}", workspaceId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM connection WHERE workspace_id = {0}", workspaceId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ingestion WHERE workspace_id = {0}", workspaceId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM debug_log WHERE workspace_id = {0}", workspaceId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM evaluation WHERE workspace_id = {0}", workspaceId);
            
            db.Workspaces.Remove(workspace); 
            await db.SaveChangesAsync();
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