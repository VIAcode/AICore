using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class AgentsProcessor : IAgentsProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public AgentsProcessor(
        IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<AgentModel>> ListAll()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Agents.Include(e => e.Tags).AsNoTracking();
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task<List<AgentModel>> List(int? workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Agents.Include(e => e.Tags).AsNoTracking();
        if (workspaceId == 0)
        {
            qry = qry.Where(e => e.WorkspaceId == null || e.WorkspaceId == 0);
        }
        else if (workspaceId != null && workspaceId > 0)
        {
            qry = qry.Where(e => e.WorkspaceId == workspaceId);
        }
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task<AgentModel?> GetById(int agentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Agents.Include(e => e.Tags).AsNoTracking().FirstOrDefaultAsync(e => e.AgentId == agentId);
    }

    public async Task<AgentModel?> GetByName(string agentName, int? workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Agents.Include(e => e.Tags).AsNoTracking();
        if (workspaceId == 0)
            qry = qry.Where(item => item.WorkspaceId == null || item.WorkspaceId == 0);
        else if (workspaceId != null)
            qry = qry.Where(item => item.WorkspaceId == workspaceId);
        return await qry.FirstOrDefaultAsync(e => e.Name == agentName);
    }

    public async Task<AgentModel?> Update(AgentModel agentModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingAgent = await db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentModel.AgentId);
        if (existingAgent == null)
            return await Add(agentModel, agentModel.WorkspaceId ?? 0);
        agentModel.WorkspaceId = existingAgent.WorkspaceId;
        var tIds = agentModel.Tags.Select(e => e.TagId);
        var tags = tIds.Any()
            ? await db.Tags.Where(e => tIds.Contains(e.TagId)).ToListAsync()
            : new List<TagModel>();
        db.Entry(existingAgent).CurrentValues.SetValues(agentModel);
        existingAgent.Tags = tags;
        db.Agents.Update(existingAgent);
        await db.SaveChangesAsync();
        return existingAgent;
    }

    public async Task<AgentModel> Add(AgentModel agentModel, int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingAgent = await db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentModel.AgentId);
        if (existingAgent != null) return existingAgent;

        if (agentModel.Tags is { Count: > 0 })
        {
            var tags = db.Tags.ToList();
            var tIds = agentModel.Tags.Select(e => e.TagId);
            agentModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
        }
        agentModel.WorkspaceId = workspaceId > 0 ? workspaceId : null;
        await db.Agents.AddAsync(agentModel);
        await db.SaveChangesAsync();
        return agentModel;
    }

    public async Task Delete(int agentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentId);
        if (agent == null)
            return;
        db.Agents.Remove(agent);
        await db.SaveChangesAsync();
    }

    public async Task UpdateFlowNameForAsync(string flowName, string? value, int? workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Agents.Where(e => e.FlowName == flowName);
        if (workspaceId == null || workspaceId == 0)
            qry = qry.Where(item => item.WorkspaceId == null || item.WorkspaceId == 0);
        else
            qry = qry.Where(item => item.WorkspaceId == workspaceId);
        var agents = await qry.ToListAsync();
        if (!agents.Any())
            return;
        foreach (var agent in agents)
        {
            agent.FlowName = value;
            db.Agents.Update(agent);
        }
        await db.SaveChangesAsync();
    }

    public async Task UpdateFlowNameForAsync(List<int> ids, string? value, int? workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Agents.Where(e => ids.Contains(e.AgentId));
        if (workspaceId == null || workspaceId == 0)
            qry = qry.Where(item => item.WorkspaceId == null || item.WorkspaceId == 0);
        else 
            qry = qry.Where(item => item.WorkspaceId == workspaceId);
        var agents = await qry.ToListAsync();
        if (!agents.Any())
            return;
        foreach (var agent in agents)
        {
            agent.FlowName = value;
            db.Agents.Update(agent);
        }
        await db.SaveChangesAsync();
    }
}

public interface IAgentsProcessor
{
    Task<List<AgentModel>> ListAll();
    Task<List<AgentModel>> List(int? workspaceId);
    Task<AgentModel?> GetById(int agentId);
    Task<AgentModel?> GetByName(string agentName, int? workspaceId);
    Task<AgentModel> Add(AgentModel agentModel, int workspaceId);
    Task<AgentModel?> Update(AgentModel agentModel);
    Task Delete(int agentId);
    Task UpdateFlowNameForAsync(string flowName, string? value, int? workspaceId);
    Task UpdateFlowNameForAsync(List<int> ids, string? value, int? workspaceId);
}