using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class EvaluationHistoryProcessor : IEvaluationHistoryProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public EvaluationHistoryProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<EvaluationHistoryModel?> Get(int evaluationHistoryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.EvaluationHistory.AsNoTracking().FirstOrDefaultAsync(e => e.EvaluationHistoryId == evaluationHistoryId);
    }

    public async Task<EvaluationHistoryModel?> GetShort(int evaluationHistoryId)
    {
        var result = await Get(evaluationHistoryId);
        if (result == null) 
            return null;
        result.Questions.ForEach(q => q.DebugMessages = "[]");
        return result;
    }

    public async Task<List<EvaluationHistoryModel>> List(int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.EvaluationHistory
            .AsNoTracking();
        qry = qry.OrderByDescending(item => item.EvaluationHistoryId);
        qry = qry.Where(e => e.WorkspaceId == workspaceId);
        qry = qry.Select(e => new EvaluationHistoryModel
        {
            AgentName = e.AgentName,
            EvaluationId = e.EvaluationId,
            EvaluationHistoryId = e.EvaluationHistoryId,
            EvaluationLlmModel = e.EvaluationLlmModel,
            EvaluationPrompt = e.EvaluationPrompt,
            Status = e.Status,
            Created = e.Created,
            CreatedBy = e.CreatedBy,
            WorkspaceId = e.WorkspaceId
        });
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task<List<EvaluationHistoryModel>> List(int evaluationId, int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agentNames = await db.EvaluationHistory
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.EvaluationId == evaluationId)
            .OrderByDescending(e => e.EvaluationHistoryId)
            .Select(e => new EvaluationHistoryModel
            {
                AgentName = e.AgentName,
                EvaluationId = e.EvaluationId,
                EvaluationHistoryId = e.EvaluationHistoryId,
                EvaluationLlmModel = e.EvaluationLlmModel,
                EvaluationPrompt = e.EvaluationPrompt,
                Status = e.Status,
                Created = e.Created,
                CreatedBy = e.CreatedBy,
                WorkspaceId = e.WorkspaceId
            })
            .ToListAsync();
        return agentNames;
    }


    public async Task Delete(int evaluationHistoryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var evaluationHistory = await db.EvaluationHistory.FirstOrDefaultAsync(item => item.EvaluationHistoryId == evaluationHistoryId);
        if (evaluationHistory == null) return;
        db.EvaluationHistory.Remove(evaluationHistory);
        await db.SaveChangesAsync();
    }

    public async Task<EvaluationHistoryModel> Add(EvaluationHistoryModel evaluationHistoryModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (evaluationHistoryModel.EvaluationHistoryId != 0) 
            throw new ArgumentException("Evaluation History identifier must be zero");

        await db.EvaluationHistory.AddAsync(evaluationHistoryModel);
        await db.SaveChangesAsync();

        return evaluationHistoryModel;
    }

    public async Task<EvaluationHistoryModel> Update(EvaluationHistoryModel evaluationHistoryModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (evaluationHistoryModel.EvaluationHistoryId == 0) 
            throw new ArgumentException("Evaluation History identifier mustn't be zero");

        var existingEvaluationHistory = await db.EvaluationHistory.FirstAsync(item => item.EvaluationHistoryId == evaluationHistoryModel.EvaluationHistoryId);
        db.Entry(existingEvaluationHistory).CurrentValues.SetValues(evaluationHistoryModel);
        db.EvaluationHistory.Update(existingEvaluationHistory);
        await db.SaveChangesAsync();
        return existingEvaluationHistory;
    }
}

public interface IEvaluationHistoryProcessor
{
    Task<EvaluationHistoryModel?> Get(int evaluationHistoryId);
    Task<EvaluationHistoryModel?> GetShort(int evaluationHistoryId);
    Task<List<EvaluationHistoryModel>> List(int workspaceId);
    Task<List<EvaluationHistoryModel>> List(int evaluationId, int workspaceId); 
    Task Delete(int evaluationHistoryId);
    Task<EvaluationHistoryModel> Add(EvaluationHistoryModel evaluationHistoryModel);
    Task<EvaluationHistoryModel> Update(EvaluationHistoryModel evaluationHistoryModel);
}