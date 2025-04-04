using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.SemanticKernel.Agents;
using AgentType = AiCoreApi.Models.DbModels.AgentType;

namespace AiCoreApi.SemanticKernel
{
    public class PlannerHelpers : IPlannerHelpers
    {
        public const string AssistantName = "assistant";

        public static class PlannerPromptPlaceholders
        {
            public const string CurrentQuestionPlaceholder = "{{currentQuestion}}";
            public const string PluginsInstructionsPlaceholder = "{{pluginsInstructions}}";
            public const string HasFilesPlaceholder = "{{hasFiles}}";
            public const string FilesNamesPlaceholder = "{{filesNames}}";
            public const string FilesDataPlaceholder = "{{filesData}}";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPromptAgent _promptAgent;
        private readonly IApiCallAgent _apiCallAgent;
        private readonly IJsonTransformAgent _jsonTransformAgent;
        private readonly IContainsAgent _containsAgent;
        private readonly IBingSearchAgent _bingSearchAgent;
        private readonly IHistoryAgent _historyAgent;
        private readonly IRagPromptAgent _ragPromptAgent;
        private readonly IOcrAgent _ocrAgent;
        private readonly IOcrClassifyDocumentAgent _ocrClassifyDocumentAgent;
        private readonly IBackgroundWorkerAgent _backgroundWorkerAgent;
        private readonly IContentSafetyAgent _contentSafetyAgent;
        private readonly IImageToTextAgent _imageToTextAgent;
        private readonly IWhisperAgent _whisperAgent;
        private readonly IVectorSearchAgent _vectorSearchAgent;
        private readonly IStorageAccountAgent _storageAccountAgent;
        private readonly IPostgreSqlAgent _postgreSqlAgent;
        private readonly ISqlServerAgent _sqlServerAgent;
        private readonly IRedisAgent _redisAgent;
        private readonly IAzureAiTranslatorAgent _azureAiTranslatorAgent;
        private readonly IAzureAiSpeechCreateSpeechAgent _azureAiSpeechCreateSpeechAgent;
        private readonly IAzureAiSearchAgent _azureAiSearchAgent;
        private readonly IAzureServiceBusNotificationAgent _azureServiceBusNotificationAgent;
        private readonly IRabbitMqNotificationAgent _rabbitMqNotificationAgent;
        private readonly IAudioPromptAgent _audioPromptAgent;
        private readonly IWebCrawlerAgent _webCrawlerAgent;
        private readonly IStabilityAiImagesAgent _stabilityAiImagesAgent;
        private readonly IOcrBuildClassifierAgent _ocrBuildClassifierAgent;
        private readonly IAzureLogAnalyticsAgent _azureLogAnalyticsAgent;

