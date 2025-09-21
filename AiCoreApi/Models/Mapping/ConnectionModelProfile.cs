using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping
{
    public class ConnectionModelProfile : Profile
    {
        public ConnectionModelProfile()
        {
            CreateMap<ConnectionViewModel, ConnectionModel>();
            CreateMap<ConnectionModel, ConnectionViewModel>();
            CreateMap<ConnectionModel, ConnectionExportModel>();
            CreateMap<ConnectionExportModel, ConnectionModel>();
        }
    }
}