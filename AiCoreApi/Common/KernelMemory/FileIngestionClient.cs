using AiCoreApi.Common.Extensions;
using AiCoreApi.Models.DbModels;
using System.Text;

namespace AiCoreApi.Common.KernelMemory
{
    public sealed class FileIngestionClient : IFileIngestionClient
    {
        private static SemaphoreSlim? _semaphore;
        private const string FilesCollection = "files";
        private readonly IServiceProvider _serviceProvider;
        private readonly ExtendedConfig _config;

        public FileIngestionClient(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _config = _serviceProvider.GetService<ExtendedConfig>();
            _semaphore ??= new SemaphoreSlim(_config.MaxParallelFileIngestionRequests, _config.MaxParallelFileIngestionRequests);
        }

        public Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string? name, byte[] content, Dictionary<string, List<string>> tags,
            TranslateStepModel? translateStep = null, CancellationToken cancellationToken = default) =>
            ExecuteWithSemaphore(() => UploadInternal(embeddingConnectionModel, id, name, content, tags, translateStep, cancellationToken));

        public Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string? name, Stream content, Dictionary<string, List<string>> tags,
            TranslateStepModel? translateStep = null, long? size = null, CancellationToken cancellationToken = default)
        {
            if (content.CanSeek && content.Position > 0)
                content.Seek(0, SeekOrigin.Begin);

            if (!size.HasValue)
                size = content.Length;

            using var binaryReader = new BinaryReader(content);
            var bytes = binaryReader.ReadBytes((int)size);

            return ExecuteWithSemaphore(() => UploadInternal(embeddingConnectionModel, id, name, bytes, tags, translateStep, cancellationToken));
        }

        public Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string url, Dictionary<string, List<string>> tags,
            TranslateStepModel? translateStep = null, CancellationToken cancellationToken = default)
        {
            ValidateUrl(url);
            var bytes = Encoding.UTF8.GetBytes(new Uri(url).AbsoluteUri);
            return ExecuteWithSemaphore(() => UploadInternal(embeddingConnectionModel, id, "content.url", bytes, tags, translateStep, cancellationToken));
        }

        public Task Delete(EmbeddingConnectionModel embeddingConnectionModel, string id, CancellationToken cancellationToken = default) =>
            ExecuteWithSemaphore(() => DeleteInternal(embeddingConnectionModel, id, cancellationToken));

        private async Task ExecuteWithSemaphore(Func<Task> operation)
        {
            await _semaphore.WaitAsync();
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                OnOperationFailed(ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task UploadInternal(EmbeddingConnectionModel embeddingConnectionModel, string id, string? name, byte[] content, 
            Dictionary<string, List<string>> tags, TranslateStepModel? translateStep = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new UploadFileRequest(id, name, content, tags, embeddingConnectionModel, translateStep);
                var httpClient = GetClient();
                using var response = await httpClient.PostAsJsonAsync(FilesCollection, request, cancellationToken);
                response.EnsureSuccessStatusCode();
                OnUploadCompleted(id);
            }
            catch (Exception ex)
            {
                OnOperationFailed(ex, id);
            }
        }

        private async Task DeleteInternal(EmbeddingConnectionModel embeddingConnectionModel, string id, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, $"{FilesCollection}/{id}");
                request.Content = new StringContent(embeddingConnectionModel.ToJson(), Encoding.UTF8, "application/json");
                var httpClient = GetClient();
                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                OnDeleteCompleted(id);
            }
            catch (Exception ex)
            {
                OnOperationFailed(ex, id);
            }
        }

        private HttpClient GetClient()
        {
            var httpClientFactory = _serviceProvider.GetService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(_config.FileIngestionUrl.TrimEnd('/') + "/");
            return httpClient;
        }

        private static void ValidateUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentNullException(nameof(url), "The URL is empty");

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri?.Host) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException($"The URL `{url}` is not valid");
        }

        private void OnUploadCompleted(string id)
        {

        }

        private void OnDeleteCompleted(string id)
        {

        }

        private void OnOperationFailed(Exception error, string id = "")
        {

        }
    }

    public class UploadFileRequest
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public byte[] Content { get; init; }
        public Dictionary<string, List<string>> Tags { get; init; }
        public EmbeddingConnectionModel EmbeddingConnection { get; init; }
        public TranslateStepModel TranslateStep { get; init; } = new();

        public UploadFileRequest(string id, string? name, byte[] content, Dictionary<string, List<string>> tags, EmbeddingConnectionModel embeddingConnection, TranslateStepModel? translateStep)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id), "The file ID is empty");
            }
            if (!IsValid(id))
            {
                throw new ArgumentOutOfRangeException(nameof(id), "The file ID contains invalid chars (allowed: A-B, a-b, 0-9, '.', '_', '-')");
            }

            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? "content.txt" : name;
            Content = content;
            Tags = tags;
            EmbeddingConnection = embeddingConnection;
            TranslateStep = translateStep ?? new TranslateStepModel();
        }

        private static bool IsValid(string? value) => value != null && value.All(IsValidChar);

        private static bool IsValidChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
        }
    }

    public record TranslateStepModel
    {
        public string TargetLanguage { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public bool Enabled { get; set; } = false;
    }

    public class EmbeddingConnectionModel
    {
        public string Endpoint { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string MaxTokens { get; set; } = "";
        public string IndexName { get; set; } = "";
        public ConnectionTypeEnum ConnectionType { get; set; } = ConnectionTypeEnum.Qdrant;
        public string ConnectionString { get; set; } = "";

        public EmbeddingConnectionModel Populate(ConnectionModel embeddingConnection)
        {
            if (embeddingConnection.Type == Models.DbModels.ConnectionType.AzureOpenAiEmbedding)
            {
                Endpoint = embeddingConnection.Content["endpoint"];
                ModelName = embeddingConnection.Content["modelName"];
                ApiKey = embeddingConnection.Content["apiKey"];
                MaxTokens = embeddingConnection.Content["maxTokens"];
                IndexName = embeddingConnection.Content["indexName"];
            }
            else if (embeddingConnection.Type == Models.DbModels.ConnectionType.OpenAiEmbedding)
            {
                ModelName = embeddingConnection.Content["modelName"];
                ApiKey = embeddingConnection.Content["apiKey"];
                MaxTokens = embeddingConnection.Content["maxTokens"];
                IndexName = embeddingConnection.Content["indexName"];
            }
            return this;
        }
    }

    public enum ConnectionTypeEnum
    {
        AzureAiSearch = 1,
        Qdrant = 2
    }

    public interface IFileIngestionClient
    {
        Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string? name, byte[] content, Dictionary<string, List<string>> tags, TranslateStepModel? translateStep = null, CancellationToken cancellationToken = default);
        Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string? name, Stream content, Dictionary<string, List<string>> tags, TranslateStepModel? translateStep = null, long? size = null, CancellationToken cancellationToken = default);
        Task Upload(EmbeddingConnectionModel embeddingConnectionModel, string id, string url, Dictionary<string, List<string>> tags, TranslateStepModel? translateStep = null, CancellationToken cancellationToken = default);
        Task Delete(EmbeddingConnectionModel embeddingConnectionModel, string id, CancellationToken cancellationToken = default);
    }
}