        public PlannerHelpers(
            RequestAccessor requestAccessor,
            IAgentsProcessor agentsProcessor,
            IServiceProvider serviceProvider,
            IPromptAgent promptAgent,
            IApiCallAgent apiCallAgent,
            IJsonTransformAgent jsonTransformAgent,
            IContainsAgent containsAgent,
            IBingSearchAgent bingSearchAgent,
            IHistoryAgent historyAgent,
            IRagPromptAgent ragPromptAgent,
            IOcrAgent ocrAgent,
            IOcrClassifyDocumentAgent ocrClassifyDocumentAgent,
            IBackgroundWorkerAgent backgroundWorkerAgent,
            IContentSafetyAgent contentSafetyAgent,
            IImageToTextAgent imageToTextAgent,
            IWhisperAgent whisperAgent,
            IVectorSearchAgent vectorSearchAgent,
            IStorageAccountAgent storageAccountAgent,
            IPostgreSqlAgent postgreSqlAgent,
            ISqlServerAgent sqlServerAgent,
            IRedisAgent redisAgent,
            IAzureAiTranslatorAgent azureAiTranslatorAgent,
            IAzureAiSpeechCreateSpeechAgent azureAiSpeechCreateSpeechAgent,
            IAzureAiSearchAgent azureAiSearchAgent,
            IAzureServiceBusNotificationAgent azureServiceBusNotificationAgent,
            IRabbitMqNotificationAgent rabbitMqNotificationAgent,
            IAudioPromptAgent audioPromptAgent,
            IWebCrawlerAgent webCrawlerAgent,
            IStabilityAiImagesAgent stabilityAiImagesAgent,
            IOcrBuildClassifierAgent ocrBuildClassifierAgent,
            IAzureLogAnalyticsAgent azureLogAnalyticsAgent
            )
        {
            _requestAccessor = requestAccessor;
            _agentsProcessor = agentsProcessor;
            _serviceProvider = serviceProvider;
            _promptAgent = promptAgent;
            _apiCallAgent = apiCallAgent;
            _jsonTransformAgent = jsonTransformAgent;
            _containsAgent = containsAgent;
            _bingSearchAgent = bingSearchAgent;
            _historyAgent = historyAgent;
            _ragPromptAgent = ragPromptAgent;
            _ocrAgent = ocrAgent;
            _ocrClassifyDocumentAgent = ocrClassifyDocumentAgent;
            _backgroundWorkerAgent = backgroundWorkerAgent;
            _contentSafetyAgent = contentSafetyAgent;
            _imageToTextAgent = imageToTextAgent;
            _whisperAgent = whisperAgent;
            _vectorSearchAgent = vectorSearchAgent;
            _storageAccountAgent = storageAccountAgent;
            _postgreSqlAgent = postgreSqlAgent;
            _sqlServerAgent = sqlServerAgent;
            _redisAgent = redisAgent;
            _azureAiTranslatorAgent = azureAiTranslatorAgent;
            _azureAiSpeechCreateSpeechAgent = azureAiSpeechCreateSpeechAgent;
            _azureAiSearchAgent = azureAiSearchAgent;
            _azureServiceBusNotificationAgent = azureServiceBusNotificationAgent;
            _rabbitMqNotificationAgent = rabbitMqNotificationAgent;
            _audioPromptAgent = audioPromptAgent;
            _webCrawlerAgent = webCrawlerAgent;
            _stabilityAiImagesAgent = stabilityAiImagesAgent;
            _ocrBuildClassifierAgent = ocrBuildClassifierAgent;
            _azureLogAnalyticsAgent = azureLogAnalyticsAgent;
        }

        private List<AgentModel>? _agentsList;
        public async Task<List<AgentModel>> GetAgentsList()
        {
            if(_agentsList != null)
                return _agentsList;

            return await _agentsProcessor.List();
        }

        public async Task<string> ExecuteAgent(string agentName, List<string>? parameters = null)
        {
            var dbAgents = await _agentsProcessor.List();
            var agent = dbAgents.FirstOrDefault(item => item.Name.ToLower() == agentName.ToLower());
            if (agent == null)
                throw new Exception($"Agent not found: {agentName}");
            var parametersDictionary = parameters
                .Select((value, i) => new KeyValuePair<string, string>("parameter" + (i + 1), value))
                .ToDictionary(
                    key => key.Key,
                    value => value.Value);

            var agentTypes = GetAgentTypes();
            if (!agentTypes.TryGetValue(agent.Type, out var agentType))
                throw new Exception($"Agent type not found: {agent.Type}");
            var agentInstance = ((BaseAgent)agentType);
            var result = await agentInstance.DoCall(agent, parametersDictionary);
            return result;
        }

        private ICompositeAgent? _compositeAgent;

        public ICompositeAgent CompositeAgent
        {
            get
            {
                if (_compositeAgent == null)
                    _compositeAgent = (ICompositeAgent)_serviceProvider.GetService(typeof(ICompositeAgent));
                return _compositeAgent;
            }
            set
            {
                _compositeAgent = value;
            }
        }

        private ICsharpCodeAgent? _csharpCodeAgent;
        public ICsharpCodeAgent CsharpCodeAgent
        {
            get
            {
                if (_csharpCodeAgent == null)
                    _csharpCodeAgent = (ICsharpCodeAgent)_serviceProvider.GetService(typeof(ICsharpCodeAgent));
                return _csharpCodeAgent;
            }
            set
            {
                _csharpCodeAgent = value;
            }
        }

        private IPythonCodeAgent? _pythonCodeAgent;
        public IPythonCodeAgent PythonCodeAgent
        {
            get
            {
                if (_pythonCodeAgent == null)
                    _pythonCodeAgent = (IPythonCodeAgent)_serviceProvider.GetService(typeof(IPythonCodeAgent));
                return _pythonCodeAgent;
            }
            set
            {
                _pythonCodeAgent = value;
            }
        }

