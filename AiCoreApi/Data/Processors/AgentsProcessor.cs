using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class AgentsProcessor : IAgentsProcessor
{
    private readonly Db _db;

    public AgentsProcessor(
        Db db)
    {
        _db = db;
    }

    public async Task<List<AgentModel>> List(int? workspaceId)
    {
        var qry = _db.Agents.Include(e => e.Tags).AsNoTracking();
        if (workspaceId == 0)
        {
            qry = qry.Where(e => e.WorkspaceId == null);
        }
        else if(workspaceId != null && workspaceId > 0)
        {
            qry = qry.Where(e => e.WorkspaceId == workspaceId);
        }
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task<AgentModel?> GetById(int agentId)
    {
        return await _db.Agents.Include(e => e.Tags).AsNoTracking().FirstOrDefaultAsync(e => e.AgentId == agentId);
    }

    public async Task<AgentModel?> GetByName(string agentName, int? workspaceId)
    {
        var qry = _db.Agents.Include(e => e.Tags).AsNoTracking();
        if (workspaceId == 0)
            qry = qry.Where(item => item.WorkspaceId == null);
        else if (workspaceId != null)
            qry = qry.Where(item => item.WorkspaceId == workspaceId);
        return await qry.FirstOrDefaultAsync(e => e.Name == agentName);
    }

    public async Task<AgentModel?> Update(AgentModel agentModel)
    {
        var existingAgent = await _db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentModel.AgentId);
        if (existingAgent == null)
            return await Add(agentModel, agentModel.WorkspaceId ?? 0);
        agentModel.WorkspaceId = existingAgent.WorkspaceId;
        var tIds = agentModel.Tags.Select(e => e.TagId);
        var tags = tIds.Any()
            ? await _db.Tags.Where(e => tIds.Contains(e.TagId)).ToListAsync()
            : new List<TagModel>();
        _db.Entry(existingAgent).CurrentValues.SetValues(agentModel);
        existingAgent.Tags = tags;
        _db.Agents.Update(existingAgent);
        await _db.SaveChangesAsync();
        return existingAgent;
    }

    public async Task<AgentModel> Add(AgentModel agentModel, int workspaceId)
    {
        var existingAgent = await _db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentModel.AgentId);
        if (existingAgent != null) return existingAgent;

        if (agentModel.Tags is { Count: > 0 })
        {
            var tags = _db.Tags.ToList();
            var tIds = agentModel.Tags.Select(e => e.TagId);
            agentModel.Tags = tags.Where(e => tIds.Contains(e.TagId)).ToList();
        }
        agentModel.WorkspaceId = workspaceId > 0 ? workspaceId : null;
        await _db.Agents.AddAsync(agentModel);
        await _db.SaveChangesAsync();
        return agentModel;
    }

    public async Task Delete(int agentId)
    {
        var agent = await _db.Agents
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(item => item.AgentId == agentId);
        if (agent == null)
            return;
        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync();
    }
}

public interface IAgentsProcessor
{
    Task<List<AgentModel>> List(int? workspaceId);
    Task<AgentModel?> GetById(int agentId);
    Task<AgentModel?> GetByName(string agentName, int? workspaceId);
    Task<AgentModel> Add(AgentModel agentModel, int workspaceId);
    Task<AgentModel?> Update(AgentModel agentModel);
    Task Delete(int agentId);
}