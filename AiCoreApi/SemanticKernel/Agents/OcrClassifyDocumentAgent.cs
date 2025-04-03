using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.SemanticKernel;
using System.Web;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core.Pipeline;
using Azure.Core;
using static Python.Runtime.TypeSpec;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class OcrClassifyDocumentAgent : BaseAgent, IOcrClassifyDocumentAgent
    {
        private const string DebugMessageSenderName = "OcrClassifyDocumentAgent";

        public static class AgentPromptPlaceholders
        {
            public const string FileDataPlaceholder = "firstFileData";
        }

        private static class AgentContentParameters
        {
            public const string DocumentIntelligenceConnection = "documentIntelligenceConnection";
            public const string Base64Image = "base64Image";
            public const string ClassifierId = "classifierID";
            public const string Pages = "pages";
            public const string SplitMode = "splitMode";
        }

        private readonly IEntraTokenProvider _entraTokenProvider;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public OcrClassifyDocumentAgent(
            IEntraTokenProvider entraTokenProvider,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<OcrClassifyDocumentAgent> logger) : base(requestAccessor, extendedConfig, logger)
        {
            _entraTokenProvider = entraTokenProvider;
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

            var base64Image = ApplyParameters(agent.Content[AgentContentParameters.Base64Image].Value, parameters);
            if (_requestAccessor.MessageDialog != null && _requestAccessor.MessageDialog.Messages!.Last().HasFiles() && base64Image.Contains(AgentPromptPlaceholders.FileDataPlaceholder))
            {
                base64Image = ApplyParameters(base64Image, new Dictionary<string, string>
                {
                    {AgentPromptPlaceholders.FileDataPlaceholder, _requestAccessor.MessageDialog.Messages!.Last().Files!.First().Base64Data},
                });
            }

            var documentIntelligenceConnection = agent.Content[AgentContentParameters.DocumentIntelligenceConnection].Value;
            var connections = await _connectionProcessor.List();
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.DocumentIntelligence, DebugMessageSenderName, connectionName: documentIntelligenceConnection);

            var splitMode = GetParameterValueOrNull(agent, AgentContentParameters.SplitMode);
            var pages = ApplyParameters(GetParameterValueOrNull(agent, AgentContentParameters.Pages), parameters);

            var classifierId = ApplyParameters(agent.Content[AgentContentParameters.ClassifierId].Value, parameters);

            if (string.IsNullOrWhiteSpace(classifierId))
            {
                throw new ArgumentException($"{AgentContentParameters.ClassifierId} cannot be empty");
            }

            var endpoint = connection.Content["endpoint"];
            var accessType = connection.Content.ContainsKey("accessType") ? connection.Content["accessType"] : "apiKey";
            var apiKey = connection.Content.ContainsKey("apiKey") ? connection.Content["apiKey"] : "";

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Classifying", $"{endpoint}, {classifierId}, {apiKey} \r\nSplitMode:{splitMode} Pages:{pages}");

            var result = await ProcessFileAsync(
                endpoint, 
                classifierId,
                accessType,
                apiKey,
                base64Image.StripBase64(),
                splitMode,
                pages
                );

            _responseAccessor.AddDebugMessage(DebugMessageSenderName, "OCR Classifying Result", result);

            return result;
        }

        private static string? GetParameterValueOrNull(AgentModel agent, string optionName)
        {
            return !agent.Content.ContainsKey(optionName)
                ? null
                : string.IsNullOrWhiteSpace(agent.Content[optionName].Value)
                    ? null
                    : agent.Content[optionName].Value;
        }

        private async Task<string?> ProcessFileAsync(
            string modelUrl, 
            string classifierId, 
            string accessType,
            string apiKey, 
            string base64Data, 
            string? splitMode,
            string? pages)
        {
            var file = Convert.FromBase64String(base64Data);
            AzureKeyCredential? keyCredential = null;
            TokenCredential? tokenCredential = null;

            if (accessType.Equals("apiKey", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(apiKey))
                    throw new ArgumentException("API key is required for apiKey access type.");

                keyCredential = new AzureKeyCredential(apiKey);
            }
            else
            {
                var accessToken = await _entraTokenProvider.GetAccessTokenObjectAsync(accessType, "https://cognitiveservices.azure.com/.default");
                tokenCredential = new StaticTokenCredential(accessToken.Token, accessToken.ExpiresOn);
            }

            _httpClientFactory.CreateClient("");
            var clientOptions = new DocumentIntelligenceClientOptions
            {
                Transport = new HttpClientTransport(_httpClientFactory.CreateClient("RetryClient"))
            };
            var client = keyCredential != null
                ? new DocumentIntelligenceClient(new Uri(modelUrl), keyCredential, clientOptions)
                : new DocumentIntelligenceClient(new Uri(modelUrl), tokenCredential!, clientOptions);

            var binaryData = new BinaryData(file);
            var options = new ClassifyDocumentOptions(classifierId, binaryData)
            {
                Split = string.IsNullOrWhiteSpace(splitMode) ? (SplitMode?)null : splitMode,
                Pages = pages
            };

            var operation = await client.ClassifyDocumentAsync(WaitUntil.Completed, options);

            var result = HandleResult(operation.Value);
            return result.ToJson();
        }

        private static OcrClassifyResult HandleResult(AnalyzeResult result)
        {
            var documents = result.Documents
                .Select(doc => new OcrClassifyDocument { DocType = doc.DocumentType, Confidence = doc.Confidence })
                .ToList();

            return new OcrClassifyResult
            {
                Documents = documents
            };
        }
    }

    public class OcrClassifyResult
    {
        public List<OcrClassifyDocument>? Documents { get; set; } = new();
    }

    public class OcrClassifyDocument
    {
        public string DocType { get; set; }

        public float Confidence { get; set; }
    }

    public interface IOcrClassifyDocumentAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
