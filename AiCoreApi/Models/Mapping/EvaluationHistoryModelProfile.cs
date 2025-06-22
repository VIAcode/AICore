using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping
{
    public class EvaluationHistoryModelProfile : Profile
    {
        public EvaluationHistoryModelProfile()
        {
            CreateMap<EvaluationHistoryQuestionModel, EvaluationHistoryQuestionViewModel>().ReverseMap();
            CreateMap<EvaluationHistoryViewModel, EvaluationHistoryModel>()
                .ForMember(
                    dst => dst.Questions,
                    opt => opt.MapFrom(src => src.Questions));

            CreateMap<EvaluationHistoryModel, EvaluationHistoryViewModel>()
                .ForMember(
                    dst => dst.Questions,
                    opt => opt.MapFrom(src => src.Questions));
        }
    }
}