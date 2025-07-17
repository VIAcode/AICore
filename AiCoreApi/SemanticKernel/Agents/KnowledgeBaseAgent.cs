using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Models.ViewModels;
using Microsoft.KernelMemory;
using AiCoreApi.Data.Processors;
using System.Web;
using AiCoreApi.Common.Extensions;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;
using AiCoreApi.Common.Monitoring;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
namespace AiCoreApi.SemanticKernel.Agents
{
    public class KnowledgeBaseAgent : BaseAgent, IKnowledgeBaseAgent
    {
        private string _debugMessageSenderName = "KnowledgeBaseAgent";

        private static class AgentContentParameters
        {
            public const string Question = "question";
            public const string Prompt = "prompt";
            public const string Tags = "tags";
            public const string MinRelevance = "minRelevance";
            public const string DataSource = "dataSource";
            public const string Action = "action";

            public const string Answer = "answer";
            public const string Feedback = "feedback";

            public const string SearchPrompt = "searchPrompt";
            public const string ChangePrompt = "changePrompt"; 
            public const string SummarizePrompt = "summarizePrompt";
            public const string Limit = "limit"; 
            public const string AutoSyncOnFeedback = "autoSyncOnFeedback";
            public const string UseBingToEnrichFeedback = "useBingToEnrichFeedback";
            public const string BingConnectionName = "bingConnectionName";
            public const string BingResultsLimit = "bingResultsLimit";
        }

        private static class Actions
        {
            public const string Answer = "answer";
            public const string Search = "search";
            public const string Sync = "sync";
            public const string Feedback = "feedback";
        }

        private static class Constants
        {
            public const double PromptTemperature = 0.5;
            public const double PromptMaxTokens = 1;
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IFeatureFlags _featureFlags;
        private readonly ExtendedConfig _extendedConfig;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IBingSearchAgent _bingSearchAgent;

        public KnowledgeBaseAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IKernelMemoryProvider kernelMemoryProvider,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IConnectionProcessor connectionProcessor,
            IIngestionProcessor ingestionProcessor,
            ILoginProcessor loginProcessor,
            IFeatureFlags featureFlags,
            ExtendedConfig extendedConfig,
            MonitoringConfig monitoringConfig,
            ITaskProcessor taskProcessor,
            ISemanticKernelProvider semanticKernelProvider, 
            IBingSearchAgent bingSearchAgent,
            ILogger<RagPromptAgent> logger) : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _kernelMemoryProvider = kernelMemoryProvider;
            _documentMetadataProcessor = documentMetadataProcessor;
            _connectionProcessor = connectionProcessor;
            _ingestionProcessor = ingestionProcessor;
            _loginProcessor = loginProcessor;
            _featureFlags = featureFlags;
            _extendedConfig = extendedConfig;
            _taskProcessor = taskProcessor;
            _bingSearchAgent = bingSearchAgent;
            _semanticKernelProvider = semanticKernelProvider;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var action = agent.Content.ContainsKey(AgentContentParameters.Action)
                ? agent.Content[AgentContentParameters.Action].Value.ToLower()
                : Actions.Answer;

            return action switch
            {
                Actions.Search => await SearchAsync(agent, parameters),
                Actions.Sync => await SyncAsync(agent, parameters),
                Actions.Feedback => await SyncFeedback(agent, parameters),

                _ => await AnswerAsync(agent, parameters)
            };
        }

        private async Task<string> SyncFeedback(AgentModel agent, Dictionary<string, string> parameters)
        {
            var answer = ApplyParameters(agent.Content[AgentContentParameters.Answer].Value, parameters);
            var feedback = ApplyParameters(agent.Content[AgentContentParameters.Feedback].Value, parameters);
            var dataSource = ApplyParameters(agent.Content[AgentContentParameters.DataSource].Value, parameters);
            var ingestion = await GetIngestionModel(dataSource);

            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags) ? agent.Content[AgentContentParameters.Tags].Value : "";
            var minRelevance = Convert.ToDouble(agent.Content.ContainsKey(AgentContentParameters.MinRelevance) ? agent.Content[AgentContentParameters.MinRelevance].Value : "0");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Answer:\r\n{answer}\r\n\r\n# Feedback:\r\n{feedback}\r\n\r\n# MinRelevance: {minRelevance}");

