using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class ClientSsoProcessor : IClientSsoProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public ClientSsoProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ClientSsoModel?> Get(int clientSsoId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientSso.Include(e => e.Groups).AsNoTracking().FirstOrDefaultAsync(e => e.ClientSsoId == clientSsoId);
    }

    public async Task<List<ClientSsoModel>> List()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientSso.Include(e => e.Groups).AsNoTracking().ToListAsync();
    }

    public async Task Delete(int clientSsoId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var clientSso = await db.ClientSso.Include(e => e.Groups).FirstOrDefaultAsync(item => item.ClientSsoId == clientSsoId);
        if (clientSso == null) return;
        db.ClientSso.Remove(clientSso);
        await db.SaveChangesAsync();
    }

    public async Task<ClientSsoModel> Add(ClientSsoModel clientSsoModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (clientSsoModel.ClientSsoId != 0) throw new ArgumentException("Sso Client identifier must be zero");

        if (clientSsoModel.Groups != null && clientSsoModel.Groups.Count > 0)
        {
            var gIds = clientSsoModel.Groups.Select(e => e.GroupId);
            var groups = gIds.Any()
            ? await db.Groups.Where(e => gIds.Contains(e.GroupId)).ToListAsync()
            : new List<GroupModel>();

            clientSsoModel.Groups = groups.ToList();
        }

        await db.ClientSso.AddAsync(clientSsoModel);
        await db.SaveChangesAsync();

        return clientSsoModel;
    }

    public async Task<ClientSsoModel> Update(ClientSsoModel clientSsoModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (clientSsoModel.ClientSsoId == 0) throw new ArgumentException("Sso Client identifier mustn't be zero");

        var existingClientSso = await db.ClientSso.Include(e => e.Groups).FirstAsync(item => item.ClientSsoId == clientSsoModel.ClientSsoId);

        var gIds = clientSsoModel.Groups.Select(e => e.GroupId);

        var groups = gIds.Any()
            ? await db.Groups.Where(e => gIds.Contains(e.GroupId)).ToListAsync()
            : [];

        db.Entry(existingClientSso).CurrentValues.SetValues(clientSsoModel);

        existingClientSso.Groups = groups;

        db.ClientSso.Update(existingClientSso);
        await db.SaveChangesAsync();
        return existingClientSso;
    }
}

public interface IClientSsoProcessor
{
    Task<ClientSsoModel?> Get(int clientSsoId);
    Task<List<ClientSsoModel>> List();
    Task Delete(int clientSsoId);
    Task<ClientSsoModel> Add(ClientSsoModel clientSsoModel);
    Task<ClientSsoModel> Update(ClientSsoModel clientSsoModel);
}