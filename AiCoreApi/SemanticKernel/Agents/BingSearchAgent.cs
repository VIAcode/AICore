using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using HtmlAgilityPack;
using System.Text.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class BingSearchAgent : BaseAgent, IBingSearchAgent
    {
        private string _debugMessageSenderName = "BingSearchAgent";
        private readonly Uri? _uri = new("https://api.bing.microsoft.com/v7.0/search?q");

        public static class AgentPromptPlaceholders
        {
            public const string HasFilesPlaceholder = "hasFiles";
            public const string FilesNamesPlaceholder = "filesNames";
            public const string FilesDataPlaceholder = "filesData";
        }

        private static class AgentContentParameters
        {
            public const string QueryString = "queryString";
            public const string BingConnection = "bingConnection";
            public const string Count = "count";
            public const string OutputType = "outputType";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public BingSearchAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<BingSearchAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _httpClientFactory = httpClientFactory;
            _connectionProcessor = connectionProcessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var queryString = ApplyParameters(agent.Content[AgentContentParameters.QueryString].Value, parameters);
            queryString = ApplyParameters(queryString, new Dictionary<string, string>
            {
                {AgentPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().HasFiles().ToString()},
                {AgentPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileContents()},
                {AgentPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileNames()}
            });
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Query String", queryString);

            var bingConnectionName = agent.Content[AgentContentParameters.BingConnection].Value;
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var bingConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.BingApi, _debugMessageSenderName, connectionName: bingConnectionName);

            var count = int.Parse(agent.Content[AgentContentParameters.Count].Value);
            var outputType = agent.Content.TryGetValue(AgentContentParameters.OutputType, out var ot) ? ot.Value : "snippetTexts";
            var results = await DoSearchAsync(queryString, bingConnection.Content["bingApiKey"], count);

            string result;
            if (outputType == "snippetJson")
            {
                var jsonList = results.Select(r => new { url = r.Url, text = r.Snippet }).ToList();
                result = JsonSerializer.Serialize(jsonList);
            }
            else if (outputType == "pagesJson")
            {
                var pages = new List<Dictionary<string, string>>();
                foreach (var page in results)
                {
                    var text = await CrawlPageTextAsync(page.Url);
                    pages.Add(new Dictionary<string, string> { { "url", page.Url }, { "text", text } });
                }
                result = JsonSerializer.Serialize(pages);
            }
            else // default: snippetTexts
            {
                result = JsonSerializer.Serialize(results.Select(r => r.Snippet).ToList());
            }

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Query String Result", result);
            return result;
        }

        private async Task<string> CrawlPageTextAsync(string url)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("NoRetryClient");
                var html = await client.GetStringAsync(url);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                doc.DocumentNode.Descendants()
                    .Where(n => n.Name == "script" || n.Name == "style")
                    .ToList()
                    .ForEach(n => n.Remove());

                var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
                return string.Join("\n",
                    text.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l)));
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to crawl {url}, {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<List<WebPage>> DoSearchAsync(string query, string apiKey, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 50)
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 50.");

            var uri = new Uri($"{_uri}={Uri.EscapeDataString(query.Trim())}&count={count}&offset={offset}");
            using var response = await SendGetRequestAsync(uri, apiKey, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var webPages = json.JsonGet<List<WebPage>>("webPages.value");
            return webPages ?? new List<WebPage>();
        }

        private async Task<HttpResponseMessage> SendGetRequestAsync(Uri uri, string apiKey, CancellationToken cancellationToken = default)
        {
            using var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(apiKey))
                httpRequestMessage.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
            else
                throw new InvalidOperationException("Bing API key is not set.");
            using var httpClient = _httpClientFactory.CreateClient("RetryClient");
            return await httpClient.SendAsync(httpRequestMessage, cancellationToken).ConfigureAwait(false);
        }

        public class WebPage
        {
            public string Name { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
        }
    }

    public interface IBingSearchAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
