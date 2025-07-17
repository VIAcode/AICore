using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel;

namespace AiCoreApi.Services.IngestionServices
{
    public class FeedbackService : IFeedbackService
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IDataIngestionWorkerFactory _ingestionWorkerFactory;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILoginProcessor _loginProcessor;

        private static class Constants
        {
            public const string LoginId = "loginId";
            public const string WorkspaceId = "workspaceId";
            public const string Answer = "answer";
            public const string Feedback = "feedback";
            public const string ChangePrompt = "changePrompt";
            public const string LlmConnectionId = "llmConnectionId";
            public const string DocumentIds = "documentIds";
            public const string AutoSyncOnFeedback = "autoSyncOnFeedback";
            public const string MessageTitle = "FeedbackService task";
        }

        public FeedbackService(
            IIngestionProcessor ingestionProcessor,
            ISemanticKernelProvider semanticKernelProvider,
            IDataIngestionWorkerFactory ingestionWorkerFactory,
            IServiceProvider serviceProvider,
            IConnectionProcessor connectionProcessor,
            ITaskProcessor taskProcessor,
            ILoginProcessor loginProcessor)
        {
            _ingestionProcessor = ingestionProcessor;
            _semanticKernelProvider = semanticKernelProvider;
            _ingestionWorkerFactory = ingestionWorkerFactory;
            _serviceProvider = serviceProvider;
            _connectionProcessor = connectionProcessor;
            _taskProcessor = taskProcessor;
            _loginProcessor = loginProcessor;
        }

        public async Task Process(int ingestionId, int taskId, string payload)
        {
            var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId)
                ?? throw new InvalidOperationException($"Data source '{ingestionId}' not found.");

            var service = _ingestionWorkerFactory.GetService(ingestion);

            var payloadDictionary = payload.JsonGet<Dictionary<string, string>>();

            var loginId = Convert.ToInt32(payloadDictionary[Constants.LoginId]);
            var workspaceId = Convert.ToInt32(payloadDictionary[Constants.WorkspaceId]);
            var feedback = payloadDictionary[Constants.Feedback];
            var changePrompt = payloadDictionary[Constants.ChangePrompt];
            var llmConnectionId = Convert.ToInt32(payloadDictionary[Constants.LlmConnectionId]);
            var autoSyncOnFeedback = payloadDictionary[Constants.AutoSyncOnFeedback].ToLower() == "true";
            var documentIds = payloadDictionary[Constants.DocumentIds].Split(','); 

            var llmConnection = await _connectionProcessor.GetById(llmConnectionId);
            var runAsUser = await _loginProcessor.GetById(loginId)
                ?? throw new Exception($"FeedbackService: User not found (Ingestion: {ingestionId})");

            await using var scope = _serviceProvider.CreateAsyncScope();

            var userContextAccessor = scope.ServiceProvider.GetRequiredService<UserContextAccessor>();
            var requestAccessor = scope.ServiceProvider.GetRequiredService<RequestAccessor>();

            requestAccessor.MessageDialog = new MessageDialogViewModel
            {
                Messages = new List<MessageDialogViewModel.Message>
                {
                    new() { Text = Constants.MessageTitle, Sender = runAsUser.Login }
                }
            };

            requestAccessor.Login = runAsUser.Login;
            requestAccessor.LoginTypeString = runAsUser.LoginType.ToString();
            requestAccessor.WorkspaceId = workspaceId;

            userContextAccessor.SetLoginId(loginId);
            UserContextAccessor.AsyncScheduledLoginId.Value = loginId;

            foreach (var documentId in documentIds)
            {
                var file = await service.GetFile(ingestion, documentId);

                var prompt = changePrompt
                    .Replace("{{file}}", file)
                    .Replace("{{feedback}}", feedback);

                var newFile = await _semanticKernelProvider.ExecutePrompt(llmConnection, prompt, 0.5, 1, "");
                if (newFile.StartsWith("# [Article]"))
                {
                    newFile = newFile.Remove(0, 11).Trim();
                }
                await service.SetFile(ingestion, documentId, newFile);
            }

            if (autoSyncOnFeedback)
            {
                var task = new TaskModel
                {
                    IngestionId = ingestion.IngestionId,
                    Ingestion = null,
                    Type = TaskType.DataSync,
                    CreatedBy = runAsUser.Login,
                    IsRetriable = true,
                };
                await _taskProcessor.ScheduleTask(task);
            }
        }
    }

    public interface IFeedbackService : IIngestionDataService { }
}
