using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using HtmlAgilityPack;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using AiCoreApi.Common.Extensions;
using System.Text.Encodings.Web;
using Microsoft.Playwright;
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
            public const string Engine = "engine";
            public const string WaitTimeout = "waitTimeout";
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ExtendedConfig _extendedConfig;

        public WebCrawlerAgent(
            ILogger<WebCrawlerAgent> logger,
            ExtendedConfig extendedConfig,
            MonitoringConfig monitoringConfig,
            IHttpClientFactory httpClientFactory,
            ResponseAccessor responseAccessor,
            RequestAccessor requestAccessor) : base(responseAccessor, requestAccessor, monitoringConfig, logger)
        {
            _httpClientFactory = httpClientFactory;
            _responseAccessor = responseAccessor;
            _extendedConfig = extendedConfig;
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
            var engine = agent.Content.TryGetValue(AgentContentParameters.Engine, out var engineVal)
                ? ApplyParameters(engineVal.Value, parameters).ToLower()
                : "html";
            var waitTimeout = Convert.ToInt32(agent.Content.TryGetValue(AgentContentParameters.WaitTimeout, out var waitTimeoutVal)
                ? ApplyParameters(waitTimeoutVal.Value, parameters).ToLower()
                : "10000");


            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allResults = new List<Dictionary<string, string>>();

            IBrowser? playwrightBrowser = null;
            IBrowserContext? playwrightContext = null;
            IPage? sharedPage = null;

            if (engine == "playwright")
            {
                PlaywrightInstall.EnsureInstalled();
                var playwright = await Playwright.CreateAsync();
                playwrightBrowser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

                playwrightContext = await playwrightBrowser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = string.IsNullOrWhiteSpace(userAgent)
                        ? "Mozilla/5.0 (compatible; WebCrawlerAgent/1.0)"
                        : userAgent,
                    Proxy = string.IsNullOrEmpty(_extendedConfig.Proxy)
                        ? null
                        : new Proxy { Server = _extendedConfig.Proxy },
                    IgnoreHTTPSErrors = true,
                });

                sharedPage = await playwrightContext.NewPageAsync();
            }

            async Task Crawl(string url, int depth, Regex? filter)
            {
                try
                {
                    url = url.TrimEnd(' ', '/', '#', '?');
                    if (depth < 1 || visited.Contains(url) || (maxUrls > 0 && visited.Count >= maxUrls)) return;
                    visited.Add(url);

                    var result = engine == "playwright"
                        ? await GetPageContentAndLinksWithPlaywrightAsync(url, sharedPage!, waitTimeout)
                        : await GetTextAndLinksWithHtmlAgilityPackAsync(url, agent, userAgent, parameters);

                    if (!string.IsNullOrWhiteSpace(result.text))
                    {
                        allResults.Add(new Dictionary<string, string>
                        {
                            { "url", url },
                            { "text", result.text }
                        });
                    }

                    if (depth > 1)
                    {
                        foreach (var link in result.links)
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
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to crawl {url}, {ex.Message}");
                }
            }

            try
            {
                await Crawl(startUrl, crawlDepth, crawlRegex);
            }
            finally
            {
                if (sharedPage != null) await sharedPage.CloseAsync();
                if (playwrightContext != null) await playwrightContext.CloseAsync();
                if (playwrightBrowser != null) await playwrightBrowser.CloseAsync();
            }

            var json = JsonSerializer.Serialize(allResults, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

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

        private async Task<(string text, List<string> links)> GetTextAndLinksWithHtmlAgilityPackAsync(
            string url,
            AgentModel agent,
            string userAgent,
            Dictionary<string, string> parameters)
        {
            var client = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
            ApplyCustomHeaders(client, agent, userAgent, parameters);

            var links = new List<string>();

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
                var cleanedText = string.Join("\n",
                    text.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)));

                var baseUri = new Uri(url);
                var anchorTags = doc.DocumentNode.SelectNodes("//a[@href]");
                if (anchorTags != null)
                {
                    foreach (var a in anchorTags)
                    {
                        var href = a.GetAttributeValue("href", "");
                        if (string.IsNullOrWhiteSpace(href)) continue;

                        var fullUri = href.StartsWith("http")
                            ? href
                            : new Uri(baseUri, href).ToString();
                        links.Add(fullUri);
                    }
                }

                return (cleanedText, links.Distinct().ToList());
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Failed to crawl {url}, {ex.Message}");
                return (string.Empty, new List<string>());
            }
        }

        private async Task<(string text, List<string> links)> GetPageContentAndLinksWithPlaywrightAsync(string url, IPage page, int waitTimeout)
        {
            try
            {
                await page.GotoAsync(url, new() { Timeout = waitTimeout });

                var allText = new List<string>();
                var allLinks = new HashSet<string>();
                var baseUri = new Uri(url);

                // Helper to process any frame (main or iframe)
                async Task ProcessFrame(IFrame frame)
                {
                    var content = await frame.ContentAsync();
                    var doc = new HtmlDocument();
                    doc.LoadHtml(content);

                    doc.DocumentNode.Descendants()
                        .Where(n => n.Name == "script" || n.Name == "style")
                        .ToList()
                        .ForEach(n => n.Remove());

                    var frameText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
                    allText.AddRange(frameText
                        .Split('\n')
                        .Select(line => line.Trim())
                        .Where(line => !string.IsNullOrWhiteSpace(line)));

                    var elements = await frame.QuerySelectorAllAsync("a[href]");
                    foreach (var element in elements)
                    {
                        var href = await element.GetAttributeAsync("href");
                        if (string.IsNullOrWhiteSpace(href)) continue;

                        var fullUri = Uri.TryCreate(href, UriKind.Absolute, out var abs)
                            ? abs.ToString()
                            : new Uri(baseUri, href).ToString();

                        allLinks.Add(fullUri);
                    }
                }

                // Process main page + all iframes
                foreach (var frame in page.Frames)
                {
                    await ProcessFrame(frame);
                }

                return (string.Join("\n", allText), allLinks.ToList());
            }
            catch (Exception ex)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Error", $"Playwright failed for {url}, {ex.Message}");
                return (string.Empty, new List<string>());
            }
        }
    }

    public interface IWebCrawlerAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
