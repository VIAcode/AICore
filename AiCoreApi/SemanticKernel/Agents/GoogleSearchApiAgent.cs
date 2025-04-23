using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using HtmlAgilityPack;
using System.Text.Json;
using AiCoreApi.Common.Extensions;
using System.Text.Encodings.Web;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class GoogleSearchApiAgent : BaseAgent, IGoogleSearchApiAgent
    {
        private readonly string _googleSearchUrl = "https://www.googleapis.com/customsearch/v1";
        private string _debugMessageSenderName = "GoogleSearchApiAgent";

        public static class AgentPromptPlaceholders
        {
            public const string HasFilesPlaceholder = "hasFiles";
            public const string FilesNamesPlaceholder = "filesNames";
            public const string FilesDataPlaceholder = "filesData";
        }

        private static class AgentContentParameters
        {
            public const string QueryString = "queryString";
            public const string GoogleConnection = "googleSearchApiConnection";
            public const string MaxContentLength = "maxContentLength";
            public const string Count = "count"; 
            public const string Offset = "offset"; 
            public const string OutputType = "outputType";
        }

        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionProcessor _connectionProcessor;

        public GoogleSearchApiAgent(
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IHttpClientFactory httpClientFactory,
            IConnectionProcessor connectionProcessor,
            ExtendedConfig extendedConfig,
            ILogger<GoogleSearchApiAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
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
            var maxContentLength = agent.Content.ContainsKey(AgentContentParameters.MaxContentLength)
                ? ApplyParameters(agent.Content[AgentContentParameters.MaxContentLength].Value, parameters)
                : "16384";

            queryString = ApplyParameters(queryString, new Dictionary<string, string>
            {
                { AgentPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().HasFiles().ToString() },
                { AgentPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileContents() },
                { AgentPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileNames() }
            });

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Query String", queryString);

            var googleConnectionName = agent.Content[AgentContentParameters.GoogleConnection].Value;
            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var googleConnection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.GoogleSearchApi, _debugMessageSenderName, connectionName: googleConnectionName);

            var count = int.Parse(ApplyParameters(agent.Content[AgentContentParameters.Count].Value, parameters));
            var offset = int.Parse(ApplyParameters(agent.Content[AgentContentParameters.Offset].Value, parameters));
            var outputType = agent.Content.TryGetValue(AgentContentParameters.OutputType, out var ot) ? ot.Value : "snippetTexts";
            var results = await DoSearchAsync(queryString, googleConnection.Content["apiKey"], googleConnection.Content["googleCxId"], count, offset);

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
                    if (text.Length > int.Parse(maxContentLength))
                        text = text.Substring(0, int.Parse(maxContentLength));

                    pages.Add(new Dictionary<string, string> { { "url", page.Url }, { "text", text } });
                }
                result = JsonSerializer.Serialize(pages, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            else
            {
                result = JsonSerializer.Serialize(results.Select(r => r.Snippet).ToList(), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Query String Result", result);
            return result;
        }

        private async Task<List<WebPage>> DoSearchAsync(string query, string apiKey, string cx, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count <= 0 || count > 10)
                throw new ExceptionHandlingMiddleware.AiCoreUiException($"Google Search API only allows up to 10 results per request. Now: {count}");

            var start = offset + 1;
            var uri = new Uri($"{_googleSearchUrl}?key={apiKey}&cx={cx}&q={Uri.EscapeDataString(query)}&num={count}&start={start}");

            using var response = await SendGetRequestAsync(uri, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
                return new List<WebPage>();

            var results = new List<WebPage>();
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new WebPage
                {
                    Name = item.GetProperty("title").GetString() ?? "",
                    Url = item.GetProperty("link").GetString() ?? "",
                    Snippet = item.GetProperty("snippet").GetString() ?? ""
                });
            }

            return results;
        }

        private async Task<string> CrawlPageTextAsync(string url)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("NoRetryClient");
                var html = await client.GetCompressedStringAsync(url);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                doc.DocumentNode.Descendants()
                    .Where(n => n.Name == "script" || n.Name == "style")
                    .ToList()
                    .ForEach(n => n.Remove());
                var rawText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
                var cleaned = new string(
                    rawText
                        .Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t')
                        .ToArray()
                );
                var lines = cleaned
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l));

                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to crawl {url}, {ex.Message}");
                return string.Empty;
            }
        }


        private async Task<HttpResponseMessage> SendGetRequestAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            var httpClient = _httpClientFactory.CreateClient("RetryClient");
            var httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
            return await httpClient.SendAsync(httpRequestMessage, cancellationToken).ConfigureAwait(false);
        }

        public class WebPage
        {
            public string Name { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
        }
    }

    public interface IGoogleSearchApiAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
