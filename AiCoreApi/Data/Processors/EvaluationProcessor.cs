using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class EvaluationProcessor: IEvaluationProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public EvaluationProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<EvaluationModel?> Get(int evaluationId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Evaluation.AsNoTracking().FirstOrDefaultAsync(e => e.EvaluationId == evaluationId);
    }

    public async Task<EvaluationModel?> Get(string evaluationName, int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (string.IsNullOrEmpty(evaluationName))
            return null;
        var qry = db.Evaluation.AsNoTracking().Where(e => e.Name == evaluationName && e.WorkspaceId == workspaceId);
        return await qry.FirstOrDefaultAsync();
    }

    public async Task<List<EvaluationModel>> List(int workspaceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var qry = db.Evaluation.OrderBy(item => item.Name).AsNoTracking();
        qry = qry.Where(e => e.WorkspaceId == workspaceId);
        var data = await qry.ToListAsync();
        return data;
    }

    public async Task Delete(int evaluationId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var evaluation = await db.Evaluation.FirstOrDefaultAsync(item => item.EvaluationId == evaluationId);
        if (evaluation == null) return;
        db.Evaluation.Remove(evaluation);
        await db.SaveChangesAsync();
    }

    public async Task<EvaluationModel> Add(EvaluationModel evaluationModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (evaluationModel.EvaluationId != 0) 
            throw new ArgumentException("Evaluation identifier must be zero");

        await db.Evaluation.AddAsync(evaluationModel);
        await db.SaveChangesAsync();

        return evaluationModel;
    }

    public async Task<EvaluationModel> Update(EvaluationModel evaluationModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (evaluationModel.EvaluationId == 0) 
            throw new ArgumentException("Evaluation identifier mustn't be zero");

        var existingEvaluation = await db.Evaluation.FirstAsync(item => item.EvaluationId == evaluationModel.EvaluationId);
        db.Entry(existingEvaluation).CurrentValues.SetValues(evaluationModel);
        db.Evaluation.Update(existingEvaluation);
        await db.SaveChangesAsync();
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