        public async Task AddPlugin(AgentModel agent, Kernel kernel, List<string> pluginsInstructions)
        {
            var agentTypes = GetAgentTypes();
            var agentMapping = new Dictionary<AgentType, Func<Task>>();
            foreach (var agentType in agentTypes)
            {
                agentMapping.Add(agentType.Key, () => ((BaseAgent)agentType.Value).AddAgent(agent, kernel, pluginsInstructions));
            }

            if (agentMapping.TryGetValue(agent.Type, out var addAgentTask))
            {
                await addAgentTask();
            }
        }

        public Dictionary<AgentType, object> GetAgentTypes()
        {
            var agentMapping = new Dictionary<AgentType, object>
            {
                { AgentType.Prompt, _promptAgent },
                { AgentType.ApiCall, _apiCallAgent },
                { AgentType.JsonTransform, _jsonTransformAgent },
                { AgentType.Contains, _containsAgent },
                { AgentType.Composite, CompositeAgent},
                { AgentType.PythonCode, PythonCodeAgent },
                { AgentType.CsharpCode, CsharpCodeAgent },
                { AgentType.BingSearch, _bingSearchAgent },
                { AgentType.History, _historyAgent },
                { AgentType.RagPrompt, _ragPromptAgent },
                { AgentType.Ocr, _ocrAgent },
                { AgentType.OcrClassifyDocument, _ocrClassifyDocumentAgent },
                { AgentType.BackgroundWorker, _backgroundWorkerAgent },
                { AgentType.ContentSafety, _contentSafetyAgent },
                { AgentType.ImageToText, _imageToTextAgent },
                { AgentType.Whisper, _whisperAgent },
                { AgentType.VectorSearch, _vectorSearchAgent },
                { AgentType.StorageAccount, _storageAccountAgent },
                { AgentType.PostgreSql, _postgreSqlAgent },
                { AgentType.SqlServer, _sqlServerAgent },
                { AgentType.Redis, _redisAgent },
                { AgentType.AzureAiTranslator, _azureAiTranslatorAgent },
                { AgentType.AzureAiSpeechCreateSpeech, _azureAiSpeechCreateSpeechAgent },
                { AgentType.AzureAiSearch, _azureAiSearchAgent },
                { AgentType.AzureServiceBusNotification, _azureServiceBusNotificationAgent },
                { AgentType.RabbitMqNotification, _rabbitMqNotificationAgent },
                { AgentType.AudioPromptAgent, _audioPromptAgent },
                { AgentType.WebCrawler, _webCrawlerAgent },
                { AgentType.StabilityAiImages, _stabilityAiImagesAgent },
                { AgentType.OcrBuildClassifierAgent, _ocrBuildClassifierAgent },
                { AgentType.AzureLogAnalytics, _azureLogAnalyticsAgent },
            };
            return agentMapping;
        }

        public string ApplyPlaceholders(string plannerPrompt) => plannerPrompt
            .Replace(PlannerPromptPlaceholders.CurrentQuestionPlaceholder, _requestAccessor.MessageDialog!.GetQuestion())
            .Replace(PlannerPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().HasFiles().ToString())
            .Replace(PlannerPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().GetFileNames())
            .Replace(PlannerPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().GetFileContents());

        public string GetPlannerCacheKey(string plannerPrompt, Kernel kernel)
        {
            var pluginNames = string.Join(",", kernel.Plugins.Select(p => p.Name));
            return $"planner_{plannerPrompt.GetHash()}_{pluginNames.GetHash()}";
        }
    }

    public interface IPlannerHelpers
    {
        Task<List<AgentModel>> GetAgentsList();
        Task<string> ExecuteAgent(string agentName, List<string>? parameters = null);
        Task AddPlugin(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        string ApplyPlaceholders(string plannerPrompt);
        string GetPlannerCacheKey(string plannerPrompt, Kernel kernel);
        ICompositeAgent CompositeAgent { get; set; }
        ICsharpCodeAgent CsharpCodeAgent { get; set; }
        IPythonCodeAgent PythonCodeAgent { get; set; }
    }
}
