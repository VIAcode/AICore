using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using Microsoft.Extensions.Caching.Distributed;

namespace AiCoreApi.Services.ControllersServices
{
    public class SpentService : ISpentService
    {
        private const int DaysToShow = 30;

        private readonly ISpentProcessor _spentProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RequestAccessor _requestAccessor;
        private readonly IDistributedCache _cache;

        public SpentService(
            ISpentProcessor spentProcessor,
            ILoginProcessor loginProcessor,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            RequestAccessor requestAccessor,
            IDistributedCache cache)
        {
            _spentProcessor = spentProcessor;
            _loginProcessor = loginProcessor;
            _connectionProcessor = connectionProcessor;
            _httpClientFactory = httpClientFactory;
            _requestAccessor = requestAccessor;
            _cache = cache;
        }

        public async Task<List<SpentItemViewModel>> List()
        {
            var logins = await _loginProcessor.List();
            var connectionList = await _connectionProcessor.List(_requestAccessor.WorkspaceId);

            var llmConnections = connectionList
                .Where(c => c.Type.IsLlmConnection())
                .ToDictionary(
                    c => c.Name,
                    c => new
                    {
                        InputTokenCost = Convert.ToDecimal(c.Content["inputTokenCost"]),
                        OutputTokenCost = Convert.ToDecimal(c.Content["outputTokenCost"])
                    });

            var lastMonthData = await _spentProcessor.ListLastMonth();

            var grouped = lastMonthData.GroupBy(item => item.LoginId)
                .Select(group =>
                {
                    var login = logins.FirstOrDefault(x => x.LoginId == group.Key);
                    var chatGroup = group.ToList();

                    var tokensIncoming = chatGroup.Sum(x => x.TokensIncoming);
                    var tokensOutgoing = chatGroup.Sum(x => x.TokensOutgoing);
                    var costDayByDay = new List<decimal>();

                    decimal runningTotal = 0;

                    for (var i = 0; i < DaysToShow; i++)
                    {
                        var date = DateTime.UtcNow.Date.AddDays(i - (DaysToShow - 1));
                        var dayCost = chatGroup
                            .Where(x => x.Date.Date == date)
                            .Sum(x =>
                                llmConnections.TryGetValue(x.ModelName, out var cost) ?
                                    (x.TokensIncoming * cost.OutputTokenCost + x.TokensOutgoing * cost.InputTokenCost) : 0
                            ) / 1000;

                        runningTotal += dayCost;
                        costDayByDay.Add(runningTotal);
                    }

                    return new SpentItemViewModel
                    {
                        LoginId = group.Key,
                        Login = login?.Login,
                        LoginType = login?.LoginType.ToString(),
                        TokensIncoming = tokensIncoming,
                        TokensOutgoing = tokensOutgoing,
                        Cost = costDayByDay.LastOrDefault(),
                        CostDayByDay = costDayByDay
                    };
                })
                .OrderBy(x => x.LoginId)
                .ToList();

            // Add Total Row
            var totalDayByDay = new decimal[DaysToShow];
            foreach (var item in grouped)
                for (int i = 0; i < DaysToShow; i++)
                    totalDayByDay[i] += item.CostDayByDay[i];

            grouped.Insert(0, new SpentItemViewModel
            {
                LoginId = 0,
                Login = "Total",
                LoginType = "",
                TokensIncoming = grouped.Sum(x => x.TokensIncoming),
                TokensOutgoing = grouped.Sum(x => x.TokensOutgoing),
                Cost = totalDayByDay.LastOrDefault(),
                CostDayByDay = totalDayByDay.ToList()
            });

            return grouped;
        }

