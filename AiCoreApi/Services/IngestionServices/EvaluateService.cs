using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.ControllersServices;

namespace AiCoreApi.Services.IngestionServices
{
    public class EvaluateService : IEvaluateService
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDataIngestionWorkerFactory _ingestionWorkerFactory;
        private readonly IEvaluationProcessor _evaluationProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoginProcessor _loginProcessor;
        private readonly INotificationsProcessor _notificationsProcessor;

        private static class Constants
        {
            public const string EvaluationId = "evaluationId";
            public const string ChangedFiles = "changedFiles";
            public const string LoginId = "loginId";
            public const string WorkspaceId = "workspaceId";
        }

        public EvaluateService(
            IIngestionProcessor ingestionProcessor,
            IDataIngestionWorkerFactory ingestionWorkerFactory,
            IEvaluationProcessor evaluationProcessor,
            ITaskProcessor taskProcessor,
            IServiceProvider serviceProvider,
            ILoginProcessor loginProcessor,
            INotificationsProcessor notificationsProcessor
            )
        {
            _ingestionProcessor = ingestionProcessor;
            _ingestionWorkerFactory = ingestionWorkerFactory;
            _evaluationProcessor = evaluationProcessor;
            _taskProcessor = taskProcessor;
            _serviceProvider = serviceProvider;
            _loginProcessor = loginProcessor;
            _notificationsProcessor = notificationsProcessor;
        }

        public async Task Process(int ingestionId, int taskId, string payload)
        {
            var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId)
                            ?? throw new InvalidOperationException($"Data source '{ingestionId}' not found.");

            var service = _ingestionWorkerFactory.GetService(ingestion);

            var payloadDictionary = payload.JsonGet<Dictionary<string, string>>();
            if (payloadDictionary == null ||
                !payloadDictionary.ContainsKey(Constants.EvaluationId) ||
                !payloadDictionary.ContainsKey(Constants.ChangedFiles) ||
                !payloadDictionary.ContainsKey(Constants.WorkspaceId) ||
                !payloadDictionary.ContainsKey(Constants.LoginId))
            {
                throw new ArgumentException("Payload is missing one or more required keys.");
            }

            var evaluationId = Convert.ToInt32(payloadDictionary[Constants.EvaluationId]);
            var changedFiles = payloadDictionary[Constants.ChangedFiles].JsonGet<Dictionary<string, string>>();
            if (changedFiles == null)
            {
                throw new ArgumentException("Failed to deserialize 'changedFiles' from the payload.");
            }
            var workspaceId = Convert.ToInt32(payloadDictionary[Constants.WorkspaceId]);
            var loginId = Convert.ToInt32(payloadDictionary[Constants.LoginId]);

            var evaluation = await _evaluationProcessor.Get(evaluationId) ?? throw new InvalidOperationException($"Evaluation '{evaluationId}' not found.");
            await _taskProcessor.SetMessage(taskId, $"Running evaluation {evaluation.Name}");
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
                var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();
                requestAccessor.MessageDialog = new MessageDialogViewModel
                {
                    Messages = new List<MessageDialogViewModel.Message>
                    {
                        new()
                        {
                            Text = $"",
                            Sender = "User",
                        }
                    }
                };
                // Set user context with all tags for scheduled task
                var login = await _loginProcessor.GetById(loginId);
                if (login == null)
                {
                    var errorMessage = $"Login with ID '{loginId}' not found.";
                    await _taskProcessor.SetMessage(taskId, errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }
                var notification = await _notificationsProcessor.Add(new NotificationModel
                {
                    CreatedAt = DateTime.UtcNow,
                    IsRead = false,
                    Message = $"Evaluation '{evaluation.Name}' is in progress.",
                    Title = "Evaluation In Progress",
                    Type = NotificationTypes.Info,
                    InProgress = true,
                    User = login.Login,
                    WorkspaceId = workspaceId
                });
                requestAccessor.Login = login.Login;
                requestAccessor.LoginTypeString = login.LoginType.ToString();
                requestAccessor.TagsString = string.Join(",", login.Tags.Select(tag => tag.TagId));
                requestAccessor.WorkspaceId = workspaceId;

                userContextAccessor.SetLoginId(loginId);
                UserContextAccessor.AsyncScheduledLoginId.Value = loginId;
                var newScore = await scope.ServiceProvider.GetRequiredService<IEvaluationService>().Run(evaluationId, true);

                if (newScore < evaluation.LastScore)
                {
                    var i = 0;
                    foreach (var changedFile in changedFiles)
                    {
                        i++;
                        await _taskProcessor.SetMessage(taskId, $"Revert file {changedFile.Key} [{i}/{changedFiles.Count}]");
                        await _notificationsProcessor.Update(new NotificationModel
                        {
                            NotificationId = notification.NotificationId,
                            CreatedAt = DateTime.UtcNow,
                            IsRead = false,
                            Message = $"Reverting file {changedFile.Key} [{i}/{changedFiles.Count}]",
                            Title = "Evaluation Revert",
                            Type = NotificationTypes.Info,
                            InProgress = false,
                            User = login.Login,
                            WorkspaceId = workspaceId
                        });
                        await service.SetFile(ingestion, changedFile.Key, changedFile.Value);
                    }
                }
                var notificationMessage = newScore >= evaluation.LastScore
                    ? $"Evaluation '{evaluation.Name}' completed. New score: {newScore}, previous score: {evaluation.LastScore}."
                    : $"Evaluation '{evaluation.Name}' completed. New score: {newScore}, previous score: {evaluation.LastScore}. Changes have been reverted.";
                notificationMessage += $"<br><a href=\"/evaluations/history?evaluationId={evaluationId}\">View Evaluation Details</a>";
                await _notificationsProcessor.Update(new NotificationModel
                {
                    NotificationId = notification.NotificationId,
                    CreatedAt = DateTime.UtcNow,
                    IsRead = false,
                    Message = notificationMessage,
                    Title = "Evaluation Completed",
                    Type = NotificationTypes.Info,
                    InProgress = false,
                    User = login.Login,
                    WorkspaceId = workspaceId
                });
                await _taskProcessor.SetMessage(taskId, $"Completed.");
            }
        }
    }

    public interface IEvaluateService : IIngestionDataService { }
}
