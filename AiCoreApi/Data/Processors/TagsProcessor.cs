using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class TagsProcessor : ITagsProcessor
{
    private readonly IDbContextFactory<Db> _dbFactory;

    public TagsProcessor(IDbContextFactory<Db> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TagModel?> Get(int tagId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().FirstOrDefaultAsync(item => item.TagId == tagId);
    }

    public async Task<TagModel?> Set(TagModel tagModel)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        TagModel? tag;
        if (tagModel.TagId == 0)
        {
            tag = new TagModel
            {
                TagId = tagModel.TagId,
                Name = tagModel.Name,
                Description = tagModel.Description,
                Created = tagModel.Created,
                CreatedBy = tagModel.CreatedBy,
                Color = tagModel.Color
            };
            await db.Tags.AddAsync(tag);
        }
        else
        {
            tag = db.Tags.FirstOrDefault(item => item.TagId == tagModel.TagId);

            if (tag == null) return null;

            tag.TagId = tagModel.TagId;
            tag.Name = tagModel.Name;
            tag.Description = tagModel.Description;
            tag.Color = tagModel.Color;
            db.Tags.Update(tag);
        }
        await db.SaveChangesAsync();
        return tag;
    }

    public async Task<List<TagModel>> List()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.
            Select(item => new TagModel
            {
                TagId = item.TagId,
                Name = item.Name,
                Description = item.Description,
                Created = item.Created,
                CreatedBy = item.CreatedBy,
                Color = item.Color,
            })
            .AsNoTracking().ToListAsync();
    }

    public async Task<bool> Remove(int tagId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var tag = await db.Tags.FirstOrDefaultAsync(item => item.TagId == tagId);
        if (tag != null)
        {
            // Check if the tag is in use in Ingestions, we cannot delete it
            var isInUse = await db.Tags
                .FromSqlRaw("SELECT * FROM tags_x_ingestions WHERE tags_tag_id = {0}", tagId)
                .AnyAsync();
            if (isInUse)
                return false;

            // Clean up many-to-many relations
            await db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_groups WHERE tags_tag_id = {0}", tagId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_logins WHERE tags_tag_id = {0}", tagId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_rbac_role_sync WHERE tags_tag_id = {0}", tagId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_agents WHERE tags_tag_id = {0}", tagId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_workspaces WHERE tags_tag_id = {0}", tagId);

            db.Tags.Remove(tag);
            await db.SaveChangesAsync();
        }
        return true;
    }
}

public interface ITagsProcessor
{
    Task<TagModel?> Get(int tagId);
    Task<TagModel?> Set(TagModel tagModel);
    Task<List<TagModel>> List();
    Task<bool> Remove(int tagId);
}