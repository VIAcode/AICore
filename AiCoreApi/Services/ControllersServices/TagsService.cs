using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;

namespace AiCoreApi.Services.ControllersServices
{
    public class TagsService : ITagsService
    {
        private readonly IMapper _mapper;
        private readonly ILoginProcessor _loginProcessor;
        private readonly ITagsProcessor _tagsProcessor; 

        public TagsService(
            ILoginProcessor loginProcessor,
            ITagsProcessor tagsProcessor, 
            IMapper mapper)
        {
            _loginProcessor = loginProcessor;
            _tagsProcessor = tagsProcessor;
            _mapper = mapper;
        }

        public TagViewModel? GetTag(int tagId)
        {
            var tag = _tagsProcessor.Get(tagId);

            if (tag == null) return null;

            return _mapper.Map<TagViewModel>(tag);
        }

        public async Task<TagViewModel> AddOrUpdateTag(TagViewModel tagViewModel)
        {
            var tagModel = _mapper.Map<TagModel>(tagViewModel);
            var savedModel = await _tagsProcessor.Set(tagModel);
            var result = _mapper.Map<TagViewModel>(savedModel);
            return result;
        }

        public List<TagViewModel> ListTags()
        {
            var tags = _tagsProcessor.List();
            var tagsViewModelList = _mapper.Map<List<TagViewModel>>(tags);
            return tagsViewModelList;
        }

        public async Task<List<TagViewModel>> ListUserTags(string login, LoginTypeEnum loginType)
        {
            var tags = await _loginProcessor.GetTagsByLogin(login, loginType);
            var tagsViewModelList = _mapper.Map<List<TagViewModel>>(tags);
            return tagsViewModelList;
        }

        public async Task<bool> RemoveTag(int tagId)
        {
            var result = await _tagsProcessor.Remove(tagId);
            return result;
        }
    }

    public interface ITagsService
    {
        TagViewModel? GetTag(int tagId);
        Task<TagViewModel> AddOrUpdateTag(TagViewModel tagViewModel);
        List<TagViewModel> ListTags();
        Task<List<TagViewModel>> ListUserTags(string login, LoginTypeEnum loginType);
        Task<bool> RemoveTag(int tagId);
    }
}

