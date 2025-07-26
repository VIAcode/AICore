using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class DocumentMetadataProcessor : IDocumentMetadataProcessor
{
    private readonly Db _db;
    private readonly IDbQuery _dbQuery;

    public DocumentMetadataProcessor(Db db, IDbQuery dbQuery)
    {
        _db = db;
        _dbQuery = dbQuery;
    }

    public DocumentMetadataModel? Get(string documentId)
    {
        return _db.DocumentMetadata.AsNoTracking().FirstOrDefault(item => item.DocumentId == documentId);
    }

    public List<DocumentMetadataModel> Get(List<string>? documentIds)
    {
        if (documentIds == null || !documentIds.Any())
            return new List<DocumentMetadataModel>();
        return _db.DocumentMetadata.AsNoTracking()
            .Where(item => documentIds.Contains(item.DocumentId))
            .ToList();
    }

    public List<DocumentMetadataModel> GetByIngestion(int ingestionId)
    {
        return _db.DocumentMetadata.AsNoTracking().Where(t => t.IngestionId == ingestionId).ToList();
    }

    public async Task Set(DocumentMetadataModel model)
    {
        model.LastMetadataUpdateTime = DateTime.UtcNow;
        var entity = _db.DocumentMetadata.Local.FirstOrDefault(item => item.DocumentId == model.DocumentId)
            ?? _db.DocumentMetadata.FirstOrDefault(item => item.DocumentId == model.DocumentId);        

        if (entity == null)
            await _db.DocumentMetadata.AddAsync(model);
        else
            _db.Entry(entity).CurrentValues.SetValues(model);

        await _db.SaveChangesAsync();
    }

    public async Task Remove(DocumentMetadataModel documentMetadataModel)
    {
        _db.DocumentMetadata.Remove(documentMetadataModel);
        await _db.SaveChangesAsync();
    }
}

public interface IDocumentMetadataProcessor
{
    DocumentMetadataModel? Get(string documentId);
    List<DocumentMetadataModel> Get(List<string>? documentIds);
    List<DocumentMetadataModel> GetByIngestion(int ingestionId);
    Task Set(DocumentMetadataModel ingestionTaskModel);
    Task Remove(DocumentMetadataModel documentMetadataModel);
}