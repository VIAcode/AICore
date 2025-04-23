using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Text;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using System.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class ContentSafetyAgent : BaseAgent, IContentSafetyAgent
    {
        private string _debugMessageSenderName = "ContentSafetyAgent";
        private static class AgentContentParameters
        {
            public const string ContentSafetyConnection = "contentSafetyConnection";
            public const string Hate = "hate";
            public const string SelfHarm = "selfHarm";
            public const string Sexual = "sexual";
            public const string Violence = "violence";
            public const string ProtectedMaterial = "protectedMaterial";
            public const string JailBreakAttack = "jailBreakAttack";
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IConnectionProcessor _connectionProcessor;

        public ContentSafetyAgent(
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory, 
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor,
            ExtendedConfig extendedConfig,
            ILogger<ContentSafetyAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
            _requestAccessor = requestAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var connectionName = agent.Content[AgentContentParameters.ContentSafetyConnection].Value;
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var contentSafetyConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.ContentSafety, _debugMessageSenderName, connectionName: connectionName);
            var apiKey = contentSafetyConnection.Content["apiKey"];
            var contentSafetyUrl = contentSafetyConnection.Content["contentSafetyUrl"].TrimEnd('/');
            var hate = Convert.ToInt32(agent.Content[AgentContentParameters.Hate].Value);
            var selfHarm = Convert.ToInt32(agent.Content[AgentContentParameters.SelfHarm].Value);
            var sexual = Convert.ToInt32(agent.Content[AgentContentParameters.Sexual].Value);
            var violence = Convert.ToInt32(agent.Content[AgentContentParameters.Violence].Value);
            var protectedMaterial = Convert.ToBoolean(agent.Content[AgentContentParameters.ProtectedMaterial].Value);
            var jailBreakAttack = Convert.ToBoolean(agent.Content[AgentContentParameters.JailBreakAttack].Value);

            var textToAnalyze = parameters["parameter1"];
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request", 
                $"Content Safety Analysis for: {textToAnalyze} \r\n\r\nAPI Url: {contentSafetyUrl} \r\nHate: {hate}\r\nSelfHarm: {selfHarm}\r\nSexual: {sexual}\r\nViolence: {violence}\r\nProtectedMaterial: {protectedMaterial}\r\nJailBreakAttack: {jailBreakAttack}");

            var foundContentSafetyIssues = new List<string>();
            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
            async Task DetectContentSafetyIssue(string endpoint, string debugMessage, string body, string jsonPath, string issueName)
            {
                var uri = new Uri($"{contentSafetyUrl}/contentsafety/{endpoint}?api-version=2024-09-01");
                using var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, uri);
                httpRequestMessage.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await httpClient.SendAsync(httpRequestMessage);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, $"DoCall {debugMessage}", responseBody);
                var detected = responseBody.JsonGet<bool>(jsonPath);
                if (detected)
                {
                    foundContentSafetyIssues.Add(issueName);
                }
            }
            if (protectedMaterial)
            {
                var body = new { text = textToAnalyze }.ToJson();
                await DetectContentSafetyIssue("text:detectProtectedMaterial", "DetectProtectedMaterial", body, "protectedMaterialAnalysis.detected", "Protected Material");
            }
            if (jailBreakAttack)
            {
                var body = new { userPrompt = textToAnalyze }.ToJson();
                await DetectContentSafetyIssue("text:shieldPrompt", "DetectJailBreakAttack", body, "userPromptAnalysis.attackDetected", "Jail Break Attack");
            }
            if (hate < 6 || selfHarm < 6 || sexual < 6 || violence < 6)
            {
                var uri = new Uri($"{contentSafetyUrl}/contentsafety/text:analyze?api-version=2024-09-01");
                using var httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, uri);
                var body = new { text = textToAnalyze }.ToJson();
                httpRequestMessage.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await httpClient.SendAsync(httpRequestMessage);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Analyze", responseBody);
                var analysis = responseBody.JsonGet<CategoriesAnalysisResponse>();
                foreach (var item in analysis.CategoriesAnalysis)
                {
                    if(item.Category == "Hate" && item.Severity > hate)
                        foundContentSafetyIssues.Add("Hate");
                    if(item.Category == "SelfHarm" && item.Severity > selfHarm)
                        foundContentSafetyIssues.Add("Self Harm");
                    if(item.Category == "Sexual" && item.Severity > sexual)
                        foundContentSafetyIssues.Add("Sexual");
                    if(item.Category == "Violence" && item.Severity > violence)
                        foundContentSafetyIssues.Add("Violence");
                }
            }
            var result = foundContentSafetyIssues.Count == 0
                ? "True"
                : $"False: {string.Join(", ", foundContentSafetyIssues)}";
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        public class CategoriesAnalysisResponse
        {
            public List<ContentAnalysisAnswer> CategoriesAnalysis { get; set; }
        }
        public class ContentAnalysisAnswer
        {
            public string Category { get; set; } = "";
            public int Severity { get; set; }
        }
    }

    public interface IContentSafetyAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
