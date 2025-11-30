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
    public class AzDoWikiIngestionService : IAzDoWikiIngestionService
    {
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly ILogger<AzDoWikiIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;
        private readonly IConnectionManager _connectionManager;

        public AzDoWikiIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<AzDoWikiIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IHttpClientFactory httpClientFactory,
            IKernelMemoryProvider kernelMemoryProvider,
            IConnectionManager connectionManager)
        {
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _httpClientFactory = httpClientFactory;
            _kernelMemoryProvider = kernelMemoryProvider;
            _connectionManager = connectionManager;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            await _taskProcessor.SetMessage(taskId, "Initializing Azure DevOps Wiki ingestion");
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            var pat = ingestion.Content["PAT"];
            var org = ingestion.Content["Organization"];
            var project = ingestion.Content["Project"];
            var wiki = ingestion.Content["WikiIdentifier"];

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var patToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", patToken);

            var llmConnection = await _connectionManager.GetConnectionWithParams(ingestion.WorkspaceId, isLlmConnection: true); // Assuming there's a default LLM connection
            var vectorDbConnectionId = ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.VectorDbConnectionField) ? ingestion.Content[DataIngestionHelperService.Constants.VectorDbConnectionField] : "";

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null // Internal Qdrant
                : await _connectionManager.GetConnectionWithParams(workspaceId: ingestion.WorkspaceId, connectionId: Convert.ToInt32(vectorDbConnectionId));

            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);

            var pages = await GetWikiPages(client, org, project, wiki);
            var pageDocIds = new HashSet<string>();

            var i = 0;
            foreach (var page in pages)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Processing page '{page.Path}' [{i}/{pages.Count}]");
                await GetPageContent(client, org, project, wiki, page);
                var contentHash = ComputeHash(page.Content);
                var docId = ($"{wiki}/{page.Path}").UniqueId();
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
                file.Url = $"https://dev.azure.com/{org}/{project}/_wiki/wikis/{wiki}/{page.Id}#{contentHash}";
                file.Name = page.Path + ".md";

                await _documentMetadataProcessor.Set(file);

                // Remove previous version before re-uploading
                if (fileInDatabase != null)
                {
                    await _fileIngestionClient.Delete(embeddingConnectionModel, docId);
                    await Task.Delay(5000);
                }

                if (!string.IsNullOrEmpty(page.Content) && page.Id != 0)
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(page.Content));
                    var fileName = page.Path.Split('/').LastOrDefault();
                    await _fileIngestionClient.Upload(embeddingConnectionModel, docId, fileName + ".md", stream, ingestion.Tags.ToTagDictionary(), translateStepModel);
                    _logger.LogInformation($"File {page.Path} uploaded successfully.");
                }
                else
                {
                    _logger.LogInformation($"File {page.Path} empty.");
                }
                file.ImportFinished = true;
                await _documentMetadataProcessor.Set(file);
            }

            await RemoveDeletedFiles(embeddingConnectionModel, ingestion, pageDocIds, taskId);

            await _taskProcessor.SetMessage(taskId, "Completed");
        }

        public async Task<string> GetFileByPath(IngestionModel ingestion, string path)
        {
            var pat = ingestion.Content["PAT"];
            var org = ingestion.Content["Organization"];
            var project = ingestion.Content["Project"];
            var wiki = ingestion.Content["WikiIdentifier"];

            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            var patToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", patToken);

            // Get the latest content directly from Azure DevOps Wiki
            var encodedPath = Uri.EscapeDataString(path).Replace("%20", "+");
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={encodedPath}&includeContent=True&api-version=7.0";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<AzDoWikiPage>(json);

            return data?.Content ?? string.Empty;
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            try
            {
                // fileId == docId == "{wiki}/{pagePath}". UniqueId() was used before,
                // so we need to reconstruct the path from the docId if necessary.
                // Assuming docId was based on "wiki/path" -> UniqueId(), we should search metadata first.
                var metadata = _documentMetadataProcessor.Get(fileId);
                if (metadata == null)
                    throw new InvalidOperationException($"File with id '{fileId}' not found in metadata.");

                // Extract the original wiki path from the file name (stored as .md)
                var path = metadata.Name.Replace(".md", string.Empty);
                return await GetFileByPath(ingestion, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file '{fileId}' from Azure DevOps Wiki.");
                throw;
            }
        }

        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            try
            {
                var pat = ingestion.Content["PAT"];
                var org = ingestion.Content["Organization"];
                var project = ingestion.Content["Project"];
                var wiki = ingestion.Content["WikiIdentifier"];
                var branch = ingestion.Content.ContainsKey("Branch") ? ingestion.Content["Branch"] : "";

                var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                var patToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", patToken);

                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File '{fileId}' not found.");

                var path = metadata.Name.Replace(".md", string.Empty);
                var encodedPath = HttpUtility.UrlEncode(path);

                var getPageUrl = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={encodedPath}&includeContent=True&api-version=7.1";
                var getResponse = await client.GetAsync(getPageUrl);
                getResponse.EnsureSuccessStatusCode();
                if (string.IsNullOrEmpty(getResponse.Headers.ETag?.Tag))
                    throw new InvalidOperationException("Failed to get ETag from existing wiki page.");

                var etag = getResponse.Headers.ETag.Tag;

                var json = await getResponse.Content.ReadAsStringAsync();
                var pageData = JsonConvert.DeserializeObject<AzDoWikiPage>(json);
                if (pageData == null || pageData.Id == 0)
                    throw new InvalidOperationException($"Page '{path}' not found in Azure DevOps Wiki.");


                var updateUrl = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={encodedPath}&{(string.IsNullOrEmpty(branch) ? "" : $@"versionDescriptor.versionType=branch&versionDescriptor.version={branch}&")}api-version=7.1";
                var payload = new { content = articleText };
                var requestContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.IfMatch.ParseAdd(etag);

                var updateResponse = await client.PutAsync(updateUrl, requestContent);
                updateResponse.EnsureSuccessStatusCode();

                _logger.LogInformation($"Updated page '{path}' with ETag {etag}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set file '{fileId}' in Azure DevOps Wiki.");
                throw;
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

        private async Task<List<AzDoWikiPage>> GetWikiPages(HttpClient client, string org, string project, string wiki)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path=/&recursionLevel=full&api-version=7.0";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var azDoWikiPage = JsonConvert.DeserializeObject<AzDoWikiPage>(json);
            if(azDoWikiPage == null)
                return new List<AzDoWikiPage>();
            var list = new List<AzDoWikiPage> { azDoWikiPage };
            foreach (var subPage in azDoWikiPage.SubPages)
            {
                list.AddRange(GetSubPages(subPage));
            }
            return list;
        }

        private List<AzDoWikiPage> GetSubPages(AzDoWikiPage page)
        {
            var list = new List<AzDoWikiPage> { page };
            foreach (var subPage in page.SubPages)
            {
                list.AddRange(GetSubPages(subPage));
            }
            return list;
        }

        private async Task GetPageContent(HttpClient client, string org, string project, string wiki, AzDoWikiPage page)
        {
            var url = $"https://dev.azure.com/{org}/{project}/_apis/wiki/wikis/{wiki}/pages?path={HttpUtility.UrlEncode(page.Path)}&includeContent=True&api-version=7.0";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<AzDoWikiPage>(json);
            page.Content = data?.Content ?? string.Empty;
            page.Id = data?.Id ?? 0;
        }

        private string ComputeHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            var hashBytes = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hashBytes);
        }


        public class AzDoWikiPage
        {
            public int Id { get; set; } = 0;
            public string Path { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public int Order { get; set; }
            public bool IsParentPage { get; set; }
            public string GitItemPath { get; set; } = string.Empty;
            public List<AzDoWikiPage> SubPages { get; set; } = new();
            public string RemoteUrl { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }

    public interface IAzDoWikiIngestionService : IDataIngestionWorker
    {
    }
}
