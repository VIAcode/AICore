using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;
using AiCoreApi.Common;

namespace AiCoreApi.Services.ControllersServices
{
    public class WorkspacesService : IWorkspacesService
    {
        private readonly IMapper _mapper;
        private readonly IWorkspaceProcessor _workspaceProcessor;
        private readonly ILoginProcessor _loginProcessor;

        public WorkspacesService(
            IWorkspaceProcessor workspaceProcessor,
            ILoginProcessor loginProcessor,
            IMapper mapper)
        {
            _workspaceProcessor = workspaceProcessor;
            _loginProcessor = loginProcessor;
            _mapper = mapper;
        }

        public async Task<WorkspaceViewModel?> GetWorkspace(int workspaceId)
        {
            var workspace = await _workspaceProcessor.Get(workspaceId);
            if (workspace == null) 
                return null;
            return _mapper.Map<WorkspaceViewModel>(workspace);
        }

        public async Task<WorkspaceViewModel> AddOrUpdateWorkspace(WorkspaceViewModel workspacesViewModel)
        {
            var workspacesModel = _mapper.Map<WorkspaceModel>(workspacesViewModel);
            var savedModel = await _workspaceProcessor.Set(workspacesModel);
            var result = _mapper.Map<WorkspaceViewModel>(savedModel);
            return result;
        }

        public async Task<List<WorkspaceViewModel>> ListWorkspaces()
        {
            var workspaces = await _workspaceProcessor.ListAsync();
            var workspacesViewModelList = _mapper.Map<List<WorkspaceViewModel>>(workspaces);
            return workspacesViewModelList;
        }

        public async Task<bool> RemoveWorkspace(int workspaceId)
        {
            var result = await _workspaceProcessor.Remove(workspaceId);
            return result;
        }

        public async Task<List<WorkspaceViewModel>> ListUserWorkspaces(string login, LoginTypeEnum loginType)
        {
            var workspaces = await _workspaceProcessor.ListAsync();
            var userTags = await _loginProcessor.GetTagsByLogin(login, loginType);
            workspaces = workspaces
                .Where(workspace => 
                    workspace.Tags.Count == 0 || 
                    workspace.Tags.Any(t => userTags.Any(ut => ut.TagId == t.TagId)))
                .ToList();
            var workspacesViewModelList = _mapper.Map<List<WorkspaceViewModel>>(workspaces);
            return workspacesViewModelList;
        }
    }

    public interface IWorkspacesService
    {
        Task<WorkspaceViewModel?> GetWorkspace(int workspaceId);
        Task<WorkspaceViewModel> AddOrUpdateWorkspace(WorkspaceViewModel workspacesViewModel);
        Task<List<WorkspaceViewModel>> ListWorkspaces();
        Task<bool> RemoveWorkspace(int workspaceId);
        Task<List<WorkspaceViewModel>> ListUserWorkspaces(string login, LoginTypeEnum loginType);
    }
}

