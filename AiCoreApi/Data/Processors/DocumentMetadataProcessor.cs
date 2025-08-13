using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class DocumentMetadataProcessor : IDocumentMetadataProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public DocumentMetadataProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public DocumentMetadataModel? Get(string documentId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.DocumentMetadata.AsNoTracking().FirstOrDefault(item => item.DocumentId == documentId);
    }

    public async Task<List<DocumentMetadataModel>> Get(List<string>? documentIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (documentIds == null || !documentIds.Any())
            return new List<DocumentMetadataModel>();
        return await db.DocumentMetadata.AsNoTracking()
            .Where(item => documentIds.Contains(item.DocumentId))
            .ToListAsync();
    }

    public async Task<List<DocumentMetadataModel>> GetByIngestion(int ingestionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DocumentMetadata.AsNoTracking().Where(t => t.IngestionId == ingestionId).ToListAsync();
    }

    public async Task Set(DocumentMetadataModel model)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        model.LastMetadataUpdateTime = DateTime.UtcNow;
        var entity = db.DocumentMetadata.Local.FirstOrDefault(item => item.DocumentId == model.DocumentId)
            ?? db.DocumentMetadata.FirstOrDefault(item => item.DocumentId == model.DocumentId);        

        if (entity == null)
            await db.DocumentMetadata.AddAsync(model);
        else
            db.Entry(entity).CurrentValues.SetValues(model);

        await db.SaveChangesAsync();
    }

    public async Task Remove(DocumentMetadataModel documentMetadataModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.DocumentMetadata.Remove(documentMetadataModel);
        await db.SaveChangesAsync();
    }
}

public interface IDocumentMetadataProcessor
{
    DocumentMetadataModel? Get(string documentId);
    Task<List<DocumentMetadataModel>> Get(List<string>? documentIds);
    Task<List<DocumentMetadataModel>> GetByIngestion(int ingestionId);
    Task Set(DocumentMetadataModel ingestionTaskModel);
    Task Remove(DocumentMetadataModel documentMetadataModel);
}