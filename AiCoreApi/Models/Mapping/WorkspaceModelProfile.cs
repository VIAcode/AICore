using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping
{
    public class WorkspaceModelProfile : Profile
    {
        public WorkspaceModelProfile()
        {
            CreateMap<WorkspaceViewModel, WorkspaceModel>();
            CreateMap<WorkspaceModel, WorkspaceViewModel>();
        }
    }
}