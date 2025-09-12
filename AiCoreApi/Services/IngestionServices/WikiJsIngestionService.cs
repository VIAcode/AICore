using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Text;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using System.Security.Cryptography;
using AiCoreApi.Common;

namespace AiCoreApi.Services.IngestionServices
{
    public class WikiJsIngestionService : IWikiJsIngestionService
    {
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILogger<WikiJsIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;

        private const int DelayBeforeReUploadMilliseconds = 5000;

        public WikiJsIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<WikiJsIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            IKernelMemoryProvider kernelMemoryProvider)
        {
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
            _kernelMemoryProvider = kernelMemoryProvider;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            await _taskProcessor.SetMessage(taskId, "Initializing Wiki.js ingestion");
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            if (!ingestion.Content.TryGetValue("ConnectionName", out var connectionNameValue))
            {
                _logger.LogError("ConnectionName key is missing in ingestion.Content.");
                throw new KeyNotFoundException("The 'ConnectionName' key is required but was not found in ingestion.Content.");
            }

            var connections = await _connectionProcessor.List(ingestion.WorkspaceId);
            var wikiJsConnection = await GetConnection(ingestion, Convert.ToInt32(connectionNameValue), connections);
            var llmConnection = connections.FirstOrDefault(x => x.Type.IsLlmConnection());
            var vectorDbConnectionId = ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.VectorDbConnectionField) ? ingestion.Content[DataIngestionHelperService.Constants.VectorDbConnectionField] : "";

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null
                : connections.FirstOrDefault(x => x.ConnectionId.ToString() == vectorDbConnectionId);