        public async Task<List<TokenCostViewModel>> ListTokenCosts()
        {
            var pricesJson = await GetAzurePrices("openai-service");
            var offers = pricesJson.JsonGet<Dictionary<string, LlmOfferModel>>("offers");
            var result = new List<TokenCostViewModel>();
            int id = 0;

            foreach (var (key, promptModel) in offers)
            {
                if (!key.StartsWith("language-models-") || !key.EndsWith("-prompt"))
                    continue;

                var modelName = key.Replace("language-models-", "").Replace("-prompt", "");
                var outputCost = promptModel.Prices.Perthousandapitransactions.Values.FirstOrDefault()?.Value ?? 0;

                if (!offers.TryGetValue($"language-models-{modelName}-completion", out var completionModel))
                    continue;

                var inputCost = completionModel.Prices.Perthousandapitransactions.Values.FirstOrDefault()?.Value ?? 0;

                result.Add(new TokenCostViewModel
                {
                    TokenCostId = id++,
                    ModelName = modelName,
                    ModelTitle = modelName,
                    IsDefault = id == 1,
                    Incoming = inputCost / 1000,
                    Outgoing = outputCost / 1000
                });
            }

            return result;
        }

        public async Task<List<ResourcePriceViewModel>> ListAksPrices(string location)
        {
            var pricesJson = await GetAzurePrices("kubernetes-service");
            var offers = pricesJson.JsonGet<Dictionary<string, AksOfferModel>>("offers");

            return offers
                .Where(kv => kv.Key.StartsWith("linux-") && kv.Key.EndsWith("-standard"))
                .Select(kv =>
                {
                    var slug = kv.Key.Replace("linux-", "").Replace("-standard", "");
                    var price = kv.Value.Prices.Perhour.GetValueOrDefault(location);
                    if (price == null) return null;

                    return new ResourcePriceViewModel
                    {
                        ResourceName = $"{slug.ToUpper()}: {kv.Value.Cores} Cores, {kv.Value.Ram} GB RAM, {kv.Value.DiskSize} GB Temp",
                        Series = kv.Value.Series,
                        PriceHour = price.Value,
                        Location = location
                    };
                })
                .Where(x => x != null)
                .OrderBy(x => x.PriceHour)
                .ToList()!;
        }

        public async Task<List<ResourcePriceLocationViewModel>> ListRegions()
        {
            var pricesJson = await GetAzurePrices("kubernetes-service");
            var regions = pricesJson.JsonGet<List<ResourceLocationModel>>("regions");

            return regions.Select(r => new ResourcePriceLocationViewModel
            {
                Location = r.Slug,
                DisplayName = r.DisplayName
            }).ToList();
        }

        private async Task<string> GetAzurePrices(string type)
        {
            var cacheKey = $"prices-{type}";
            var cached = await _cache.GetStringAsync(cacheKey);

            if (!string.IsNullOrEmpty(cached))
                return cached;

            var client = _httpClientFactory.CreateClient(HttpClients.RetryClient);
            var response = await client.GetAsync($"https://azure.microsoft.com/api/v3/pricing/{type}/calculator/?culture=en-us&discount=mca");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
            });

            return json;
        }

        // Models
        public class AksOfferModel
        {
            public int Cores { get; set; }
            public int DiskSize { get; set; }
            public decimal Ram { get; set; }
            public string Series { get; set; } = "";
            public AksOfferPricesModel Prices { get; set; } = new();

            public class AksOfferPricesModel
            {
                public Dictionary<string, AksOfferPriceModel> Perhour { get; set; } = new();
            }

            public class AksOfferPriceModel
            {
                public decimal Value { get; set; }
            }
        }

        public class LlmOfferModel
        {
            public LlmOfferPricesModel Prices { get; set; } = new();

            public class LlmOfferPricesModel
            {
                public Dictionary<string, LlmOfferPriceModel> Perthousandapitransactions { get; set; } = new();
            }

            public class LlmOfferPriceModel
            {
                public decimal Value { get; set; }
            }
        }

        public class ResourceLocationModel
        {
            public string Slug { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }
    }

    public interface ISpentService
    {
        Task<List<SpentItemViewModel>> List();
        Task<List<TokenCostViewModel>> ListTokenCosts();
        Task<List<ResourcePriceViewModel>> ListAksPrices(string location);
        Task<List<ResourcePriceLocationViewModel>> ListRegions();
    }
}
