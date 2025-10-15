using AiCoreApi.Common.Data;
using AiCoreApi.Models.DbModels;
using Microsoft.EntityFrameworkCore;

namespace AiCoreApi.Data.Processors
{
    public class NotificationsProcessor : INotificationsProcessor
    {
        private readonly IDbContextFactory<Db> _dbFactory;

        public NotificationsProcessor(IDbContextFactory<Db> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<List<NotificationModel>> ListUnread(int workspaceId, string login)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var qry = db.Notification.OrderByDescending(item => item.NotificationId).AsNoTracking();
            qry = qry.Where(e => e.WorkspaceId == workspaceId && e.IsRead == false && login == e.User);
            var data = await qry.ToListAsync();
            return data;
        }

        public async Task<NotificationModel> Add(NotificationModel notification)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            notification.IsRead = false;
            notification.CreatedAt = DateTime.UtcNow;
            await db.Notification.AddAsync(notification);
            await db.SaveChangesAsync();
            return notification;
        }

        public async Task<NotificationModel> Update(NotificationModel notification)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existingNotification = await db.Notification.FirstOrDefaultAsync(item => item.NotificationId == notification.NotificationId);
            if (existingNotification == null)
            {
                throw new ArgumentException("Notification not found");
            }
            db.Entry(existingNotification).CurrentValues.SetValues(notification);
            db.Notification.Update(existingNotification);
            await db.SaveChangesAsync();
            return existingNotification;
        }

        public async Task MarkAsRead(int notificationId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var notification = await db.Notification.FirstOrDefaultAsync(item => item.NotificationId == notificationId);
            if (notification != null)
            {
                notification.IsRead = true;
                db.Notification.Update(notification);
                await db.SaveChangesAsync();
            }
        }

        public async Task MarkAllAsRead(int workspaceId, string login)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var notifications = await db.Notification
                .Where(e => e.WorkspaceId == workspaceId && e.IsRead == false && login == e.User)
                .ToListAsync();
            foreach (var notification in notifications)
            {
                notification.IsRead = true;
            }
            db.Notification.UpdateRange(notifications);
            await db.SaveChangesAsync();
        }
    }

    public interface INotificationsProcessor
    {
        Task<List<NotificationModel>> ListUnread(int workspaceId, string login);
        Task<NotificationModel> Add(NotificationModel notification);
        Task<NotificationModel> Update(NotificationModel notification);
        Task MarkAsRead(int notificationId);
        Task MarkAllAsRead(int workspaceId, string login);
    }
}