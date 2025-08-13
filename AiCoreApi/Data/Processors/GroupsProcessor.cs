using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class GroupsProcessor : IGroupsProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public GroupsProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<GroupModel?> Get(int groupId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var data = await db.Groups
            .Include(e => e.Tags)
            .Include(e => e.Logins)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.GroupId == groupId);
        return data;
    }

    public async Task<GroupModel?> Get(string groupName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var data = await db.Groups
            .Include(e => e.Tags)
            .Include(e => e.Logins)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == groupName);
        return data;
    }

    public async Task<GroupModel> Set(GroupModel groupModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        GroupModel? group;

        var gId = groupModel.GroupId;
        var tIds = groupModel.Tags.Select(e => e.TagId);
        var uIds = groupModel.Logins.Select(e => e.LoginId);

        var tags = tIds.Any() 
            ? await db.Tags.Where(e => tIds.Contains(e.TagId)).ToListAsync()
            : new List<TagModel>();

        var users = uIds.Any()
            ? await db.Login.Where(e => uIds.Contains(e.LoginId)).ToListAsync()
            : new List<LoginModel>();
        
        if (gId == 0)
        {
            group = new GroupModel
            {
                Name = groupModel.Name,
                Description = groupModel.Description,
                Created = groupModel.Created,
                CreatedBy = groupModel.CreatedBy,
                Tags = tags,
                Logins = users
            };

            await db.Groups.AddAsync(group);
        }
        else
        {
            group = await db.Groups
                .Include(e => e.Tags)
                .Include(e => e.Logins)
                .FirstAsync(e => e.GroupId == gId);

            group.Name = groupModel.Name;
            group.Description = groupModel.Description;
            group.Created = groupModel.Created;
            group.CreatedBy = groupModel.CreatedBy;
            group.Tags = tags;
            group.Logins = users;

            db.Groups.Update(group);
        }

        await db.SaveChangesAsync();
        return group;
    }

    public async Task<List<GroupModel>> List()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var data = await db.Groups.Include(e => e.Tags).AsNoTracking().ToListAsync();
        return data;
    }
}

public interface IGroupsProcessor
{
    Task<GroupModel?> Get(int groupId);
    Task<GroupModel?> Get(string groupName);
    Task<GroupModel> Set(GroupModel groupModel);
    Task<List<GroupModel>> List();
}