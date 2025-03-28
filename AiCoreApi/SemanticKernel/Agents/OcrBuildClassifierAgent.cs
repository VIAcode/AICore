using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Azure.Core;
using Azure.Storage;
using Azure;
using Microsoft.SemanticKernel;
using System.Web;
using Azure.Identity;
using Azure.AI.DocumentIntelligence;
using Elastic.Transport;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OcrBuildClassifierAgent: BaseAgent, IOcrBuildClassifierAgent
    {
        private static readonly HashSet<string> SupportedFileFormats = new([".pdf", ".jpeg", "jpg", ".png", ".bmp", ".tiff", ".heif"], StringComparer.InvariantCultureIgnoreCase);

        private const string DebugMessageSenderName = "OcrBuildClassifierAgent";

        private static readonly TimeSpan SasTtl = TimeSpan.Parse("02:00:00"); 

        private const string OcrModelName = "prebuilt-layout";

        private const string HttpClientName = "RetryClient";

        private const int DocumentTypesMinCount = 2;

        private static class AgentContentParameters
        {
            public const string DocumentIntelligenceConnection = "documentIntelligenceConnection";
            public const string StorageAccountConnection = "storageAccountConnection";
            public const string ClassifierId = "classifierId";
            public const string BaseClassifierId = "baseClassifierId";
            public const string ContainerName = "containerName";
            public const string DocumentTypes = "documentTypes";
            public const string DocumentIntelligenceToStorageAccountAuthMethod = "documentIntelligenceToStorageAccountAuthMethod";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public OcrBuildClassifierAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig, 
            ILogger<BaseAgent> logger) : base(requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent, 
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var diConnectionName = agent.Content[AgentContentParameters.DocumentIntelligenceConnection].Value;

            var saConnectionName = agent.Content[AgentContentParameters.StorageAccountConnection].Value;

            var connections = await _connectionProcessor.List();

            var diConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.DocumentIntelligence, DebugMessageSenderName, connectionName: diConnectionName);
            var saConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.StorageAccount, DebugMessageSenderName, connectionName: saConnectionName);

            var classifierId = ApplyParameters(agent.Content[AgentContentParameters.ClassifierId].Value, parameters);

            if (string.IsNullOrWhiteSpace(classifierId))
            {
                throw new ArgumentException($"{AgentContentParameters.ClassifierId} cannot be empty");
            }

            var baseClassifierId = !agent.Content.ContainsKey(AgentContentParameters.BaseClassifierId) 
                ? null 
                : ApplyParameters(agent.Content[AgentContentParameters.BaseClassifierId].Value, parameters);

            var containerName = ApplyParameters(agent.Content[AgentContentParameters.ContainerName].Value, parameters);

            if (string.IsNullOrWhiteSpace(containerName))
            {
                throw new ArgumentException($"{AgentContentParameters.ContainerName} cannot be empty");
            }

            var documentTypesRaw = ApplyParameters(agent.Content[AgentContentParameters.DocumentTypes].Value, parameters);
            var documentTypes = Regex.Split(documentTypesRaw, @"\r?\n")
                .Select(x => x.Trim())
                .Where(s=>!string.IsNullOrWhiteSpace(s))
                .ToList();

            if (documentTypes.Count < DocumentTypesMinCount)
            {
                throw new ArgumentException($"{AgentContentParameters.DocumentTypes} should contain at least {DocumentTypesMinCount} 2 non-empty entries");
            }

            var diEndpoint = diConnection.Content["endpoint"];
            var diApiKey = diConnection.Content["apiKey"];

            var saAccountName = saConnection.Content["accountName"];
            var saAccountKey = saConnection.Content["apiKey"];

            var diToSaAuthMethodRaw = GetParameterValueOrNull(agent, AgentContentParameters.DocumentIntelligenceToStorageAccountAuthMethod);

            var authData = GetAuthData(saAccountName, saAccountKey, diApiKey, diToSaAuthMethodRaw);
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Build Classifier", $"Document Intelligence: {diEndpoint}\nClassifier ID: {classifierId}\nBase Classifier ID: {baseClassifierId}\nDocument Types: {JsonSerializer.Serialize(documentTypes)}\nStorage Account: {saAccountName}\nContainer Name: {containerName}");
            
            var buildWarnings = await BuildClassifier(
                diEndpoint,
                classifierId,
                baseClassifierId,
                saAccountName,
                containerName,
                documentTypes,
                authData
            );

            return buildWarnings;
        }
        private async Task<string> BuildClassifier(
            string diEndpoint,
            string classifierId,
            string baseClassifierId,
            string saAccountName,
            string containerName,
            List<string> documentTypes,
            AuthData authData)
        {
            var saEndpoint = GetStorageAccountEndpoint(saAccountName);
            var blobServiceClient = GetBlobServiceClient(saEndpoint, authData);

            var containerUrl = GetContainerUrl(saEndpoint, containerName, authData, blobServiceClient, SasTtl);

            var blobSources = GetBlobSources(documentTypes, containerName, containerUrl);

            await PrepareBlobs(blobServiceClient, blobSources, OcrModelName, diEndpoint, authData, SupportedFileFormats);

            var result = await BuildClassifier(blobSources, classifierId, baseClassifierId, diEndpoint, authData);

            return result?.Warnings == null ? string.Empty : JsonSerializer.Serialize(result.Warnings);
        }

        async Task<DocumentClassifierDetails> BuildClassifier(ICollection<ImageTypeBlobSource> blobSources, string classifierId, string baseClassifierId, string ocrEndpoint, AuthData authData, CancellationToken cancellationToken = default(CancellationToken))
        {
            var imageTypes = GetDocumentTypes(blobSources);

            var buildOptions = new BuildDocumentClassifierContent(classifierId, imageTypes)
            {
                AllowOverwrite = true,
                BaseClassifierId = string.IsNullOrWhiteSpace(baseClassifierId) ? null : baseClassifierId
            };

            var adminClient = GetOcrAdministrationClient(ocrEndpoint, authData);

            var operation = await adminClient.BuildClassifierAsync(
                WaitUntil.Completed,
                buildOptions,
                cancellationToken
            ).ConfigureAwait(false);

            return operation.Value;
        }

        Dictionary<string, ClassifierDocumentTypeDetails> GetDocumentTypes(ICollection<ImageTypeBlobSource> blobSourceItems)
        {
            return blobSourceItems.ToDictionary(b => b.ImageTypeName, b => new ClassifierDocumentTypeDetails { AzureBlobSource = b.BlobSource });
        }

        async Task PrepareBlobs(BlobServiceClient blobServiceClient, ICollection<ImageTypeBlobSource> blobSources, string modelName, string ocrEndpoint, AuthData authData, HashSet<string> supportedFileExtensions, CancellationToken cancellationToken = default(CancellationToken))
        {
            var ocrClient = GetOcrClient(ocrEndpoint, authData);

            foreach (var bsi in blobSources)
            {
                var containerClient = blobServiceClient.GetBlobContainerClient(bsi.ContainerName);

                await foreach (var blob in containerClient.GetBlobsAsync(prefix: bsi.BlobPrefix, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (!supportedFileExtensions.Contains(Path.GetExtension(blob.Name)))
                    {
                        continue;
                    }

                    try
                    {
                        //TODO: Upgrade Azure.AI.DocumentIntelligence to a newer version and get a rid of downloading a blob. 
                        //In the newer version of the nuget we can pass uri of a blob directly to AnalyzeDocumentAsync.
                        //<Download blob>
                        var blobClient = containerClient.GetBlobClient(blob.Name);
                        using var memoryStream = new MemoryStream();
                        await blobClient.DownloadToAsync(memoryStream, cancellationToken).ConfigureAwait(false);

                        var content = new AnalyzeDocumentContent
                        {
                            Base64Source = BinaryData.FromBytes(memoryStream.ToArray()),
                        };
                        //</Download blob>

                        var operation = await ocrClient.AnalyzeDocumentAsync(WaitUntil.Completed, modelName, content, cancellationToken: cancellationToken).ConfigureAwait(false);
                        var response = operation.GetRawResponse();

                        var ocrBlobName = blob.Name + ".ocr.json";
                        var ocrBlobClient = containerClient.GetBlobClient(ocrBlobName);
                        var blobResponse = await ocrBlobClient.UploadAsync(response.Content, true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // continue processing of the remaining blobs
                        // Log warning
                    }
                    
                }
            }
        }

        ICollection<ImageTypeBlobSource> GetBlobSources(ICollection<string> prefixes, string containerName, string containerUrl)
        {
            var prefixesUnique = prefixes.Select(p => Regex.Replace(p, "/+$", "")).ToHashSet(StringComparer.InvariantCultureIgnoreCase);
            var containerUri = new Uri(containerUrl);
            return prefixesUnique.Select(p => new ImageTypeBlobSource
            {
                ImageTypeName = p, 
                ContainerName = containerName, 
                ContainerUrl = containerUrl, 
                BlobPrefix = $"{p}/", 
                BlobSource = new AzureBlobContentSource(containerUri) { Prefix = $"{p}/" }
            }).ToArray();
        }

        string GetContainerUrl(string accountEndpoint, string containerName, AuthData authData, BlobServiceClient blobClient, TimeSpan sasTtl)
        {
            var sasToken = authData.OcrToBlobAuthMethod == AuthMethod.ApiKey ? GetSasToken(authData, containerName, blobClient, sasTtl) : null;

            var containerUrl = $"{accountEndpoint}/{HttpUtility.UrlEncode(containerName)}";

            return !string.IsNullOrEmpty(sasToken) ? $"{containerUrl}?{sasToken}" : containerUrl;
        }

        string GetSasToken(AuthData authData, string containerName, BlobServiceClient blobServiceClient, TimeSpan sasTtl)
        {
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                Resource = "c", // "c" for container
                ExpiresOn = DateTimeOffset.UtcNow.Add(sasTtl)
            };

            // Permissions: read, write, list, etc.
            sasBuilder.SetPermissions(BlobContainerSasPermissions.Read |
                                      BlobContainerSasPermissions.Write |
                                      BlobContainerSasPermissions.List);

            // Build the SAS URI
            string sasToken = sasBuilder
                .ToSasQueryParameters(authData.BlobCredential)
                .ToString();

            return sasToken;
        }

        string GetStorageAccountEndpoint(string accountName)
        {
            return $"https://{accountName}.blob.core.windows.net";
        }

        private static AuthMethod GetDiToSaAuthMethod(string? authMethodRaw)
        {
            return authMethodRaw?.ToLowerInvariant() switch
            {
                "sas" => AuthMethod.ApiKey,
                "managedidentity" => AuthMethod.ManagedIdentity,
                _ => throw new ArgumentException(
                    $"Invalid value for {AgentContentParameters.DocumentIntelligenceToStorageAccountAuthMethod}")
            };
        }

        private static string? GetParameterValueOrNull(AgentModel agent, string optionName)
        {
            return !agent.Content.ContainsKey(optionName)
                ? null
                : string.IsNullOrWhiteSpace(agent.Content[optionName].Value)
                    ? null
                    : agent.Content[optionName].Value;
        }

        AuthData GetAuthData(string accountName, string? storageAccountKey, string? ocrApiKey, string? diToSaAuthMethodRaw)
        {
            var diToSaAuthMethod = GetDiToSaAuthMethod(diToSaAuthMethodRaw);

            //Temporary solution.
            //TODO: Rewrite this after implementing project-wide support of Managed Identity.
            var saAuthMethod = string.IsNullOrWhiteSpace(storageAccountKey) ? AuthMethod.ManagedIdentity : AuthMethod.ApiKey;
            var ocrAuthMethod = string.IsNullOrWhiteSpace(ocrApiKey) ? AuthMethod.ManagedIdentity : AuthMethod.ApiKey;

            return new AuthData
            {
                BlobAuthMethod = saAuthMethod,
                TokenCredential = new ManagedIdentityCredential(),
                BlobCredential = saAuthMethod == AuthMethod.ApiKey ? new StorageSharedKeyCredential(accountName, storageAccountKey) : null,
                StorageAccountKey = storageAccountKey,
                OcrCredential = ocrAuthMethod == AuthMethod.ApiKey ? new AzureKeyCredential(ocrApiKey) : null,
                OcrAuthMethod = ocrAuthMethod,
                OcrToBlobAuthMethod = diToSaAuthMethod
            };
        }

        #region GetClients

        DocumentIntelligenceClient GetOcrClient(string ocrEndpoint, AuthData authData)
        {
            var ocrEndpointUri = new Uri(ocrEndpoint);

            //var clientOptions = new DocumentIntelligenceClientOptions
            //{
            //    Transport = new HttpClientTransport(_httpClientFactory.CreateClient(HttpClientName))
            //};

            return authData.BlobAuthMethod == AuthMethod.ApiKey
                ? new DocumentIntelligenceClient(ocrEndpointUri, authData.OcrCredential)
                : new DocumentIntelligenceClient(ocrEndpointUri, authData.TokenCredential);
        }

        DocumentIntelligenceAdministrationClient GetOcrAdministrationClient(string ocrEndpoint, AuthData authData)
        {
            var ocrEndpointUri = new Uri(ocrEndpoint);

            //var clientOptions = new DocumentIntelligenceClientOptions
            //{
            //    Transport = new HttpClientTransport(_httpClientFactory.CreateClient(HttpClientName))
            //};

            return authData.BlobAuthMethod == AuthMethod.ApiKey
                ? new DocumentIntelligenceAdministrationClient(ocrEndpointUri, authData.OcrCredential)
                : new DocumentIntelligenceAdministrationClient(ocrEndpointUri, authData.TokenCredential);
        }

        BlobServiceClient GetBlobServiceClient(string accountEndpoint, AuthData authData)
        {
            var accountUri = new Uri(accountEndpoint);

            // Using of HttpClientName leads to infinite retrying even if a request succeeds
            
            //var options= new BlobClientOptions
            //{
            //    Transport = new HttpClientTransport(_httpClientFactory.CreateClient(HttpClientName))
            //};
            
            return authData.BlobAuthMethod == AuthMethod.ApiKey
                ? new BlobServiceClient(accountUri, authData.BlobCredential)
                : new BlobServiceClient(accountUri, authData.TokenCredential);
        }

        #endregion

        #region Types

        class AuthData
        {
            public TokenCredential? TokenCredential { get; set; }

            public StorageSharedKeyCredential? BlobCredential { get; set; }

            public AuthMethod BlobAuthMethod { get; set; }

            public string? StorageAccountKey { get; set; }

            public AzureKeyCredential OcrCredential { get; set; }

            public AuthMethod OcrAuthMethod { get; set; }

            public AuthMethod OcrToBlobAuthMethod { get; set; }
        }

        class ImageTypeBlobSource
        {
            public string ImageTypeName { get; set; }

            public AzureBlobContentSource BlobSource { get; set; }

            public string BlobPrefix { get; set; }

            public string ContainerName { get; set; }

            public string ContainerUrl { get; set; }
        }
        
        enum AuthMethod
        {
            ManagedIdentity,
            ApiKey
        }

        #endregion
    }

    public interface IOcrBuildClassifierAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
