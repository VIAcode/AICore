using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class EvaluationHistoryProcessor : IEvaluationHistoryProcessor
{
    private readonly Db _db;

    public EvaluationHistoryProcessor(Db db)
    {
        _db = db;
    }

    public async Task<EvaluationHistoryModel?> Get(int evaluationHistoryId)
    {
        return await _db.EvaluationHistory.AsNoTracking().FirstOrDefaultAsync(e => e.EvaluationHistoryId == evaluationHistoryId);
    }

    public async Task<List<EvaluationHistoryModel>> List(int workspaceId)
    {
        var qry = _db.EvaluationHistory.OrderByDescending(item => item.EvaluationHistoryId).AsNoTracking();
        qry = qry.Where(e => e.WorkspaceId == workspaceId);
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task<List<EvaluationHistoryModel>> ListShort(int evaluationId, int workspaceId)
    {
        var agentNames = await _db.EvaluationHistory
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
        var evaluationHistory = await _db.EvaluationHistory.FirstOrDefaultAsync(item => item.EvaluationHistoryId == evaluationHistoryId);
        if (evaluationHistory == null) return;
        _db.EvaluationHistory.Remove(evaluationHistory);
        await _db.SaveChangesAsync();
    }

    public async Task<EvaluationHistoryModel> Add(EvaluationHistoryModel evaluationHistoryModel)
    {
        if (evaluationHistoryModel.EvaluationHistoryId != 0) 
            throw new ArgumentException("Evaluation History identifier must be zero");

        await _db.EvaluationHistory.AddAsync(evaluationHistoryModel);
        await _db.SaveChangesAsync();

        return evaluationHistoryModel;
    }

    public async Task<EvaluationHistoryModel> Update(EvaluationHistoryModel evaluationHistoryModel)
    {
        if (evaluationHistoryModel.EvaluationHistoryId == 0) 
            throw new ArgumentException("Evaluation History identifier mustn't be zero");

        var existingEvaluationHistory = await _db.EvaluationHistory.FirstAsync(item => item.EvaluationHistoryId == evaluationHistoryModel.EvaluationHistoryId);
        _db.Entry(existingEvaluationHistory).CurrentValues.SetValues(evaluationHistoryModel);
        _db.EvaluationHistory.Update(existingEvaluationHistory);
        await _db.SaveChangesAsync();
        return existingEvaluationHistory;
    }
}

public interface IEvaluationHistoryProcessor
{
    Task<EvaluationHistoryModel?> Get(int evaluationHistoryId);
    Task<List<EvaluationHistoryModel>> List(int workspaceId);
    Task<List<EvaluationHistoryModel>> ListShort(int evaluationId, int workspaceId); 
    Task Delete(int evaluationHistoryId);
    Task<EvaluationHistoryModel> Add(EvaluationHistoryModel evaluationHistoryModel);
    Task<EvaluationHistoryModel> Update(EvaluationHistoryModel evaluationHistoryModel);
}