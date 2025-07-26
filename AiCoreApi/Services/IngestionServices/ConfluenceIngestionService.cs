using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Text;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Web;
using AiCoreApi.Common;

namespace AiCoreApi.Services.IngestionServices
{
    public class ConfluenceIngestionService : IConfluenceIngestionService
    {
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILogger<ConfluenceIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;

        private const int DelayBeforeReUploadMilliseconds = 5000;

        public ConfluenceIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<ConfluenceIngestionService> logger,
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
            await _taskProcessor.SetMessage(taskId, "Initializing Confluence ingestion");
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
            var confluenceConnection = await GetConnection(ingestion, Convert.ToInt32(connectionNameValue), connections);
            var llmConnection = connections.FirstOrDefault(x => x.Type.IsLlmConnection()); // Assuming there's a default LLM connection
            var vectorDbConnectionId = ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.VectorDbConnectionField) ? ingestion.Content[DataIngestionHelperService.Constants.VectorDbConnectionField] : "";

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null
                : connections.FirstOrDefault(x => x.ConnectionId.ToString() == vectorDbConnectionId);

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);
            var baseUrl = confluenceConnection.Content["baseUrl"];
            var username = confluenceConnection.Content["username"];
            var apiToken = confluenceConnection.Content["apiToken"];
            var rootPageId = confluenceConnection.Content["rootPageId"];

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            var pages = await GetAllPages(client, baseUrl, rootPageId);
            var pageDocIds = new HashSet<string>();

            var i = 0;
            foreach (var page in pages)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Processing page '{page.Title}' [{i}/{pages.Count}]");

                var pageContent = await GetPageContent(client, baseUrl, page.Id);
                var contentHash = ComputeHash(pageContent);
                var docId = ($"{page.Id}/{page.Title}").UniqueId();
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
                file.Url = $"{baseUrl}/spaces/{page.Expandable.Space.Split('/').Last()}/pages/{page.Id}#{contentHash}";
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
            var connection = connections.FirstOrDefault(c => c.ConnectionId == Convert.ToInt32(connectionId) && c.Type == ConnectionType.Confluence)
                             ?? throw new InvalidOperationException($"Connection '{connectionId}' not found.");
            return connection;
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            try
            {
                var connection = await GetConnection(ingestion, Convert.ToInt32(ingestion.Content["ConnectionName"]));

                var baseUrl = connection.Content["baseUrl"];
                var username = connection.Content["username"];
                var apiToken = connection.Content["apiToken"];

                var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File with id '{fileId}' not found in metadata.");

                var pageId = GetPageIdByUrl(metadata.Url);
                return await GetPageContent(client, baseUrl, pageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file '{fileId}' from Confluence.");
                throw;
            }
        }
        
        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            try
            {
                var connection = await GetConnection(ingestion, Convert.ToInt32(ingestion.Content["ConnectionName"]));

                var baseUrl = connection.Content["baseUrl"];
                var username = connection.Content["username"];
                var apiToken = connection.Content["apiToken"];

                var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File '{fileId}' not found.");

                var pageId = GetPageIdByUrl(metadata.Url);

                await UpdatePageContent(client, baseUrl, pageId, articleText);

                _logger.LogInformation($"Updated page '{metadata.Name}' ({pageId}).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set file '{fileId}' in Confluence.");
                throw;
            }
        }

        private string GetPageIdByUrl(string url)
        {
            var pagesPlaceholder = "/pages/";
            var pageId = url.Substring(url.IndexOf(pagesPlaceholder) + pagesPlaceholder.Length).Split("#").FirstOrDefault()
                         ?? throw new InvalidOperationException("Cannot extract pageId from metadata URL.");
            return pageId;
        }

        private async Task<List<ConfluencePage>> GetAllPages(HttpClient client, string baseUrl, string rootPageId)
        {
            var list = new List<ConfluencePage>();
            var url = $"{baseUrl}/rest/api/content/{HttpUtility.UrlEncode(rootPageId)}/descendant/page?expand=version";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<ConfluenceResult>(json);

            if (data?.Results != null)
            {
                list.AddRange(data.Results);
            }

            var rootJson = await client.GetAsync($"{baseUrl}/rest/api/content/{rootPageId}?expand=version");
            rootJson.EnsureSuccessStatusCode();
            var rootData = JsonConvert.DeserializeObject<ConfluencePage>(await rootJson.Content.ReadAsStringAsync());
            if (rootData != null)
                list.Add(rootData);

            return list;
        }

        private async Task<string> GetPageContent(HttpClient client, string baseUrl, string pageId)
        {
            var url = $"{baseUrl}/rest/api/content/{pageId}?expand=body.storage";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<ConfluencePage>(json);
            return data?.Body?.Storage?.Value ?? string.Empty;
        }

        private async Task UpdatePageContent(HttpClient client, string baseUrl, string pageId, string content)
        {
            var pageInfoJson = await client.GetAsync($"{baseUrl}/rest/api/content/{pageId}?expand=version,title");
            pageInfoJson.EnsureSuccessStatusCode();
            var pageInfo = JsonConvert.DeserializeObject<ConfluencePage>(
                await pageInfoJson.Content.ReadAsStringAsync());

            var updateBody = new
            {
                id = pageId,
                type = "page",
                title = pageInfo.Title,
                version = new { number = pageInfo.Version.Number + 1 },
                body = new
                {
                    storage = new
                    {
                        value = content,
                        representation = "storage"
                    }
                }
            };

            var jsonBody = new StringContent(JsonConvert.SerializeObject(updateBody),
                Encoding.UTF8, "application/json");

            var response = await client.PutAsync($"{baseUrl}/rest/api/content/{pageId}", jsonBody);
            response.EnsureSuccessStatusCode();
        }

        private async Task RemoveDeletedFiles(EmbeddingConnectionModel embeddingConnectionModel, IngestionModel ingestion, HashSet<string> currentDocIds, int taskId)
        {
            var filesInDatabase = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
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

        public class ConfluenceResult
        {
            [JsonProperty("results")]
            public List<ConfluencePage> Results { get; set; } = new();
        }

        public class ConfluencePage
        {
            [JsonProperty("id")]
            public string Id { get; set; } = string.Empty;

            [JsonProperty("title")]
            public string Title { get; set; } = string.Empty;

            [JsonProperty("version")]
            public ConfluenceVersion Version { get; set; } = new();

            [JsonProperty("_expandable")]
            public ConfluenceExpandable Expandable { get; set; } = new();

            [JsonProperty("body")]
            public ConfluenceBody Body { get; set; } = new();
        }

        public class ConfluenceVersion
        {
            [JsonProperty("number")]
            public int Number { get; set; }
        }
        public class ConfluenceExpandable
        {
            [JsonProperty("space")] 
            public string Space { get; set; } = string.Empty;
        }

        public class ConfluenceBody
        {
            [JsonProperty("storage")]
            public ConfluenceStorage Storage { get; set; } = new();
        }

        public class ConfluenceStorage
        {
            [JsonProperty("value")]
            public string Value { get; set; } = string.Empty;
        }
    }

    public interface IConfluenceIngestionService : IDataIngestionWorker
    {
    }
}
