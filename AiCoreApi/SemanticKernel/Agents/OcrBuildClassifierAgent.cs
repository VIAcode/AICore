using System.Text.Json;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Azure.Core;
using Azure.Storage;
using Azure;
using Microsoft.SemanticKernel;
using System.Web;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using System.Text.RegularExpressions;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OcrBuildClassifierAgent: BaseAgent, IOcrBuildClassifierAgent
    {
        private static readonly HashSet<string> SupportedFileFormats = new([".pdf", ".jpeg", ".jpg", ".png", ".bmp", ".tiff", ".heif"], StringComparer.InvariantCultureIgnoreCase);

        private const string DebugMessageSenderName = "OcrBuildClassifierAgent";

        private const string OcrModelName = "prebuilt-layout";

        private const int DocumentTypesMinCount = 2;

        private static class AgentContentParameters
        {
            public const string DocumentIntelligenceConnection = "documentIntelligenceConnection";
            public const string StorageAccountConnection = "storageAccountConnection";
            public const string ClassifierId = "classifierId";
            public const string BaseClassifierId = "baseClassifierId";
            public const string ContainerName = "containerName";
            public const string DocumentTypes = "documentTypes";
            public const string Action = "action";
        }

        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;

        public OcrBuildClassifierAgent(
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig, 
            ILogger<OcrBuildClassifierAgent> logger) : base(requestAccessor, extendedConfig, logger)
        {
            _entraTokenProvider = entraTokenProvider;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));

            var connections = await _connectionProcessor.List();
            var action = agent.Content.ContainsKey(AgentContentParameters.Action)
                ? agent.Content[AgentContentParameters.Action].Value
                : "buildClassifier";
            if (action == "buildClassifier")
                return await BuildClassifier(connections, agent, parameters);
            return await ReturnDocumentTypes(connections, agent, parameters);
        }

        public async Task<string> ReturnDocumentTypes(
            List<ConnectionModel> connections,
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            var diConnectionName = agent.Content[AgentContentParameters.DocumentIntelligenceConnection].Value;
            var ocrConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.DocumentIntelligence, DebugMessageSenderName, connectionName: diConnectionName);
            var classifierId = ApplyParameters(agent.Content[AgentContentParameters.ClassifierId].Value, parameters);

            var ocrEndpoint = ocrConnection.Content["endpoint"];
            var ocrEndpointUri = new Uri(ocrEndpoint);

            var ocrAccessType = ocrConnection.Content.ContainsKey("accessType") ? ocrConnection.Content["accessType"] : "apiKey";
            var ocrApiKey = ocrConnection.Content.ContainsKey("apiKey") ? ocrConnection.Content["apiKey"] : string.Empty;

            DocumentIntelligenceAdministrationClient adminClient;
            if (ocrAccessType == "apiKey")
            {
                adminClient = new DocumentIntelligenceAdministrationClient(ocrEndpointUri, new AzureKeyCredential(ocrApiKey));
            }
            else
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(ocrAccessType, "https://cognitiveservices.azure.com/.default");
                adminClient = new DocumentIntelligenceAdministrationClient(ocrEndpointUri, new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn));
            }

            var classifier = await adminClient.GetClassifierAsync(classifierId);
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Build Classifier", $"Document Types: {ocrEndpoint}\nClassifier ID: {classifierId}");

            var documentTypes = classifier.Value.DocumentTypes.Select(docType => docType.Key).ToList();
            var result = documentTypes.ToJson() ?? "";
            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Build Classifier Result", $"Document Types: {result}");
            return result;
        }

        public async Task<string> BuildClassifier(
            List<ConnectionModel> connections,
            AgentModel agent,
            Dictionary<string, string> parameters)
        {
            var diConnectionName = agent.Content[AgentContentParameters.DocumentIntelligenceConnection].Value;
            var saConnectionName = agent.Content[AgentContentParameters.StorageAccountConnection].Value;
            var ocrConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.DocumentIntelligence, DebugMessageSenderName, connectionName: diConnectionName);
            var blobConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.StorageAccount, DebugMessageSenderName, connectionName: saConnectionName);
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

            var ocrEndpoint = ocrConnection.Content["endpoint"];
            var ocrAccessType = ocrConnection.Content.ContainsKey("accessType") 
                ? ocrConnection.Content["accessType"] 
                : "apiKey";
            var ocrApiKey = ocrConnection.Content.ContainsKey("apiKey")
                ? ocrConnection.Content["apiKey"]
                : string.Empty;

            var blobAccountName = blobConnection.Content["accountName"];
            var blobAccessType = blobConnection.Content.ContainsKey("accessType") 
                ? blobConnection.Content["accessType"] 
                : "apiKey";
            var blobAccountKey = blobConnection.Content.ContainsKey("apiKey")
                ? blobConnection.Content["apiKey"]
                : string.Empty;

            var authData = GetAuthData(
                blobAccessType, 
                blobAccountName, 
                blobAccountKey, 
                ocrAccessType, 
                ocrApiKey);

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Build Classifier", $"Document Intelligence: {ocrEndpoint}\nClassifier ID: {classifierId}\nBase Classifier ID: {baseClassifierId}\nDocument Types: {JsonSerializer.Serialize(documentTypes)}\nStorage Account: {blobAccountName}\nContainer Name: {containerName}");

            var buildWarnings = await BuildClassifier(
                ocrEndpoint,
                classifierId,
                baseClassifierId,
                blobAccountName,
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
            var containerUrl = GetContainerUrl(saEndpoint, containerName);
            var blobSources = GetBlobSources(documentTypes, containerName, containerUrl);
            await PrepareBlobs(saEndpoint, blobSources, OcrModelName, diEndpoint, authData, SupportedFileFormats);
            var result = await BuildClassifier(blobSources, classifierId, baseClassifierId, diEndpoint, authData);
            return result?.Warnings == null ? string.Empty : JsonSerializer.Serialize(result.Warnings);
        }

        private async Task<DocumentClassifierDetails> BuildClassifier(ICollection<ImageTypeBlobSource> blobSources, string classifierId, string baseClassifierId, string ocrEndpoint, AuthData authData, CancellationToken cancellationToken = default(CancellationToken))
        {
            var imageTypes = GetDocumentTypes(blobSources);
            var buildOptions = new BuildClassifierOptions(classifierId, imageTypes)
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

        private Dictionary<string, ClassifierDocumentTypeDetails> GetDocumentTypes(ICollection<ImageTypeBlobSource> blobSourceItems) => 
            blobSourceItems.ToDictionary(b => b.ImageTypeName, b => new ClassifierDocumentTypeDetails(b.BlobSource));

        private async Task PrepareBlobs(string saEndpoint, ICollection<ImageTypeBlobSource> blobSources, string modelName, string ocrEndpoint, AuthData authData, HashSet<string> supportedFileExtensions, CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var bsi in blobSources)
            {
                var blobServiceClient = GetBlobServiceClient(saEndpoint, authData);
                var containerClient = blobServiceClient.GetBlobContainerClient(bsi.ContainerName);
                var ocrClient = GetOcrClient(ocrEndpoint, authData);

                await foreach (var blob in containerClient
                                   .GetBlobsAsync(prefix: bsi.BlobPrefix, cancellationToken: cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (!supportedFileExtensions.Contains(Path.GetExtension(blob.Name)))
                        continue;

                    try
                    {
                        
                        var blobUri = new Uri(GetBlobUrl(bsi.ContainerUrl, blob.Name));

                        var operation = await ocrClient.AnalyzeDocumentAsync(
                            WaitUntil.Completed, 
                            modelName, 
                            blobUri, 
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        var response = operation.GetRawResponse();

                        var ocrBlobName = blob.Name + ".ocr.json";
                        var ocrBlobClient = containerClient.GetBlobClient(ocrBlobName);

                        await ocrBlobClient
                            .UploadAsync(response.Content, true, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // continue processing of the remaining blobs
                        _responseAccessor.AddDebugMessage(DebugMessageSenderName, "PrepareBlobs Error",
                            $"Error processing blob {blob.Name}: {ex.Message}");
                    }

                }

            }
        }

        private string GetBlobUrl(string containerUrl, string blobName)
        {
            var encodedBlobName = Uri.EscapeDataString(blobName).Replace("%2F", "/");

            // Handle SAS token or any other query parameters
            var queryIndex = containerUrl.IndexOf('?');
            var baseUrl = queryIndex >= 0 ? containerUrl[..queryIndex] : containerUrl;
            var query = queryIndex >= 0 ? containerUrl[queryIndex..] : string.Empty;

            return $"{baseUrl.TrimEnd('/')}/{encodedBlobName}{query}";
        }

        private ICollection<ImageTypeBlobSource> GetBlobSources(ICollection<string> prefixes, string containerName, string containerUrl)
        {
            var prefixesUnique = prefixes.Select(p => Regex.Replace(p, "/+$", "")).ToHashSet(StringComparer.InvariantCultureIgnoreCase);
            var containerUri = new Uri(containerUrl);
            return prefixesUnique.Select(p => new ImageTypeBlobSource
            {
                ImageTypeName = p,
                ContainerName = containerName,
                ContainerUrl = containerUrl,
                BlobPrefix = $"{p}/",
                BlobSource = new BlobContentSource(containerUri) { Prefix = $"{p}/" }
            }).ToArray();
        }

        private string GetContainerUrl(string accountEndpoint, string containerName)
        {
            return $"{accountEndpoint}/{Uri.EscapeDataString(containerName)}";
        }

        private string GetStorageAccountEndpoint(string accountName)
        {
            return $"https://{accountName}.blob.core.windows.net";
        }

        private AuthData GetAuthData(string blobAccessType, string accountName, string? storageAccountKey, string ocrAccessType, string? ocrApiKey)
        {
            var blobAuthIsApiKey = AuthTypeIsApiKey(blobAccessType);
            var ocrAuthIsApiKey = AuthTypeIsApiKey(ocrAccessType);

            if (blobAuthIsApiKey && string.IsNullOrWhiteSpace(storageAccountKey))
            {
                throw new ArgumentException("Storage account key is required for apiKey access type");
            }

            if (ocrAuthIsApiKey && string.IsNullOrWhiteSpace(ocrApiKey))
            {
                throw new ArgumentException("Document Intelligence API key is required for apiKey access type");
            }

            return new AuthData
            {
                BlobAuthIsApiKey = blobAuthIsApiKey,
                BlobTokenCredential = blobAuthIsApiKey ? null : new EntraTokenProviderWrapperCredential(blobAccessType, _entraTokenProvider),
                BlobKeyCredential = blobAuthIsApiKey ? new StorageSharedKeyCredential(accountName, storageAccountKey) : null,
                OcrAuthIsApiKey = ocrAuthIsApiKey,
                OcrKeyCredential = ocrAuthIsApiKey ? new AzureKeyCredential(ocrApiKey) : null,
                OcrTokenCredential = ocrAuthIsApiKey ? null : new EntraTokenProviderWrapperCredential(ocrAccessType, _entraTokenProvider)
            };
        }

        private bool AuthTypeIsApiKey(string authTypeRaw) => authTypeRaw == "apiKey";

        private DocumentIntelligenceClient GetOcrClient(string ocrEndpoint, AuthData authData)
        {
            var ocrEndpointUri = new Uri(ocrEndpoint);
            return authData.OcrAuthIsApiKey
                ? new DocumentIntelligenceClient(ocrEndpointUri, authData.OcrKeyCredential)
                : new DocumentIntelligenceClient(ocrEndpointUri, authData.OcrTokenCredential);
        }

        private DocumentIntelligenceAdministrationClient GetOcrAdministrationClient(string ocrEndpoint, AuthData authData)
        {
            var ocrEndpointUri = new Uri(ocrEndpoint);
            return authData.OcrAuthIsApiKey
                ? new DocumentIntelligenceAdministrationClient(ocrEndpointUri, authData.OcrKeyCredential)
                : new DocumentIntelligenceAdministrationClient(ocrEndpointUri, authData.OcrTokenCredential);
        }

        private BlobServiceClient GetBlobServiceClient(string accountEndpoint, AuthData authData)
        {
            var accountUri = new Uri(accountEndpoint);
            return authData.BlobAuthIsApiKey
                ? new BlobServiceClient(accountUri, authData.BlobKeyCredential)
                : new BlobServiceClient(accountUri, authData.BlobTokenCredential);
        }

        public class EntraTokenProviderWrapperCredential : TokenCredential
        {
            readonly IEntraTokenProvider _tokenProvider;
            readonly string _accessType;

            public EntraTokenProviderWrapperCredential(string accessType, IEntraTokenProvider tokenProvider)
            {
                if (accessType == "apiKey")
                {
                    throw new ArgumentException("ApiKey is not supported for this authentication type");
                }

                _tokenProvider = tokenProvider;
                _accessType = accessType;
            }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => 
                GetTokenAsync(requestContext, cancellationToken).GetAwaiter().GetResult();

            public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => 
                await _tokenProvider.GetAccessTokenObjectAsync(_accessType, requestContext.Scopes[0]);
        }

        private class AuthData
        {
            public bool BlobAuthIsApiKey { get; set; }

            public TokenCredential? BlobTokenCredential { get; set; }
            public StorageSharedKeyCredential? BlobKeyCredential { get; set; }
            public bool OcrAuthIsApiKey { get; set; }
            public AzureKeyCredential OcrKeyCredential { get; set; }
            public TokenCredential? OcrTokenCredential { get; set; }
        }

        class ImageTypeBlobSource
        {
            public string ImageTypeName { get; set; }
            public BlobContentSource BlobSource { get; set; }
            public string BlobPrefix { get; set; }
            public string ContainerName { get; set; }
            public string ContainerUrl { get; set; }
        }
    }

    public interface IOcrBuildClassifierAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
