using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping;

public class AgentModelProfile : Profile
{
    public AgentModelProfile()
    {
        CreateMap<AgentViewModel, AgentModel>();
        CreateMap<AgentModel, AgentViewModel>();

        CreateMap<ConfigurableSetting, ConfigurableSettingView>();
        CreateMap<ConfigurableSettingView, ConfigurableSetting>();

        CreateMap<AgentExportModel, AgentModel>()
            .ForMember(dst => dst.Tags,
            opt => opt.MapFrom(e => e.Tags.Select(tag => new TagModel { Name = tag }).ToList()));
        CreateMap<AgentModel, AgentExportModel>()
            .ForMember(dst => dst.Tags,
                opt => opt.MapFrom(e => e.Tags.Select(tag => tag.Name)));

        CreateMap<ConfigurableSetting, ConfigurableExportSetting>();
        CreateMap<ConfigurableExportSetting, ConfigurableSetting>();
    }
}
