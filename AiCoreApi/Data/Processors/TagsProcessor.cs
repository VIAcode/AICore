using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors;

public class TagsProcessor : ITagsProcessor
{
    private readonly Db _db;

    public TagsProcessor(Db db)
    {
        _db = db;
    }

    public TagModel? Get(int tagId)
    {
        return _db.Tags.AsNoTracking().FirstOrDefault(item => item.TagId == tagId);
    }

    public async Task<TagModel?> Set(TagModel tagModel)
    {
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
            await _db.Tags.AddAsync(tag);
        }
        else
        {
            tag = _db.Tags.FirstOrDefault(item => item.TagId == tagModel.TagId);

            if (tag == null) return null;

            tag.TagId = tagModel.TagId;
            tag.Name = tagModel.Name;
            tag.Description = tagModel.Description;
            tag.Color = tagModel.Color;
            _db.Tags.Update(tag);
        }
        await _db.SaveChangesAsync();
        return tag;
    }

    public List<TagModel> List()
    {
        return _db.Tags.
            Select(item => new TagModel
            {
                TagId = item.TagId,
                Name = item.Name,
                Description = item.Description,
                Created = item.Created,
                CreatedBy = item.CreatedBy,
                Color = item.Color,
            })
            .AsNoTracking().ToList();
    }

    public async Task<bool> Remove(int tagId)
    {
        var tag = await _db.Tags.FirstOrDefaultAsync(item => item.TagId == tagId);
        if (tag != null)
        {
            // Check if the tag is in use in Ingestions, we cannot delete it
            var isInUse = await _db.Tags
                .FromSqlRaw("SELECT * FROM tags_x_ingestions WHERE tags_tag_id = {0}", tagId)
                .AnyAsync();
            if (isInUse)
                return false;

            // Clean up many-to-many relations
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_groups WHERE tags_tag_id = {0}", tagId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_logins WHERE tags_tag_id = {0}", tagId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_rbac_role_sync WHERE tags_tag_id = {0}", tagId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_agents WHERE tags_tag_id = {0}", tagId);
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM tags_x_workspaces WHERE tags_tag_id = {0}", tagId);

            _db.Tags.Remove(tag);
            await _db.SaveChangesAsync();
        }
        return true;
    }
}

public interface ITagsProcessor
{
    TagModel? Get(int tagId);
    Task<TagModel?> Set(TagModel tagModel);
    List<TagModel> List();
    Task<bool> Remove(int tagId);
}