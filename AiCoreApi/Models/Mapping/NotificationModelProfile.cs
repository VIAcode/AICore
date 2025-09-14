using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;

namespace AiCoreApi.Models.Mapping
{
    public class NotificationModelProfile : Profile
    {
        public NotificationModelProfile()
        {
            CreateMap<NotificationViewModel, NotificationModel>();
            CreateMap<NotificationModel, NotificationViewModel>();
        }
    }
}