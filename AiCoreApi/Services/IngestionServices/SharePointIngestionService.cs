using System.Reflection;
using Azure.Identity;
using Microsoft.Graph.Models;
using Microsoft.Graph;
using AiCoreApi.Common;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Extensions;
using Microsoft.KernelMemory.Pipeline;
using AiCoreApi.Common.KernelMemory;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;
using Microsoft.KernelMemory;
using System.Web;

namespace AiCoreApi.Services.IngestionServices
{
    public class SharePointIngestionService: ISharePointIngestionService
    {
        private readonly ExtendedConfig _config;
        private readonly IFileIngestionClient _fileIngestionClient;
        private readonly IDocumentMetadataProcessor _documentMetadataProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly ITaskProcessor _taskProcessor;
        private readonly HttpClient _httpClient;
        private readonly ILogger<SharePointIngestionService> _logger;
        private readonly IDataIngestionHelperService _dataIngestionHelperService;
        private readonly IKernelMemoryProvider _kernelMemoryProvider;

        public SharePointIngestionService(
            ExtendedConfig config,
            IFileIngestionClient fileIngestionClient,
            IDocumentMetadataProcessor documentMetadataProcessor,
            IConnectionProcessor connectionProcessor,
            ITaskProcessor taskProcessor,
            IHttpClientFactory httpClientFactory,
            ILogger<SharePointIngestionService> logger,
            IDataIngestionHelperService dataIngestionHelperService,
            IKernelMemoryProvider kernelMemoryProvider)
        {
            _config = config;
            _fileIngestionClient = fileIngestionClient;
            _documentMetadataProcessor = documentMetadataProcessor;
            _connectionProcessor = connectionProcessor;
            _taskProcessor = taskProcessor;
            _httpClient = httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            _logger = logger;
            _dataIngestionHelperService = dataIngestionHelperService;
            _kernelMemoryProvider = kernelMemoryProvider;
        }

