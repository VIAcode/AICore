using System.Text.Json;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Models.ViewModels;
using Microsoft.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Extensions;
using ConnectionType = AiCoreApi.Models.DbModels.ConnectionType;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using Microsoft.KernelMemory.AI;

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
            public const string TopK = "topK";
            public const string Evaluation = "evaluation";
        }

        private static class ConnectionContentParameters
        {
            public const string MaxRequestTokens = "maxRequestTokens";
        }

        private static class Actions
        {
            public const string Answer = "answer";
            public const string Search = "search";
            public const string Sync = "sync";
            public const string Feedback = "feedback";
        }

        private static class Placeholders
        {
            public const string Facts = "{{$facts}}";
            public const string Question = "{{$input}}";
            public const string Answer = "{{answer}}";
            public const string Feedback = "{{feedback}}";
        }


        private static class Constants
        {
            public const double PromptTemperature = 0.5;
            public const double PrompTopP = 1;
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IFeatureFlags _featureFlags;
        private readonly ExtendedConfig _extendedConfig;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ISemanticKernelProvider _semanticKernelProvider;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IBingSearchAgent _bingSearchAgent;
        private readonly IEvaluationProcessor _evaluationProcessor;

        public KnowledgeBaseAgent(
            IBaseAgentHelper baseAgentHelper,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IKernelMemoryProvider kernelMemoryProvider,
            IConnectionProcessor connectionProcessor,
            IIngestionProcessor ingestionProcessor,
            ILoginProcessor loginProcessor,
            IFeatureFlags featureFlags,
            ExtendedConfig extendedConfig,
            ITaskProcessor taskProcessor,
            ISemanticKernelProvider semanticKernelProvider,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IBingSearchAgent bingSearchAgent,
            IEvaluationProcessor evaluationProcessor,
            ILogger<RagPromptAgent> logger) : base(baseAgentHelper, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _kernelMemoryProvider = kernelMemoryProvider;
            _connectionProcessor = connectionProcessor;
            _ingestionProcessor = ingestionProcessor;
            _loginProcessor = loginProcessor;
            _featureFlags = featureFlags;
            _extendedConfig = extendedConfig;
            _taskProcessor = taskProcessor;
            _documentMetadataProcessor = documentMetadataProcessor;
            _bingSearchAgent = bingSearchAgent;
            _evaluationProcessor = evaluationProcessor;
            _semanticKernelProvider = semanticKernelProvider;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var action = agent.Content.ContainsKey(AgentContentParameters.Action)
                ? agent.Content[AgentContentParameters.Action].Value.ToLower()
                : Actions.Answer;

            return action switch
            {
                Actions.Search => await SearchAsync(agent, parameters),
                Actions.Sync => await SyncAsync(),
                Actions.Feedback => await SyncFeedback(agent, parameters),

                _ => await AnswerAsync(agent, parameters)
            };
        }

        private async Task<string> SyncFeedback(AgentModel agent, Dictionary<string, string> parameters)
        {
            var answer = await GetParameterValueAsync(AgentContentParameters.Answer);
            var feedback = await GetParameterValueAsync(AgentContentParameters.Feedback);
            var dataSource = await GetParameterValueAsync(AgentContentParameters.DataSource);
            var ingestion = await GetIngestionModel(dataSource);

            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags) ? agent.Content[AgentContentParameters.Tags].Value : "";
            var minRelevance = Convert.ToDouble(agent.Content.ContainsKey(AgentContentParameters.MinRelevance) ? agent.Content[AgentContentParameters.MinRelevance].Value : "0");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Answer:\r\n{answer}\r\n\r\n# Feedback:\r\n{feedback}\r\n\r\n# MinRelevance: {minRelevance}");

            var (kernelMemory, vectorIndexName, filters) = await PrepareKernelMemory(ingestion, tags, agent);

            var llmConnection = await GetLlmConnection(agent);

            var autoSyncOnFeedback = (await GetParameterValueAsync(AgentContentParameters.AutoSyncOnFeedback)).ToLower() == "true";
            var useBingToEnrichFeedback = (await GetParameterValueAsync(AgentContentParameters.UseBingToEnrichFeedback)).ToLower() == "true";
            var changePrompt = await GetParameterValueAsync(AgentContentParameters.ChangePrompt);
            var searchPrompt = (await GetParameterValueAsync(AgentContentParameters.SearchPrompt))
                .Replace(Placeholders.Answer, answer)
                .Replace(Placeholders.Feedback, feedback);
            var limit = await GetParameterValueAsync(AgentContentParameters.Limit);
            var searchString = await _semanticKernelProvider.ExecutePrompt(llmConnection, searchPrompt, Constants.PromptTemperature, Constants.PrompTopP, "");

            var searchResults = await kernelMemory.SearchAsync(searchString, minRelevance: minRelevance,
                index: vectorIndexName, filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);

            if (searchResults.NoResult)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", "Nothing to update");
                return "";
            }
            var summarizePrompt = (await GetParameterValueAsync(AgentContentParameters.SummarizePrompt))
                .Replace(Placeholders.Answer, answer)
                .Replace(Placeholders.Feedback, feedback);
            feedback = await _semanticKernelProvider.ExecutePrompt(llmConnection, summarizePrompt, Constants.PromptTemperature, Constants.PrompTopP, "");
            if (useBingToEnrichFeedback)
            {
                feedback = await EnrichWithBing(llmConnection, agent, parameters, feedback, searchString);
            }
            var loginId = await _requestAccessor.UserContext.GetLoginIdAsync();
            if (!int.TryParse(limit, out var limitInt))
            {
                throw new AiCoreUiException("Invalid limit parameter. Please provide a valid integer value for the limit.");
            }
            var documentIds = searchResults.Results.Select(r => r.DocumentId).Take(limitInt).ToList();
            var evaluationName = agent.Content.ContainsKey(AgentContentParameters.Evaluation)
                   && !string.IsNullOrEmpty(agent.Content[AgentContentParameters.Evaluation].Value)
                   && agent.Content[AgentContentParameters.Evaluation].Value.ToLower() != "none"
                ? agent.Content[AgentContentParameters.Evaluation].Value
                : "0";
            var evaluation = await _evaluationProcessor.Get(evaluationName, agent.WorkspaceId ?? 0);
            if (evaluation == null && evaluationName != "0")
            {
                throw new AiCoreUiException($"No evaluation found with name: {evaluationName}");
            }
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
                    { "changePrompt", changePrompt ?? ""},
                    { "evaluationId", evaluation?.EvaluationId ?? 0},
                },
                IsRetriable = true,
            };
            await _taskProcessor.ScheduleTask(task);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", "Feedback task scheduled for ingestion: " + ingestion.Name
                + "\r\n" + $"Documents to update: {string.Join(", ", documentIds)}, LLM Connection: {llmConnection.Name}, AutoSync: {autoSyncOnFeedback}, Feedback: {feedback}");
            return "Feedback task scheduled successfully for ingestion: " + ingestion.Name;
        }

        private async Task<string> SyncAsync()
        {
            var dataSource = await GetParameterValueAsync(AgentContentParameters.DataSource);
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
                var bingConnectionName = await GetParameterValueAsync(AgentContentParameters.BingConnectionName);
                var bingResultsLimit = await GetParameterValueAsync(AgentContentParameters.BingResultsLimit);
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
                feedback = await _semanticKernelProvider.ExecutePrompt(llmConnection, summarizeWebPrompt, Constants.PromptTemperature, Constants.PrompTopP, "");

                return feedback;
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Bing Enrichment Error", ex.Message);
                return feedback; // return original feedback if Bing enrichment fails
            }
        }
        private async Task<IngestionModel> GetIngestionModel(string ingestionName)
        {
            var ingestions = await _ingestionProcessor.List(_requestAccessor.WorkspaceId);
            var ingestion = ingestions.FirstOrDefault(i => i.Name.Equals(ingestionName, StringComparison.OrdinalIgnoreCase));
            if (ingestion == null)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", $"No ingestion found with name: {ingestionName}");
                throw new AiCoreUiException($"No ingestion found with name: {ingestionName}");
            }
            return ingestion;
        }

        private async Task<string> AnswerAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var llmConnection = await GetLlmConnection(agent);

            var searchResults = await DoSearchAsync(agent);
            _responseAccessor.CurrentMessage.Sources = MapSources(searchResults);

            var resultText = string.Join($"{Environment.NewLine}{Environment.NewLine}", searchResults.SelectMany(r => r.Partitions.Select(p => p.Text).ToList()));
            var question = await GetParameterValueAsync(AgentContentParameters.Question);
            var prompt = (await GetParameterValueAsync(AgentContentParameters.Prompt))
                .Replace(Placeholders.Question, question);

            var prompTokens = new O200KTokenizer().CountTokens(prompt); // Default Tokenizer for gpt-4o-* models
            if (llmConnection.Content.ContainsKey(ConnectionContentParameters.MaxRequestTokens) && Int32.TryParse(llmConnection.Content[ConnectionContentParameters.MaxRequestTokens], out var maxRequestTokens))
            {
                var requestTokensCount = new O200KTokenizer().CountTokens(resultText);
                if (requestTokensCount > maxRequestTokens - 4000 - prompTokens)
                {
                    // take the first maxRequestTokens - 4000 tokens
                    var tokens = new O200KTokenizer().GetTokens(resultText);
                    var tokensToTake = maxRequestTokens - 4000 - prompTokens;
                    var tokensToTakeList = tokens.Take(tokensToTake).ToList();
                    var tokensToTakeString = string.Join(" ", tokensToTakeList);
                    resultText = tokensToTakeString;
                }
            }
            prompt = prompt.Replace(Placeholders.Facts, resultText);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Answer Search Prompt", prompt);

            if (searchResults.Count == 0)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _extendedConfig.NoInformationFoundText);
                return _extendedConfig.NoInformationFoundText;
            }


            var result = await _semanticKernelProvider.ExecutePrompt(llmConnection, prompt, Constants.PromptTemperature, Constants.PrompTopP, "");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", resultText);
            return result;
        }

        private async Task<string> SearchAsync(AgentModel agent, Dictionary<string, string> parameters)
        {
            var searchResults = await DoSearchAsync(agent);
            var resultText = searchResults.ToJson() ?? "";
            _responseAccessor.CurrentMessage.Sources = searchResults.Select(r => new MessageDialogViewModel.MessageSource
            {
                Name = r.SourceName,
                Url = r.SourceUrl
            }).ToList();

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", resultText);
            return resultText;
        }

        private async Task<List<SearchResult>> DoSearchAsync(AgentModel agent)
        {
            var question = await GetParameterValueAsync(AgentContentParameters.Question);
            var dataSource = await GetParameterValueAsync(AgentContentParameters.DataSource);
            var topK = await GetParameterValueAsync(AgentContentParameters.TopK, "10");
            var ingestion = await GetIngestionModel(dataSource);

            var tags = agent.Content.ContainsKey(AgentContentParameters.Tags)
                ? agent.Content[AgentContentParameters.Tags].Value
                : "";
            var minRelevance = Convert.ToDouble(
                agent.Content.ContainsKey(AgentContentParameters.MinRelevance)
                    ? agent.Content[AgentContentParameters.MinRelevance].Value
                    : "0");

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", $"# Search Question: {question}\r\n# Tags: {tags}\r\n# MinRelevance: {minRelevance}");

            var (kernelMemory, vectorIndexName, filters) = await PrepareKernelMemory(ingestion, tags, agent);

            var searchResults = await kernelMemory.SearchAsync(question, minRelevance: minRelevance,
                index: vectorIndexName,
                filters: _featureFlags.IsEnabled(FeatureFlags.Names.Tagging) ? filters : null);

            if (searchResults.Results.Count == 0)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", _extendedConfig.NoInformationFoundText);
                return new List<SearchResult>();
            }

            var topKValue = int.TryParse(topK, out var topKInt) ? topKInt : 10;
            var result = searchResults.Results.Select(r => new SearchResult
            {
                SourceName = r.SourceName,
                SourceUrl = r.SourceUrl ?? "",
                DocumentId = r.DocumentId,
                FileId = r.FileId,
                Partitions = r.Partitions.Select(p => new SearchResultPartition
                {
                    Text = p.Text,
                    Relevance = p.Relevance,
                    SectionNumber = p.SectionNumber,
                    LastUpdate = p.LastUpdate,
                    Tags = p.Tags.Select(t => new SearchResultPartitionKeyValuePair
                    {
                        Key = t.Key,
                        Value = string.Join(",", t.Value)
                    }).ToList()
                }).ToList()
            }).Take(Convert.ToInt32(topKValue))
            .ToList();

            return result;
        }

        private async Task<(IKernelMemory kernelMemory, string vectorIndexName, List<MemoryFilter> filters)> PrepareKernelMemory(IngestionModel ingestionModel, string tags, AgentModel agent)
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
                : await GetConnectionAsync(_requestAccessor, _responseAccessor, connections, ConnectionType.AzureAiSearch, _debugMessageSenderName, connectionId: Convert.ToInt32(vectorDbConnectionId));

            var embeddingConnection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections,
                new[] { ConnectionType.AzureOpenAiEmbedding, ConnectionType.OpenAiEmbedding }, _debugMessageSenderName, connectionId: Convert.ToInt32(embeddingConnectionId));

            var llmConnection = await GetLlmConnection(agent, connections);

            var vectorIndexName = embeddingConnection.Content.ContainsKey("indexName")
                ? embeddingConnection.Content["indexName"]
                : "default";

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);

            return (kernelMemory, vectorIndexName, filters);
        }

        private async Task<ConnectionModel> GetLlmConnection(AgentModel agent, List<ConnectionModel>? connections = null)
        {
            connections ??= await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var llmConnection = await GetConnectionAsync(_requestAccessor, _responseAccessor, connections,
                new[]
                {
                    ConnectionType.AzureOpenAiLlm, 
                    ConnectionType.OpenAiLlm,
                    ConnectionType.DeepSeekLlm,
                    ConnectionType.CohereLlm,
                    ConnectionType.GeminiLlm
                }, _debugMessageSenderName, agent.LlmType);
            return llmConnection;
        }

        private List<MessageDialogViewModel.MessageSource> MapSources(List<SearchResult>? searchResults)
        {
            var documentIds = searchResults?.Select(r => r.DocumentId).Distinct().ToList();
            var documents = _documentMetadataProcessor.Get(documentIds);

            return searchResults.Select(s =>
                {
                    var documentMetadata = documents.FirstOrDefault(x => x.DocumentId == s.DocumentId);
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
                        Name = documentMetadata.Name ?? "",
                        Url = documentMetadata.Url
                    };
                })
                .GroupBy(x => $"{x.Url}|{x.Name}")
                .Select(x => x.First())
                .ToList();
        }
    }

    public class SearchResult
    {
        public string SourceName { get; set; } = "";
        public string SourceUrl { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string FileId { get; set; } = "";
        public List<SearchResultPartition> Partitions { get; set; } = new();
    }

    public class SearchResultPartition
    {
        public string Text { get; set; } = "";
        public double Relevance { get; set; } = 0;
        public int SectionNumber { get; set; } = 0;
        public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.UtcNow;
        public List<SearchResultPartitionKeyValuePair> Tags { get; set; } = new();
    }

    public class SearchResultPartitionKeyValuePair
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public interface IKnowledgeBaseAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
