using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AutoMapper;
using AiCoreApi.Common;

namespace AiCoreApi.Services.ControllersServices;

public class NotificationsService : INotificationsService
{
    private readonly RequestAccessor _requestAccessor;
    private readonly IMapper _mapper;
    private readonly INotificationsProcessor _notificationsProcessor;

    public NotificationsService(
        RequestAccessor requestAccessor,
        INotificationsProcessor notificationsProcessor, 
        IMapper mapper)
    {
        _requestAccessor = requestAccessor;
        _notificationsProcessor = notificationsProcessor;
        _mapper = mapper;
    }

    public async Task<List<NotificationViewModel>> List()
    {
        var notificationsList = await _notificationsProcessor.ListUnread(_requestAccessor.WorkspaceId ?? 0);
        var notificationsViewModelList = _mapper.Map<List<NotificationViewModel>>(notificationsList);
        return notificationsViewModelList;
    }

    public async Task<bool> MarkAsRead(int notificationId)
    {
        await _notificationsProcessor.MarkAsRead(notificationId);
        return true;
    }

}

public interface INotificationsService
{
    Task<List<NotificationViewModel>> List();
    Task<bool> MarkAsRead(int notificationId);
}