            if (llmConnection == null)
            {
                _logger.LogError("No LLM connection found in workspace. Please configure an LLM connection before running ingestion.");
                throw new InvalidOperationException("No LLM connection found in workspace. Please configure an LLM connection before running ingestion.");
            }

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);
            var baseUrl = wikiJsConnection.Content["baseUrl"];
            var apiToken = wikiJsConnection.Content["apiToken"];
            var locale = wikiJsConnection.Content.ContainsKey("locale") ? wikiJsConnection.Content["locale"] : "en";

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var pages = await GetAllPages(client, baseUrl, locale);
            var pageDocIds = new HashSet<string>();

            var i = 0;
            foreach (var page in pages)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Processing page '{page.Title}' [{i}/{pages.Count}]");

                var pageContent = await GetPageContent(client, baseUrl, page.Id, locale);
                var contentHash = ComputeHash(pageContent);
                var docId = ($"{page.Id}/{page.Path}").UniqueId();
                pageDocIds.Add(docId);

                var fileInDatabase = _documentMetadataProcessor.Get(docId);
                if (fileInDatabase != null &&
                    fileInDatabase.Url?.Split("#")?.LastOrDefault() == contentHash)
                {
                    var search = await kernelMemory.SearchAsync("",
                        embeddingConnectionModel.IndexName,
                        filter: new Microsoft.KernelMemory.MemoryFilter().ByDocument(docId));
                    if (search.Results.Count > 0) // If the file already exists in the vector DB, skip re-upload
                        continue;
                }

                var file = fileInDatabase ?? new DocumentMetadataModel(docId)
                {
                    IngestionId = ingestion.IngestionId,
                    CreatedTime = DateTime.UtcNow,
                };
                file.Url = $"{baseUrl}/{page.Path}#{contentHash}";
                file.Name = $"{page.Title}.md";
                await _documentMetadataProcessor.Set(file);

                // Remove previous version before re-uploading
                if (fileInDatabase != null)
                {
                    await _fileIngestionClient.Delete(embeddingConnectionModel, docId);
                    await Task.Delay(DelayBeforeReUploadMilliseconds);
                }

                if (!string.IsNullOrEmpty(pageContent))
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(pageContent));
                    await _fileIngestionClient.Upload(embeddingConnectionModel, docId, $"{page.Title}.md",
                        stream, ingestion.Tags.ToTagDictionary(), translateStepModel);

                    _logger.LogInformation($"Page {page.Title} uploaded successfully.");
                }
                else
                {
                    _logger.LogInformation($"Page {page.Title} is empty.");
                }

                file.ImportFinished = true;
                await _documentMetadataProcessor.Set(file);
            }

            await RemoveDeletedFiles(embeddingConnectionModel, ingestion, pageDocIds, taskId);

            await _taskProcessor.SetMessage(taskId, "Completed");
        }

        private async Task<ConnectionModel> GetConnection(IngestionModel ingestion, int connectionId, List<ConnectionModel>? connections = null)
        {
            connections ??= await _connectionProcessor.List(ingestion.WorkspaceId);
            var connection = connections.FirstOrDefault(c => c.ConnectionId == Convert.ToInt32(connectionId) && c.Type == ConnectionType.WikiJs)
                             ?? throw new InvalidOperationException($"Connection '{connectionId}' not found.");
            return connection;
        }

        public async Task<string> GetFileByPath(IngestionModel ingestion, string path)
        {
            var connection = await GetConnection(ingestion, Convert.ToInt32(ingestion.Content["ConnectionName"]));
            var baseUrl = connection.Content["baseUrl"];
            var apiToken = connection.Content["apiToken"];
            var locale = connection.Content.ContainsKey("locale") ? connection.Content["locale"] : "en";

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var pageId = await GetPageIdByPath(client, baseUrl, path, locale);
            return await GetPageContent(client, baseUrl, pageId, locale);
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            try
            {
                var metadata = _documentMetadataProcessor.Get(fileId) ?? throw new InvalidOperationException($"File with id '{fileId}' not found in metadata.");
                var urlWithoutHash = metadata.Url?.Split('#')[0];
                return await GetFileByPath(ingestion, urlWithoutHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file '{fileId}' from Wiki.js.");
                throw;
            }
        }

        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            try
            {
                var connection = await GetConnection(ingestion, Convert.ToInt32(ingestion.Content["ConnectionName"]));
                var baseUrl = connection.Content["baseUrl"];
                var apiToken = connection.Content["apiToken"];
                var locale = connection.Content.ContainsKey("locale") ? connection.Content["locale"] : "en";

                var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File '{fileId}' not found.");

                var pageId = await GetPageIdByPath(client, baseUrl, metadata.Url, locale);
                await UpdatePageContent(client, baseUrl, pageId, articleText, locale);

                _logger.LogInformation($"Updated page '{metadata.Name}' ({pageId}).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set file '{fileId}' in Wiki.js.");
                throw;
            }
        }

        private async Task<string> GetPageIdByPath(HttpClient client, string baseUrl, string url, string locale)
        {
            // Extract path from URL and search for the page using GraphQL
            var path = url.Split('#')[0].Replace(baseUrl, "").TrimStart('/');
            
            // Get all pages and filter by path since Wiki.js doesn't support path filtering in list query
            var query = new
            {
                query = @"
                    query {
                        pages {
                            list (orderBy: TITLE) {
                                id
                                path
                                title
                            }
                        }
                    }"
            };

            var jsonBody = new StringContent(JsonConvert.SerializeObject(query),
                Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{baseUrl}/graphql", jsonBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<WikiJsGraphQLResult<WikiJsPagesListResult>>(json);
            
            if (result?.Data?.Pages?.List?.Any() == true)
            {
                var page = result.Data.Pages.List.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                return page?.Id ?? throw new InvalidOperationException($"Page with path '{path}' not found.");
            }

            throw new InvalidOperationException($"Page with path '{path}' not found.");
        }

        private async Task<List<WikiJsPage>> GetAllPages(HttpClient client, string baseUrl, string locale)
        {
            var list = new List<WikiJsPage>();
            
            var query = new
            {
                query = @"
                    query {
                        pages {
                            list (orderBy: TITLE) {
                                id
                                path
                                title
                            }
                        }
                    }"
            };

            var jsonBody = new StringContent(JsonConvert.SerializeObject(query),
                Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{baseUrl}/graphql", jsonBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<WikiJsGraphQLResult<WikiJsPagesListResult>>(json);

            if (result?.Data?.Pages?.List != null)
            {
                list.AddRange(result.Data.Pages.List);
            }

            return list;
        }

        private async Task<string> GetPageContent(HttpClient client, string baseUrl, string pageId, string locale)
        {
            var query = new
            {
                query = @"
                    query($id: Int!) {
                        pages {
                            single (id: $id) {
                                path
                                title
                                createdAt
                                updatedAt
                                content
                                contentType
                            }
                        }
                    }",
                variables = new { id = Convert.ToInt32(pageId) }
            };

            var jsonBody = new StringContent(JsonConvert.SerializeObject(query),
                Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{baseUrl}/graphql", jsonBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<WikiJsGraphQLResult<WikiJsPageContentResult>>(json);
            return result?.Data?.Pages?.Single?.Content ?? string.Empty;
        }

        private async Task UpdatePageContent(HttpClient client, string baseUrl, string pageId, string content, string locale)
        {
            // First get the current page info to get the required fields
            var getPageQuery = new
            {
                query = @"
                    query($id: Int!) {
                        pages {
                            single(id: $id) {
                                id
                                title
                                path
                                description
                                isPrivate
                                isPublished
                                locale
                            }
                        }
                    }",
                variables = new { id = Convert.ToInt32(pageId) }
            };

            var getPageJsonBody = new StringContent(JsonConvert.SerializeObject(getPageQuery),
                Encoding.UTF8, "application/json");

            var getPageResponse = await client.PostAsync($"{baseUrl}/graphql", getPageJsonBody);
            getPageResponse.EnsureSuccessStatusCode();

            var getPageJson = await getPageResponse.Content.ReadAsStringAsync();
            var getPageResult = JsonConvert.DeserializeObject<WikiJsGraphQLResult<WikiJsPageInfoResult>>(getPageJson);

            if (getPageResult?.Data?.Pages?.Single == null)
                throw new InvalidOperationException($"Failed to get page info for page {pageId}");

            var pageInfo = getPageResult.Data.Pages.Single;

            // Update the page content using GraphQL mutation
            var updateQuery = new
            {
                query = @"
                    mutation($id: Int!, $title: String!, $path: String!, $content: String!, $description: String!, $isPrivate: Boolean!, $isPublished: Boolean!, $locale: String!, $tags: [String]!) {
                        pages {
                            update(id: $id, title: $title, path: $path, content: $content, description: $description, isPrivate: $isPrivate, isPublished: $isPublished, locale: $locale, tags: $tags) {
                                responseResult {
                                    succeeded
                                    errorCode
                                    slug
                                    message
                                }
                            }
                        }
                    }",
                variables = new
                {
                    id = Convert.ToInt32(pageId),
                    title = pageInfo.Title,
                    path = pageInfo.Path,
                    content = content,
                    description = pageInfo.Description,
                    isPrivate = pageInfo.IsPrivate,
                    isPublished = pageInfo.IsPublished,
                    locale = locale,
                    tags = new string[] { } // Empty tags array as default
                }
            };

            var updateJsonBody = new StringContent(JsonConvert.SerializeObject(updateQuery),
                Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{baseUrl}/graphql", updateJsonBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<WikiJsGraphQLResult<WikiJsUpdateResult>>(json);
            
            if (result?.Data?.Pages?.Update?.ResponseResult?.Succeeded != true)
            {
                throw new InvalidOperationException($"Failed to update page: {result?.Data?.Pages?.Update?.ResponseResult?.Message}");
            }
        }

        private async Task RemoveDeletedFiles(EmbeddingConnectionModel embeddingConnectionModel, IngestionModel ingestion, HashSet<string> currentDocIds, int taskId)
        {
            var filesInDatabase = await _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
            var toRemove = filesInDatabase.Where(f => !currentDocIds.Contains(f.DocumentId)).ToList();

            int i = 0;
            foreach (var file in toRemove)
            {
                i++;
                try
                {
                    await _taskProcessor.SetMessage(taskId, $"Removing deleted file '{file.Name}' [{i}/{toRemove.Count}]");
                    await _fileIngestionClient.Delete(embeddingConnectionModel, file.DocumentId);
                    await _documentMetadataProcessor.Remove(file);
                    _logger.LogInformation($"Deleted file '{file.Name}' successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to delete file '{file.Name}' from memory or metadata.");
                }
            }
        }

        private string ComputeHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            return Convert.ToBase64String(sha256.ComputeHash(bytes));
        }

        // GraphQL Response Models
        public class WikiJsGraphQLResult<T>
        {
            [JsonProperty("data")]
            public T? Data { get; set; }

            [JsonProperty("errors")]
            public List<WikiJsGraphQLError>? Errors { get; set; }
        }

        public class WikiJsGraphQLError
        {
            [JsonProperty("message")]
            public string Message { get; set; } = string.Empty;

            [JsonProperty("locations")]
            public List<WikiJsGraphQLLocation>? Locations { get; set; }
        }

        public class WikiJsGraphQLLocation
        {
            [JsonProperty("line")]
            public int Line { get; set; }

            [JsonProperty("column")]
            public int Column { get; set; }
        }

        public class WikiJsPagesListResult
        {
            [JsonProperty("pages")]
            public WikiJsPagesList Pages { get; set; } = new();
        }

        public class WikiJsPagesList
        {
            [JsonProperty("list")]
            public List<WikiJsPage> List { get; set; } = new();
        }

        public class WikiJsPageContentResult
        {
            [JsonProperty("pages")]
            public WikiJsPageContent Pages { get; set; } = new();
        }

        public class WikiJsPageContent
        {
            [JsonProperty("single")]
            public WikiJsPageContentSingle? Single { get; set; }
        }

        public class WikiJsPageContentSingle
        {
            [JsonProperty("path")]
            public string Path { get; set; } = string.Empty;

            [JsonProperty("title")]
            public string Title { get; set; } = string.Empty;

            [JsonProperty("createdAt")]
            public DateTime CreatedAt { get; set; }

            [JsonProperty("updatedAt")]
            public DateTime UpdatedAt { get; set; }

            [JsonProperty("content")]
            public string Content { get; set; } = string.Empty;

            [JsonProperty("contentType")]
            public string ContentType { get; set; } = string.Empty;
        }

        public class WikiJsPageInfoResult
        {
            [JsonProperty("pages")]
            public WikiJsPageInfo Pages { get; set; } = new();
        }

        public class WikiJsPageInfo
        {
            [JsonProperty("single")]
            public WikiJsPage? Single { get; set; }
        }

        public class WikiJsUpdateResult
        {
            [JsonProperty("pages")]
            public WikiJsUpdate Pages { get; set; } = new();
        }

        public class WikiJsUpdate
        {
            [JsonProperty("update")]
            public WikiJsUpdateResponse Update { get; set; } = new();
        }

        public class WikiJsUpdateResponse
        {
            [JsonProperty("responseResult")]
            public WikiJsResponseResult ResponseResult { get; set; } = new();
        }

        public class WikiJsResponseResult
        {
            [JsonProperty("succeeded")]
            public bool Succeeded { get; set; }

            [JsonProperty("errorCode")]
            public string ErrorCode { get; set; } = string.Empty;

            [JsonProperty("slug")]
            public string Slug { get; set; } = string.Empty;

            [JsonProperty("message")]
            public string Message { get; set; } = string.Empty;
        }

        public class WikiJsPage
        {
            [JsonProperty("id")]
            public string Id { get; set; } = string.Empty;

            [JsonProperty("title")]
            public string Title { get; set; } = string.Empty;

            [JsonProperty("path")]
            public string Path { get; set; } = string.Empty;

            [JsonProperty("description")]
            public string Description { get; set; } = string.Empty;

            [JsonProperty("isPrivate")]
            public bool IsPrivate { get; set; }

            [JsonProperty("isPublished")]
            public bool IsPublished { get; set; }

            [JsonProperty("locale")]
            public string Locale { get; set; } = string.Empty;

            [JsonProperty("createdAt")]
            public DateTime CreatedAt { get; set; }

            [JsonProperty("updatedAt")]
            public DateTime UpdatedAt { get; set; }
        }
    }

    public interface IWikiJsIngestionService : IDataIngestionWorker
    {
    }
}