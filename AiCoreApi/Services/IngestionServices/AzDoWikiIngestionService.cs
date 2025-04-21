using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using System.Text;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Web;

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

        public AzDoWikiIngestionService(
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            ITaskProcessor taskProcessor,
            ILogger<AzDoWikiIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IHttpClientFactory httpClientFactory)
        {
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _taskProcessor = taskProcessor;
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _httpClientFactory = httpClientFactory;
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

            var client = _httpClientFactory.CreateClient("NoRetryClient");
            var patToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", patToken);

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
                if (fileInDatabase != null)
                {
                    if (fileInDatabase.Url?.Split("#")?.LastOrDefault() == contentHash)
                        continue;
                }

                var file = fileInDatabase ?? new DocumentMetadataModel(docId)
                {
                    IngestionId = ingestion.IngestionId,
                    Url = $"https://dev.azure.com/{org}/{project}/_wiki/wikis/{wiki}/{page.Id}#{contentHash}",
                    CreatedTime = DateTime.UtcNow,
                    Name = page.Path + ".md"
                };
                await _documentMetadataProcessor.Set(file);

                // Remove previous version before re-uploading
                if (fileInDatabase != null)
                {
                    await _fileIngestionClient.Delete(embeddingConnectionModel, docId);
                    await Task.Delay(10000);
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
