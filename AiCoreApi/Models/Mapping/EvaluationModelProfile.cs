using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping
{
    public class EvaluationModelProfile : Profile
    {
        public EvaluationModelProfile()
        {
            CreateMap<EvaluationQuestionModel, EvaluationQuestionViewModel>().ReverseMap();
            CreateMap<EvaluationViewModel, EvaluationModel>()
                .ForMember(
                    dst => dst.Questions,
                    opt => opt.MapFrom(src => src.Questions));

            CreateMap<EvaluationModel, EvaluationViewModel>()
                .ForMember(
                    dst => dst.Questions,
                    opt => opt.MapFrom(src => src.Questions));
        }
    }
}