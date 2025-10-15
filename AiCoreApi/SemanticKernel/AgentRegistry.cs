using AiCoreApi.Models.DbModels;
using AiCoreApi.SemanticKernel.Agents;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.SemanticKernel
{
    public interface IAgentRegistry
    {
        BaseAgent Resolve(AgentType type);
    }

    public class AgentRegistry : IAgentRegistry
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AgentRegistry> _logger;

        public AgentRegistry(IServiceProvider serviceProvider, ILogger<AgentRegistry> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public BaseAgent Resolve(AgentType type)
        {
            try
            {
                return type switch
                {
                    AgentType.Prompt => Get<IPromptAgent>(),
                    AgentType.ApiCall => Get<IApiCallAgent>(),
                    AgentType.JsonTransform => Get<IJsonTransformAgent>(),
                    AgentType.Contains => Get<IContainsAgent>(),
                    AgentType.Composite => Get<ICompositeAgent>(),
                    AgentType.CompositeCSharp => Get<ICompositeCSharpAgent>(),
                    AgentType.CompositePython => Get<ICompositePythonAgent>(),
                    AgentType.CompositeLoop => Get<ICompositeLoopAgent>(),
                    AgentType.CsharpCode => Get<ICsharpCodeAgent>(),
                    AgentType.PythonCode => Get<IPythonCodeAgent>(),
                    AgentType.NodeJsCode => Get<INodeJsCodeAgent>(),
                    AgentType.Flow => Get<IFlowAgent>(),
                    AgentType.BingSearch => Get<IBingSearchAgent>(),
                    AgentType.GoogleSearchApi => Get<IGoogleSearchApiAgent>(),
                    AgentType.RagPrompt => Get<IRagPromptAgent>(),
                    AgentType.KnowledgeBase => Get<IKnowledgeBaseAgent>(),
                    AgentType.History => Get<IHistoryAgent>(),
                    AgentType.VectorSearch => Get<IVectorSearchAgent>(),
                    AgentType.Embedding => Get<IEmbeddingAgent>(),
                    AgentType.Qdrant => Get<IQdrantAgent>(),
                    AgentType.OpenSearch => Get<IOpenSearchAgent>(),
                    AgentType.StorageAccount => Get<IStorageAccountAgent>(),
                    AgentType.PostgreSql => Get<IPostgreSqlAgent>(),
                    AgentType.SqlServer => Get<ISqlServerAgent>(),
                    AgentType.Redis => Get<IRedisAgent>(),
                    AgentType.AzureAiSearch => Get<IAzureAiSearchAgent>(),
                    AgentType.AzureAiTranslator => Get<IAzureAiTranslatorAgent>(),
                    AgentType.AzureAiSpeechCreateSpeech => Get<IAzureAiSpeechCreateSpeechAgent>(),
                    AgentType.AzureLogAnalytics => Get<IAzureLogAnalyticsAgent>(),
                    AgentType.AzureServiceBusNotification => Get<IAzureServiceBusNotificationAgent>(),
                    AgentType.RabbitMqNotification => Get<IRabbitMqNotificationAgent>(),
                    AgentType.Smtp => Get<ISmtpNotificationAgent>(),
                    AgentType.GraphTeamsNotification => Get<IGraphTeamsNotificationAgent>(),
                    AgentType.GraphMailNotification => Get<IGraphMailNotificationAgent>(),
                    AgentType.AzDoWiki => Get<IAzDoWikiAgent>(),
                    AgentType.Confluence => Get<IConfluenceAgent>(),
                    AgentType.BackgroundWorker => Get<IBackgroundWorkerAgent>(),
                    AgentType.ContentSafety => Get<IContentSafetyAgent>(),
                    AgentType.ImageToText => Get<IImageToTextAgent>(),
                    AgentType.Whisper => Get<IWhisperAgent>(),
                    AgentType.Ocr => Get<IOcrAgent>(),
                    AgentType.OcrClassifyDocument => Get<IOcrClassifyDocumentAgent>(),
                    AgentType.OcrBuildClassifierAgent => Get<IOcrBuildClassifierAgent>(),
                    AgentType.WebCrawler => Get<IWebCrawlerAgent>(),
                    AgentType.StabilityAiImages => Get<IStabilityAiImagesAgent>(),
                    AgentType.AudioPromptAgent => Get<IAudioPromptAgent>(),
                    AgentType.MemZero => Get<IMemZeroAgent>(),
                    AgentType.Git => Get<IGitAgent>(),
                    AgentType.McpClient => Get<IMcpClientAgent>(),
                    _ => throw new AiCoreUiException($"Unsupported agent type: {type}")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve agent for type {AgentType}", type);
                throw;
            }
        }

        private BaseAgent Get<T>() where T : class
        {
            var instance = _serviceProvider.GetRequiredService<T>();
            if (instance is not BaseAgent baseAgent)
                throw new InvalidCastException($"{typeof(T).Name} is not a BaseAgent");
            return baseAgent;
        }
    }
}