            var (kernelMemory, vectorIndexName, filters) = await PrepareKernelMemory(ingestion, tags);

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName, agent.LlmType);

            var autoSyncOnFeedback = ApplyParameters(agent.Content[AgentContentParameters.AutoSyncOnFeedback].Value, parameters).ToLower() == "true";
            var useBingToEnrichFeedback = ApplyParameters(agent.Content[AgentContentParameters.UseBingToEnrichFeedback].Value, parameters).ToLower() == "true";
            var changePrompt = ApplyParameters(agent.Content[AgentContentParameters.ChangePrompt].Value, parameters);
            var searchPrompt = ApplyParameters(agent.Content[AgentContentParameters.SearchPrompt].Value, parameters)
                .Replace("{{answer}}", answer)
                .Replace("{{feedback}}", feedback);
            var limit = ApplyParameters(agent.Content[AgentContentParameters.Limit].Value, parameters);
            var searchString = await _semanticKernelProvider.ExecutePrompt(llmConnection, searchPrompt, Constants.PromptTemperature, Constants.PromptMaxTokens, "");

            var searchResults = await kernelMemory.SearchAsync(searchString, minRelevance: minRelevance,
                index: vectorIndexName, filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);

            if (searchResults.NoResult)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", "Nothing to update");
                return "";
            }
            var summarizePrompt = ApplyParameters(agent.Content[AgentContentParameters.SummarizePrompt].Value, parameters)
                .Replace("{{answer}}", answer)
                .Replace("{{feedback}}", feedback);
            feedback = await _semanticKernelProvider.ExecutePrompt(llmConnection, summarizePrompt, Constants.PromptTemperature, Constants.PromptMaxTokens, "");
            if (useBingToEnrichFeedback)
            {
                feedback = await EnrichWithBing(llmConnection, agent, parameters, feedback, searchString);
            }
            var loginId = await _requestAccessor.UserContext.GetLoginIdAsync();
            var documentIds = searchResults.Results.Select(r => r.DocumentId).Take(Convert.ToInt32(limit)).ToList();
            var task = new TaskModel
            {
                IngestionId = ingestion.IngestionId,
                Ingestion = null,
                Type = TaskType.Feedback,
                CreatedBy = _requestAccessor.Login ?? "",
                Context = new Dictionary<string, object>
                {
                    { "feedback", feedback },
                    { "documentIds", string.Join(",", documentIds) },
                    { "llmConnectionId", llmConnection.ConnectionId },
                    { "workspaceId", agent.WorkspaceId ?? 0},
                    { "loginId", loginId ?? 1},
                    { "autoSyncOnFeedback", autoSyncOnFeedback},
                    { "changePrompt", changePrompt },

                },
                IsRetriable = true,
            };
            await _taskProcessor.ScheduleTask(task);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", "Feedback task scheduled for ingestion: " + ingestion.Name
                + "\r\n" + $"Documents to update: {string.Join(", ", documentIds)}, LLM Connection: {llmConnection.Name}, AutoSync: {autoSyncOnFeedback}, Feedback: {feedback}");
            return "Feedback task scheduled successfully for ingestion: " + ingestion.Name;
        }

        private async Task<string> SyncAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var dataSource = ApplyParameters(agent.Content[AgentContentParameters.DataSource].Value, parameters);
            var ingestion = await GetIngestionModel(dataSource);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Syncing ingestion: {ingestion.Name}");
            var task = new TaskModel
            {
                IngestionId = ingestion.IngestionId,
                Ingestion = null,
                Type = TaskType.DataSync,
                CreatedBy = _requestAccessor.Login ?? "",
                IsRetriable = true,
            };
            await _taskProcessor.ScheduleTask(task);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"Sync task scheduled for ingestion: {ingestion.Name}");
            return "Sync task scheduled successfully for ingestion: " + ingestion.Name;
        }

        private async Task<string> EnrichWithBing(ConnectionModel llmConnection, AgentModel agent, Dictionary<string, string> parameters, string feedback, string searchString)
        {
            try
            {
                var bingConnectionName = ApplyParameters(agent.Content[AgentContentParameters.BingConnectionName].Value, parameters);
                var bingResultsLimit = ApplyParameters(agent.Content[AgentContentParameters.BingResultsLimit].Value, parameters);
                var bingAgentModel = new AgentModel
                {
                    Name = "FeedbackEnrichmentBing",
                    Type = Models.DbModels.AgentType.BingSearch,
                    Content = new Dictionary<string, ConfigurableSetting>
                    {
                        { "queryString", new ConfigurableSetting { Value = searchString } },
                        { "bingConnection", new ConfigurableSetting { Value = bingConnectionName } },
                        { "count", new ConfigurableSetting { Value = bingResultsLimit } },
                        { "outputType", new ConfigurableSetting { Value = "snippetTexts" } }
                    }
                };
                var bingResultJson = await _bingSearchAgent.DoCall(bingAgentModel, new Dictionary<string, string>());
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Bing Enrichment Result", bingResultJson);
                var bingSnippets = JsonSerializer.Deserialize<List<string>>(bingResultJson) ?? new List<string>();
                var web = "";
                if (bingSnippets.Count > 0)
                {
                    web += "\n\nAdditional context from web:\n" + string.Join("\n", bingSnippets.Take(Convert.ToInt32(bingResultsLimit)));
                }
                var summarizeWebPrompt = "Enrich the feedback with additional context from the web:\n\n" +
                                         "Feedback: " + feedback + "\n\n" +
                                         "Web context: " + web + "\n\n\n\n" +
                                         "Enrich the feedback with the web context if context is useful. Return only feedback as plain text.";
                feedback = await _semanticKernelProvider.ExecutePrompt(llmConnection, summarizeWebPrompt, Constants.PromptTemperature, Constants.PromptMaxTokens, "");

                return feedback;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Bing Enrichment Error", ex.Message);
                return feedback; // return original feedback if Bing enrichment fails
            }
        }

        private async Task<string> AnswerAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var question = ApplyParameters(agent.Content[AgentContentParameters.Question].Value, parameters);

            var dataSource = ApplyParameters(agent.Content[AgentContentParameters.DataSource].Value, parameters);
            var ingestion = await GetIngestionModel(dataSource);

            var prompt = agent.Content[AgentContentParameters.Prompt].Value;
            _responseAccessor.StepState = prompt;
            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags)
                ? agent.Content[AgentContentParameters.Tags].Value
                : "";
            var minRelevance = Convert.ToDouble(ApplyParameters(agent.Content[AgentContentParameters.MinRelevance].Value, parameters));

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Question: {question}\r\n# Prompt: {prompt}\r\n# Tags: {tags}\r\n# MinRelevance: {minRelevance}");

            var (kernelMemory, vectorIndexName, filters) = await PrepareKernelMemory(ingestion, tags);

            if (filters.Count == 0 && _featureFlags.IsEnabled(FeatureFlags.Names.Tagging))
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"{_extendedConfig.NoInformationFoundText} (filters)");
                return _extendedConfig.NoInformationFoundText;
            }

            var answer = await kernelMemory.AskAsync(question, minRelevance: minRelevance,
                index: vectorIndexName,
                filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);

            if (answer.NoResult)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response",
                    _extendedConfig.NoInformationFoundText);
                return _extendedConfig.NoInformationFoundText;
            }

            _responseAccessor.CurrentMessage.Text = answer.Result;
            _responseAccessor.CurrentMessage.Sources = MapSources(answer);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response",
                _responseAccessor.CurrentMessage.Text);
            return _responseAccessor.CurrentMessage.Text;
        }

        private async Task<IngestionModel> GetIngestionModel(string ingestionName)
        {
            var ingestions = await _ingestionProcessor.List(_requestAccessor.WorkspaceId);
            var ingestion = ingestions.FirstOrDefault(i => i.Name.Equals(ingestionName, StringComparison.OrdinalIgnoreCase));
            if(ingestion == null)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"No ingestion found with name: {ingestionName}");
                throw new AiCoreUiException($"No ingestion found with name: {ingestionName}");
            }
            return ingestion;
        }

        private async Task<string> SearchAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var question = ApplyParameters(agent.Content[AgentContentParameters.Question].Value, parameters);
            var dataSource = ApplyParameters(agent.Content[AgentContentParameters.DataSource].Value, parameters);
            var ingestion = await GetIngestionModel(dataSource);

            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags)
                ? agent.Content[AgentContentParameters.Tags].Value
                : "";
            var minRelevance = Convert.ToDouble(
                agent.Content.ContainsKey(AgentContentParameters.MinRelevance)
                    ? agent.Content[AgentContentParameters.MinRelevance].Value
                    : "0");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request",
                $"# Search Question: {question}\r\n# Tags: {tags}\r\n# MinRelevance: {minRelevance}");

            var (kernelMemory, vectorIndexName, filters) = await PrepareKernelMemory(ingestion, tags);

            var searchResults = await kernelMemory.SearchAsync(question, minRelevance: minRelevance,
                index: vectorIndexName,
                filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);

            if (searchResults.Results.Count == 0)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _extendedConfig.NoInformationFoundText);
                return "[]";
            }

            var result = searchResults.Results.Select(r => new
            {
                r.SourceName,
                r.DocumentId,
                r.FileId,
                Partitions = r.Partitions.Select(p => new
                {
                    p.Text,
                    p.Relevance,
                    p.SectionNumber,
                    p.LastUpdate,
                    Tags = p.Tags.Select(t => new
                    {
                        t.Key,
                        t.Value
                    }).ToList()
                }).ToList()
            }).ToList();

            var resultText = result.ToJson();
            _responseAccessor.CurrentMessage.Sources = searchResults.Results.Select(r => new MessageDialogViewModel.MessageSource
            {
                Name = r.SourceName,
                Url = r.SourceUrl
            }).ToList();

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", resultText);
            return resultText;
        }

        private async Task<(IKernelMemory kernelMemory, string vectorIndexName, List<MemoryFilter> filters)> PrepareKernelMemory(IngestionModel ingestionModel, string tags)
        {
            var allUserTags = await _loginProcessor.GetTagsByLogin(_requestAccessor.Login, _requestAccessor.LoginType);
            var agentTags = string.IsNullOrEmpty(tags)
                ? allUserTags.Where(e => _requestAccessor.Tags.Contains(e.TagId)).Select(e => e.Name.ToLower()).ToArray()
                : tags.ToLower().Split(',');

            var filters = allUserTags
                .Where(e => (string.IsNullOrEmpty(tags) && _requestAccessor.Tags.Count == 0) || agentTags.Contains(e.Name.ToLower()))
                .Select(e => MemoryFilters.ByTag(AiCoreConstants.TagName, e.Name))
                .ToList();

            var embeddingConnectionId = ingestionModel.Content.ContainsKey("EmbeddingConnection")
                ? ingestionModel.Content["EmbeddingConnection"]
                : throw new AiCoreUiException("Embedding connection name is required in the ingestion model.");

            var vectorDbConnectionId = ingestionModel.Content.ContainsKey("VectorDBConnectionName")
                ? ingestionModel.Content["VectorDBConnectionName"]
                : "";
           
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null
                : GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiSearch, _debugMessageSenderName, connectionId: Convert.ToInt32(vectorDbConnectionId));

            var embeddingConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, _debugMessageSenderName, connectionId: Convert.ToInt32(embeddingConnectionId));

            var llmConnection = GetConnection(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiLlm, ConnectionType.OpenAiLlm, ConnectionType.CohereLlm }, _debugMessageSenderName);

            var vectorIndexName = embeddingConnection.Content.ContainsKey("indexName")
                ? embeddingConnection.Content["indexName"]
                : "default";

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);

            return (kernelMemory, vectorIndexName, filters);
        }

        private List<MessageDialogViewModel.MessageSource> MapSources(MemoryAnswer answer)
        {
            return answer.RelevantSources.Select(s =>
            {
                var documentMetadata = _documentMetadataProcessor.Get(s.DocumentId);
                if (documentMetadata == null)
                {
                    return new MessageDialogViewModel.MessageSource
                    {
                        Name = s.SourceUrl,
                        Url = s.SourceUrl
                    };
                }

                return new MessageDialogViewModel.MessageSource
                {
                    Name = documentMetadata.Name,
                    Url = documentMetadata.Url
                };
            })
            .GroupBy(x => $"{x.Url}|{x.Name}")
            .Select(x => x.First())
            .ToList();
        }
    }

    public interface IKnowledgeBaseAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