        private static string[]? _ext;
        public static string[] Ext
        {
            get
            {
                if (_ext == null)
                {
                    // get all supported extensions from FileExtensions class from KernelMemory
                    var fields = typeof(FileExtensions)
                        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string));
                    _ext = fields.Select(f => (string)(f.GetRawConstantValue() ?? "unknown")).ToArray();
                }
                return _ext;
            }
        }

        public async Task<string> GetFileByPath(IngestionModel ingestion, string path)
        {
            // Retrieve connection
            var sharePointConnectionId = Convert.ToInt32(ingestion.Content["ConnectionId"]);
            var sharePointConnection = await _connectionProcessor.GetById(sharePointConnectionId)
                                       ?? throw new InvalidOperationException($"SharePoint connection with Id = {sharePointConnectionId} not found.");
            var connection = new SharePointConnection(sharePointConnection.Content);
            // Graph client
            var graph = new GraphServiceClient(
                _httpClient,
                new ClientSecretCredential(
                    connection.TenantId,
                    connection.ClientId,
                    connection.ClientSecret),
                new[] { "https://graph.microsoft.com/.default" });

            var decoded = path.Split('|');
            var driveId = "";
            var itemId = "";
            if (decoded.Length == 1)
            {
                path = path.Replace(" ", "%20");
                var metadata = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId)
                    .FirstOrDefault(x => x.Url.EndsWith(path));
                if (metadata == null)
                    throw new InvalidOperationException($"File with path '{path}' not found in metadata.");
                var fileId = metadata.DocumentId.DecodeUniqueId().Split('/');
                driveId = fileId[0];
                itemId = fileId[1];
            }
            else if (decoded.Length == 2)
            {
                driveId = decoded[0];
                itemId = decoded[1];
            }
            else
                throw new InvalidOperationException($"Invalid path format: {path}, expected format is 'DriveId|ItemId'.");


            // Download content as stream
            var contentStream = await graph.Drives[driveId].Items[itemId].Content.GetAsync() ?? throw new InvalidOperationException($"Failed to retrieve file content for '{itemId}'.");

            using var memory = new MemoryStream();
            await contentStream.CopyToAsync(memory);
            var bytes = memory.ToArray();

            // Try to interpret as UTF-8 text; fallback to Base64
            try
            {
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Convert.ToBase64String(bytes);
            }
        }

        public async Task<string> GetFile(IngestionModel ingestion, string fileId)
        {
            try
            {
                // Parse DriveId and ItemId from UniqueId
                var decoded = fileId.DecodeUniqueId().Split('/');
                if (decoded.Length != 2)
                    throw new InvalidOperationException($"Invalid fileId format: {fileId}");
                return await GetFileByPath(ingestion, $"{decoded[0]}|{decoded[1]}");

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get file '{fileId}' from SharePoint.");
                throw;
            }
        }

        public async Task SetFile(IngestionModel ingestion, string fileId, string articleText)
        {
            try
            {
                // Retrieve connection
                var sharePointConnectionId = Convert.ToInt32(ingestion.Content["ConnectionId"]);
                var sharePointConnection = await _connectionProcessor.GetById(sharePointConnectionId)
                    ?? throw new InvalidOperationException($"SharePoint connection with Id = {sharePointConnectionId} not found.");
                var connection = new SharePointConnection(sharePointConnection.Content);

                // Retrieve metadata
                var metadata = _documentMetadataProcessor.Get(fileId)
                    ?? throw new InvalidOperationException($"File '{fileId}' not found in metadata.");

                // Graph client
                var graph = new GraphServiceClient(
                    _httpClient,
                    new ClientSecretCredential(
                        connection.TenantId,
                        connection.ClientId,
                        connection.ClientSecret),
                    new[] { "https://graph.microsoft.com/.default" });

                // Parse DriveId and ItemId from UniqueId
                var decoded = fileId.DecodeUniqueId().Split('/');
                if (decoded.Length != 2)
                    throw new InvalidOperationException($"Invalid fileId format: {fileId}");

                var driveId = decoded[0];
                var itemId = decoded[1];

                // Decide whether text or Base64 content was provided
                byte[] bytes;
                if (IsBase64String(articleText))
                {
                    bytes = Convert.FromBase64String(articleText);
                }
                else
                {
                    bytes = System.Text.Encoding.UTF8.GetBytes(articleText);
                }

                // Update file in SharePoint
                using var stream = new MemoryStream(bytes);
                await graph.Drives[driveId].Items[itemId].Content.PutAsync(stream);

                _logger.LogInformation($"Updated file '{metadata.Name}' successfully in SharePoint.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set file '{fileId}' in SharePoint.");
                throw;
            }
        }

        private bool IsBase64String(string input)
        {
            Span<byte> buffer = new Span<byte>(new byte[input.Length]);
            return Convert.TryFromBase64String(input, buffer, out _);
        }


        public async Task<List<string>> GetAutoComplete(string parameterName, IngestionModel ingestionModel)
        {
            if (!ingestionModel.Content.TryGetValue("ConnectionId", out var sharePointConnectionId))
                return new List<string>();
            if (parameterName != "Path")
                return new List<string>();
            if (!ingestionModel.Content.TryGetValue("Path", out var path))
                path = "/";
            var sharePointConnection = await _connectionProcessor.GetById(Convert.ToInt32(sharePointConnectionId));
            if (sharePointConnection == null)
                return new List<string>();
            var connection = new SharePointConnection(sharePointConnection.Content);
            var graph = new GraphServiceClient(
                _httpClient,
                new ClientSecretCredential(
                    connection.TenantId,
                    connection.ClientId,
                    connection.ClientSecret),
                new[] { "https://graph.microsoft.com/.default" });

            var folders = await GetSharePointFolders(graph, connection.Site, connection.Drive, path);
            return folders;
        }

        private async Task<List<string>> GetSharePointFolders(GraphServiceClient graph, string siteName, string driveName, string? path)
        {
            try
            {
                path = path?.Trim('/');
                var site = await graph.Sites[siteName].GetAsync() ?? throw new InvalidOperationException($"Site {siteName} not found.");
                var driveCollection = await graph.Sites[site.Id].Drives.GetAsync(rc =>
                { rc.QueryParameters.Select = new[] { "id", "name" }; });
                var drives = new List<Drive>();
                if (driveCollection != null)
                {
                    await PageIterator<Drive, DriveCollectionResponse>.CreatePageIterator(graph, driveCollection, drive =>
                    {
                        drives.Add(drive);
                        return true;
                    }).IterateAsync();
                }
                var drive = drives.Find(d => driveName.Equals(d.Name, StringComparison.InvariantCultureIgnoreCase))
                            ?? throw new InvalidOperationException($"Drive {driveName} not found.");

                DriveItem? driveItem;
                try
                {
                    driveItem = string.IsNullOrWhiteSpace(path)
                        ? await graph.Drives[drive.Id].Root.GetAsync()
                        : await graph.Drives[drive.Id].Root.ItemWithPath(path).GetAsync();
                }
                catch
                {
                    // Path not found
                    return new List<string>();
                }

                var rootPath = string.IsNullOrWhiteSpace(path) ? "/" : $"/{path}/";
                var folders = new List<string> { rootPath };
                var childrenCollection = await graph.Drives[drive.Id].Items[driveItem.Id].Children.GetAsync();
                var children = new List<DriveItem>();
                if (childrenCollection != null)
                {
                    await PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(graph, childrenCollection, child =>
                    {
                        children.Add(child);
                        return true;
                    }).IterateAsync();
                }
                folders.AddRange(children.Where(child => child.Folder != null).Select(child => $"{rootPath}{child.Name}/"));
                return folders;
            }
            catch (Exception)
            {
                throw new AiCoreUiException("Cannot read SharePoint folders. Please check your connection settings.");
            }
        }

        private List<string> GetExcludedExtensions(IngestionModel ingestion)
        {
            var excludedExtensions = new List<string>();
            if (ingestion.Content.TryGetValue("ExcludedExtensions", out var excludedExtensionsString))
            {
                excludedExtensions = excludedExtensionsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ext => ext.Trim().ToLower())
                    .ToList();
            }
            return excludedExtensions;
        }

        public async Task Process(IngestionModel ingestion, int taskId)
        {
            var translateStepModel = await _dataIngestionHelperService.GetTranslateStepModel(ingestion);
            var embeddingConnection = await _dataIngestionHelperService.GetEmbeddingConnection(ingestion);
            var embeddingConnectionModel = new EmbeddingConnectionModel().Populate(embeddingConnection);
            await _dataIngestionHelperService.FillVectorDbConnection(ingestion, embeddingConnectionModel);

            var connections = await _connectionProcessor.List(ingestion.WorkspaceId);
            var llmConnection = connections.FirstOrDefault(x => x.Type.IsLlmConnection()); // Assuming there's a default LLM connection
            var vectorDbConnectionId = ingestion.Content.ContainsKey(DataIngestionHelperService.Constants.VectorDbConnectionField) ? ingestion.Content[DataIngestionHelperService.Constants.VectorDbConnectionField] : "";

            var vectorDbConnection = (string.IsNullOrEmpty(vectorDbConnectionId) || vectorDbConnectionId == "0")
                ? null
                : connections.FirstOrDefault(x => x.ConnectionId.ToString() == vectorDbConnectionId);
            var kernelMemory = _kernelMemoryProvider.GetKernelMemory(llmConnection, embeddingConnection, vectorDbConnection);

            var excludedExtensions = GetExcludedExtensions(ingestion);
            var sharePointConnectionId = Convert.ToInt32(ingestion.Content["ConnectionId"]);
            var sharePointConnection = await _connectionProcessor.GetById(sharePointConnectionId);
            if (sharePointConnection == null)
                throw new InvalidOperationException($"SharePoint connection with Id = {sharePointConnectionId} not found.");

            var connection = new SharePointConnection(sharePointConnection.Content);
            ingestion.Content.TryGetValue("ExcludeFolders", out var excludeFolderPathsAsString);
            ingestion.Content.TryGetValue("Path", out var path);
            path = path?.Trim('/');
            var graph = new GraphServiceClient(
                _httpClient,
                new ClientSecretCredential(
                    connection.TenantId,
                    connection.ClientId,
                    connection.ClientSecret),
                new[] { "https://graph.microsoft.com/.default" });

            var (drive, driveItem) = await GetFolder(graph, connection, path);
            var foldersToExclude = GetFoldersToExcludeAsList(excludeFolderPathsAsString);
            await _taskProcessor.SetMessage(taskId, "Files calculation");
            var files = await GetFiles(graph, drive, driveItem, foldersToExclude, string.Empty, excludedExtensions);

            await IndexDocuments(embeddingConnectionModel, ingestion, graph, files, taskId, translateStepModel, kernelMemory);
        }

        private static List<string> GetFoldersToExcludeAsList(string? excludeFolders)
        {
            if (string.IsNullOrWhiteSpace(excludeFolders))
                return new List<string>();
            var excludeFoldersList = excludeFolders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return excludeFoldersList
                .Select(folder => folder.Replace("\\", "/").Replace("\"", "").Trim('/'))
                .ToList();
        }

        private static string GetFullPath(string currentPath, string? folderName) => folderName == null
            ? currentPath
            : !string.IsNullOrWhiteSpace(currentPath) ? $"{currentPath}/{folderName}" : folderName;

        private async Task RemoveDeletedFiles(EmbeddingConnectionModel embeddingConnectionModel, List<DocumentMetadataModel> filesInDatabase, List<File> filesInSharePoint, int taskId)
        {
            var fileInSharePointIds = filesInSharePoint.Select(f => f.UniqueId).ToList();
            var fileInDatabaseIds = filesInDatabase.Select(m => m.DocumentId);
            var fileToRemoveFromDatabaseIds = fileInDatabaseIds.Except(fileInSharePointIds).ToList();
            var i = 0;
            foreach (var fileToRemoveFromDatabaseId in fileToRemoveFromDatabaseIds)
            {
                i++;
                var fileInDatabase = filesInDatabase.First(m => m.DocumentId == fileToRemoveFromDatabaseId);
                try
                {
                    await _taskProcessor.SetMessage(taskId, $"Remove deleted files in process [{i}/{fileToRemoveFromDatabaseIds.Count}]");
                    await _fileIngestionClient.Delete(embeddingConnectionModel, fileToRemoveFromDatabaseId);
                    filesInDatabase.Remove(fileInDatabase);
                    await _documentMetadataProcessor.Remove(fileInDatabase);
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to remove file {fileInDatabase.Name} from kernel memory or from metadata.");
                }
            }
        }

        private async Task IndexDocuments(EmbeddingConnectionModel embeddingConnectionModel, IngestionModel ingestion, GraphServiceClient graph, List<File> filesInSharePoint, int taskId, TranslateStepModel translateStepModel, IKernelMemory kernelMemory)
        {
            var filesInDatabase = _documentMetadataProcessor.GetByIngestion(ingestion.IngestionId);
            // Remove files that were deleted from SharePoint
            await RemoveDeletedFiles(embeddingConnectionModel, filesInDatabase, filesInSharePoint, taskId);
            var i = 0;
            foreach (var fileInSharePoint in filesInSharePoint)
            {
                i++;
                await _taskProcessor.SetMessage(taskId, $"Import file \"{fileInSharePoint.Url}\" [{i}/{filesInSharePoint.Count}]");
                var fileInDatabase = filesInDatabase.FirstOrDefault(fileInDatabase => fileInDatabase.DocumentId == fileInSharePoint.UniqueId);
                if (fileInDatabase != null)
                {
                    filesInDatabase.Remove(fileInDatabase);
                    if (fileInDatabase.LastModifiedTime >= fileInSharePoint.LastModifiedTime)
                    {
                        var search = await kernelMemory.SearchAsync("",
                            embeddingConnectionModel.IndexName,
                            filter: new MemoryFilter().ByDocument(fileInSharePoint.UniqueId));
                        if (search.Results.Count > 0) // If the file already exists in the vector DB, skip re-upload
                            continue;
                    }
                    // Remove file from kernel memory to update it
                    await _fileIngestionClient.Delete(embeddingConnectionModel, fileInSharePoint.UniqueId);
                }
                else
                {
                    // Check if file was already imported by another ingestion
                    var doc = _documentMetadataProcessor.Get(fileInSharePoint.UniqueId);
                    if (doc != null)
                    {
                        _logger.Log(LogLevel.Warning, $"File {fileInSharePoint.Name} already imported by task {doc.IngestionId}.");
                        continue;
                    }
                }
                // Import file

                await ImportFile(embeddingConnectionModel, ingestion, graph, fileInSharePoint, translateStepModel);
            }
            await _taskProcessor.SetMessage(taskId, $"Complete.");
        }

        private async Task ImportFile(EmbeddingConnectionModel embeddingConnectionModel, IngestionModel ingestion, GraphServiceClient graph, File fileInSharePoint, TranslateStepModel translateStepModel)
        {
            // Add file to database
            var fileInDatabase = new DocumentMetadataModel(fileInSharePoint.UniqueId)
            {
                IngestionId = ingestion.IngestionId,
                Name = fileInSharePoint.Name,
                Url = fileInSharePoint.Url,
                CreatedTime = fileInSharePoint.CreatedTime.UtcDateTime,
                LastModifiedTime = fileInSharePoint.LastModifiedTime.UtcDateTime,
            };
            await _documentMetadataProcessor.Set(fileInDatabase);

            // Import file to kernel memory
            try
            {
                await using var stream = await GetFileContentAsync(graph, fileInSharePoint);
                await _fileIngestionClient.Upload(embeddingConnectionModel, fileInSharePoint.UniqueId, fileInSharePoint.Name, stream,
                    ingestion.Tags.ToTagDictionary(), translateStepModel);

                // Update file in database to mark it as imported
                fileInDatabase.ImportFinished = true;
                await _documentMetadataProcessor.Set(fileInDatabase);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to import file {fileInSharePoint.Name}.");
            }
        }

        private async Task<Stream> GetFileContentAsync(GraphServiceClient graph, File file)
        {
            var content = await graph.Drives[file.DriveId].Items[file.ItemId].Content.GetAsync() ??
                          throw new InvalidOperationException($"Failed to retrieve {file.Name} file content.");
            if (content.CanSeek)
            {
                return content;
            }

            var memory = new MemoryStream();
            await using (content)
            {
                await content.CopyToAsync(memory);

                await memory.FlushAsync();
                memory.Seek(0, SeekOrigin.Begin);
            }

            return memory;
        }

        private async Task<(Drive, DriveItem)> GetFolder(GraphServiceClient graph, SharePointConnection sharePointConnection, string? path)
        {
            var site = await graph.Sites[sharePointConnection.Site].GetAsync()
                       ?? throw new InvalidOperationException($"Site {sharePointConnection.Site} not found.");
            var driveCollection = await graph.Sites[site.Id].Drives.GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "id", "name" };
            });
            var drives = new List<Drive>();
            if (driveCollection != null)
            {
                var pi = PageIterator<Drive, DriveCollectionResponse>.CreatePageIterator(graph, driveCollection,
                    d =>
                    {
                        drives.Add(d);
                        return true;
                    });
                await pi.IterateAsync();
            }

            var drive = drives.Find(d => sharePointConnection.Drive.Equals(d.Name, StringComparison.InvariantCultureIgnoreCase))
                        ?? throw new InvalidOperationException($"Drive {sharePointConnection.Drive} not found.");

            if (string.IsNullOrWhiteSpace(path))
            {
                return (drive, await graph.Drives[drive.Id].Root.GetAsync()
                               ?? throw new InvalidOperationException($"Folder root not found."));
            }

            return (drive, await graph.Drives[drive.Id].Root.ItemWithPath(path).GetAsync()
                           ?? throw new InvalidOperationException($"Folder {path} not found."));
        }

        private async Task<List<File>> GetFiles(GraphServiceClient graph, Drive drive, DriveItem folder, List<string> excludeFolders, string currentPath, List<string> excludedExtensions)
        {
            var files = new List<File>();
            var childrenCollection = await graph.Drives[drive.Id].Items[folder.Id].Children.GetAsync();

            var children = new List<DriveItem>();
            if (childrenCollection != null)
            {
                var pi = PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(graph, childrenCollection,
                    child =>
                    {
                        children.Add(child);
                        return true;
                    });
                await pi.IterateAsync();
            }

            foreach (var child in children)
            {
                if (child.Folder != null)
                {
                    var folderPath = GetFullPath(currentPath, child.Name);
                    // * means exclude all folders except files
                    if (child.Name == null || !excludeFolders.Any(fp => fp == "*" || string.Compare(folderPath, fp, true) == 0))
                        files.AddRange(await GetFiles(graph, drive, child, excludeFolders, folderPath, excludedExtensions));
                }
                else if (IsSupported(child, excludedExtensions))
                {
                    files.Add(new File(drive, child));
                }
            }
            return files;
        }

        private bool IsSupported(DriveItem item, List<string> excludedExtensions)
        {
            var fileExtension = (Path.GetExtension(item.Name) ?? ".unknown").ToLower();
            var result = item.File != null
                && item.Deleted == null
                && item.Size < _config.MaxFileSize
                && item.Name != null
                && item.Name.StartsWith("~") != true
                && Ext.Contains(fileExtension, StringComparer.OrdinalIgnoreCase)
                && !excludedExtensions.Contains(fileExtension);
            return result;
        }

        internal class File
        {
            public string UniqueId { get; }
            public string? DriveId { get; }
            public string? ItemId { get; }
            public string? Name { get; }
            public string? Url { get; }
            public DateTimeOffset CreatedTime { get; }
            public DateTimeOffset LastModifiedTime { get; }

            public File(Drive drive, DriveItem item)
            {
                UniqueId = $"{drive.Id}/{item.Id}".UniqueId();
                DriveId = drive.Id;
                ItemId = item.Id;
                Name = item.Name;
                Url = item.WebUrl;
                CreatedTime = item.CreatedDateTime ?? DateTimeOffset.MinValue;
                LastModifiedTime = item.LastModifiedDateTime ?? DateTimeOffset.MinValue;
            }
        }

        class SharePointConnection
        {
            public string TenantId { get; }
            public string ClientId { get; }
            public string ClientSecret { get; }
            public string Site { get; }
            public string Drive { get; }

            public SharePointConnection(Dictionary<string, string> content)
            {
                TenantId = content["TenantId"];
                ClientId = content["ClientId"];
                ClientSecret = content["ClientSecret"];
                Site = content["Site"]
                    .Replace("https://", string.Empty)
                    .Replace("sharepoint.com/sites/", "sharepoint.com:/sites/");
                Drive = content["Drive"];
            }
        }
    }

    public interface ISharePointIngestionService: IDataIngestionWorker
    {
    }
}