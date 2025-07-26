using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class EvaluationProcessor: IEvaluationProcessor
{
    private readonly Db _db;

    public EvaluationProcessor(Db db)
    {
        _db = db;
    }

    public async Task<EvaluationModel?> Get(int evaluationId)
    {
        return await _db.Evaluation.AsNoTracking().FirstOrDefaultAsync(e => e.EvaluationId == evaluationId);
    }

    public async Task<EvaluationModel?> Get(string evaluationName, int workspaceId)
    {
        if (string.IsNullOrEmpty(evaluationName))
            return null;
        var qry = _db.Evaluation.AsNoTracking().Where(e => e.Name == evaluationName && e.WorkspaceId == workspaceId);
        return await qry.FirstOrDefaultAsync();
    }

    public async Task<List<EvaluationModel>> List(int workspaceId)
    {
        var qry = _db.Evaluation.OrderByDescending(item => item.EvaluationId).AsNoTracking();
        qry = qry.Where(e => e.WorkspaceId == workspaceId);
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task Delete(int evaluationId)
    {
        var evaluation = await _db.Evaluation.FirstOrDefaultAsync(item => item.EvaluationId == evaluationId);
        if (evaluation == null) return;
        _db.Evaluation.Remove(evaluation);
        await _db.SaveChangesAsync();
    }

    public async Task<EvaluationModel> Add(EvaluationModel evaluationModel)
    {
        if (evaluationModel.EvaluationId != 0) 
            throw new ArgumentException("Evaluation identifier must be zero");

        await _db.Evaluation.AddAsync(evaluationModel);
        await _db.SaveChangesAsync();

        return evaluationModel;
    }

    public async Task<EvaluationModel> Update(EvaluationModel evaluationModel)
    {
        if (evaluationModel.EvaluationId == 0) 
            throw new ArgumentException("Evaluation identifier mustn't be zero");

        var existingEvaluation = await _db.Evaluation.FirstAsync(item => item.EvaluationId == evaluationModel.EvaluationId);
        _db.Entry(existingEvaluation).CurrentValues.SetValues(evaluationModel);
        _db.Evaluation.Update(existingEvaluation);
        await _db.SaveChangesAsync();
        return existingEvaluation;
    }
}

public interface IEvaluationProcessor
{
    Task<EvaluationModel?> Get(int evaluationId);
    Task<EvaluationModel?> Get(string evaluationName, int workspaceId);
    Task<List<EvaluationModel>> List(int workspaceId);
    Task Delete(int evaluationId);
    Task<EvaluationModel> Add(EvaluationModel evaluationModel);
    Task<EvaluationModel> Update(EvaluationModel evaluationModel);
}