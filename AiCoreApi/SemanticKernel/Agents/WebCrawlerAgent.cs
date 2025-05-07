using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using HtmlAgilityPack;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using AiCoreApi.Common.Extensions;
using System.Text.Encodings.Web;
using AiCoreApi.Common.Monitoring;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class WebCrawlerAgent : BaseAgent, IWebCrawlerAgent
    {
        private string _debugMessageSenderName = "WebCrawlerAgent";

        private static class AgentContentParameters
        {
            public const string Url = "url";
            public const string CustomHeaders = "customHeaders";
            public const string CrawlDepth = "crawlDepth";
            public const string CrawlUrlRegex = "crawlUrlRegex";
            public const string UserAgent = "userAgent";
            public const string MaxUrlsCount = "maxUrlsCount";
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResponseAccessor _responseAccessor;

        public WebCrawlerAgent(
            ILogger<WebCrawlerAgent> logger,
            MonitoringConfig monitoringConfig,
            IHttpClientFactory httpClientFactory,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor) : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var startUrl = ApplyParameters(agent.Content[AgentContentParameters.Url].Value, parameters);
            var crawlDepth = GetCrawlDepth(agent, parameters);
            var crawlRegex = GetCrawlRegex(agent, parameters);
            var maxUrls = GetMaxUrlsCount(agent, parameters);
            var userAgent = agent.Content.ContainsKey(AgentContentParameters.UserAgent)
                ? ApplyParameters(agent.Content[AgentContentParameters.UserAgent].Value, parameters)
                : "";

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allResults = new List<Dictionary<string, string>>();

            async Task Crawl(string url, int depth, Regex? filter)
            {
                try
                {
                    url = url.TrimEnd(' ', '/', '#', '?');
                    if (depth < 1 || visited.Contains(url) || (maxUrls > 0 && visited.Count >= maxUrls)) return;
                    visited.Add(url);

                    var text = await GetPageTextAsync(url, agent, userAgent, parameters);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        allResults.Add(new Dictionary<string, string>
                        {
                            { "url", url },
                            { "text", text }
                        });
                    }

                    if (depth > 1)
                    {
                        var links = await ExtractLinksAsync(url, agent, userAgent, parameters);
                        foreach (var link in links)
                        {
                            if (!visited.Contains(link) && (filter == null || filter.IsMatch(link)))
                            {
                                await Crawl(link, depth - 1, filter);
                                if (maxUrls > 0 && visited.Count >= maxUrls) break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Suppress errors as links can be broken or inaccessible
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to crawl {url}, {ex.Message}");
                }
            }

            await Crawl(startUrl, crawlDepth, crawlRegex);

            var json = JsonSerializer.Serialize(allResults, new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Final Extracted JSON", json);

            return json;
        }

        private int GetCrawlDepth(AgentModel agent, Dictionary<string, string> parameters)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.CrawlDepth, out var depthVal)
                && int.TryParse(ApplyParameters(depthVal.Value, parameters), out var depth))
                return Math.Max(1, depth);
            return 1;
        }

        private int GetMaxUrlsCount(AgentModel agent, Dictionary<string, string> parameters)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.MaxUrlsCount, out var maxUrlsVal)
                && int.TryParse(ApplyParameters(maxUrlsVal.Value, parameters), out var max))
                return Math.Max(0, max);
            return 0;
        }

        private Regex? GetCrawlRegex(AgentModel agent, Dictionary<string, string> parameters)
        {
            if (agent.Content.TryGetValue(AgentContentParameters.CrawlUrlRegex, out var regexValue))
            {
                var pattern = ApplyParameters(regexValue.Value, parameters).Trim();
                if (!string.IsNullOrEmpty(pattern) && pattern != "1")
                {
                    try
                    {
                        return new Regex(pattern, RegexOptions.IgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Regex pattern: {pattern}, {ex.Message}");
                    }
                }
            }
            return null;
        }

        private async Task<string> GetPageTextAsync(string url, AgentModel agent, string userAgent, Dictionary<string, string> parameters)
        {
            using var client = _httpClientFactory.CreateClient("NoRetryClient");
            ApplyCustomHeaders(client, agent, userAgent, parameters);

            try
            {
                var html = await client.GetCompressedStringAsync(url);
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

        private async Task<List<string>> ExtractLinksAsync(string url, AgentModel agent, string userAgent, Dictionary<string, string> parameters)
        {
            using var client = _httpClientFactory.CreateClient("NoRetryClient");
            ApplyCustomHeaders(client, agent, userAgent, parameters);
            var links = new List<string>();

            try
            {
                var html = await client.GetCompressedStringAsync(url);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var baseUri = new Uri(url);

                var anchorTags = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchorTags == null) return links;

                foreach (var a in anchorTags)
                {
                    var href = a.GetAttributeValue("href", "");
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    if (href.StartsWith("http"))
                        links.Add(href);
                    else
                        links.Add(new Uri(baseUri, href).ToString());
                }
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to extract links from {url}, {ex.Message}");
            }

            return links.Distinct().ToList();
        }

        private void ApplyCustomHeaders(HttpClient client, AgentModel agent, string userAgent, Dictionary<string, string> parameters)
        {
            if (!string.IsNullOrEmpty(userAgent))
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);

            if (!agent.Content.TryGetValue(AgentContentParameters.CustomHeaders, out var headerValue))
                return;

            var decoded = ApplyParameters(headerValue.Value, parameters);
            var parts = decoded.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var kv = part.Split(':', 2);
                if (kv.Length == 2)
                {
                    var name = kv[0].Trim();
                    var value = kv[1].Trim();
                    if (!client.DefaultRequestHeaders.Contains(name))
                        client.DefaultRequestHeaders.Add(name, value);
                }
            }
        }
    }

    public interface IWebCrawlerAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
