using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;
using Microsoft.Extensions.Caching.Distributed;

namespace AiCoreApi.Services.ControllersServices
{
    public class SpentService : ISpentService
    {
        private readonly IMapper _mapper;
        private readonly ISpentProcessor _spentProcessor;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RequestAccessor _requestAccessor;
        private readonly IDistributedCache _cache;

        public SpentService(
            IMapper mapper,
            ISpentProcessor spentProcessor,
            ILoginProcessor loginProcessor,
            IConnectionProcessor connectionProcessor,
            IHttpClientFactory httpClientFactory,
            RequestAccessor requestAccessor,
            IDistributedCache cache)
        {
            _mapper = mapper;
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
            var llmConnections = (await _connectionProcessor.List(_requestAccessor.WorkspaceId))
                .Where(x => x.Type.IsLlmConnection())
                .ToDictionary(
                    key => key.Name, 
                    value => new
                    {
                        InputTokenCost = Convert.ToDecimal(value.Content["inputTokenCost"]),
                        OutputTokenCost = Convert.ToDecimal(value.Content["outputTokenCost"])
                    });

            var lastMonthData = await _spentProcessor.ListLastMonth();
            var result = lastMonthData.GroupBy(item => item.LoginId)
                .Select(group =>
                {
                    var login = logins.FirstOrDefault(x => x.LoginId == group.Key);
                    var chatGroup = group.ToList();
                    var tokensIncoming = chatGroup.Sum(item => item.TokensIncoming);
                    var tokensOutgoing = chatGroup.Sum(item => item.TokensOutgoing);
                    var costDayByDay = new List<decimal>();
                    var previousDayCost = 0m;
                    for (var i = 0; i < 30; i++)
                    {
                        var date = DateTime.Now.Date.AddDays(i - 29);
                        var dayCost = group.Where(x => x.Date == date.Date)
                            .Sum(item => item.TokensIncoming * llmConnections[item.ModelName].OutputTokenCost + 
                                                 item.TokensOutgoing * llmConnections[item.ModelName].InputTokenCost) / 1000;
                        previousDayCost += dayCost;
                        costDayByDay.Add(previousDayCost);
                    }
                    return new SpentItemViewModel
                    {
                        LoginId = group.Key,
                        Login = login?.Login,
                        LoginType = login?.LoginType.ToString(),
                        TokensIncoming = tokensIncoming,
                        TokensOutgoing = tokensOutgoing,
                        Cost = costDayByDay.Sum(),
                        CostDayByDay = costDayByDay
                    };
                })
                .OrderBy(x => x.LoginId)
                .ToList();
            result.Insert(0, new SpentItemViewModel
            {
                LoginId = 0,
                Login = "Total",
                LoginType = "",
                TokensIncoming = result.Sum(item => item.TokensIncoming),
                TokensOutgoing = result.Sum(item => item.TokensOutgoing),
                Cost = result.Sum(item => item.Cost),
                CostDayByDay = result.SelectMany(item => item.CostDayByDay).ToList()
            });
            return result;
        }

        public async Task<List<TokenCostViewModel>> ListTokenCosts()
        {
            var pricesJson = await GetAzurePrices("openai-service");
            var prices = pricesJson.JsonGet<Dictionary<string, LlmOfferModel>>("offers");
            var i = 0;
            var result = new List<TokenCostViewModel>();
            foreach (var price in prices)
            {
                if (!price.Key.StartsWith("language-models-") || !price.Key.EndsWith("-prompt"))
                    continue;

                var modelName = price.Key.Replace("language-models-", string.Empty).Replace("-prompt", string.Empty);
                var outgoingCost = price.Value.Prices.Perthousandapitransactions.Values.FirstOrDefault()?.Value ?? 0;
                if (!prices.ContainsKey($"language-models-{modelName}-completion"))
                    continue;
                var incomingModel = prices[$"language-models-{modelName}-completion"];
                var incomingCost = incomingModel.Prices.Perthousandapitransactions.Values.FirstOrDefault()?.Value ?? 0;
                var tokenCostModel = new TokenCostViewModel
                {
                    ModelName = modelName,
                    ModelTitle = modelName,
                    IsDefault = i == 0,
                    TokenCostId = i++,
                    Incoming = incomingCost / 1000,
                    Outgoing = outgoingCost / 1000
                };
                result.Add(tokenCostModel);
            }
            return result;
        }

        public async Task<List<ResourcePriceViewModel>> ListAksPrices(string location)
        {
            var pricesJson = await GetAzurePrices("kubernetes-service");
            var prices = pricesJson.JsonGet<Dictionary<string, AksOfferModel>>("offers");
            var result = prices
                .Where(item => item.Key.StartsWith("linux-") && item.Key.EndsWith("-standard"))
                .Select(item =>
                {
                    var slug = item.Key.Replace("linux-", string.Empty).Replace("-standard", string.Empty);   
                    var price = item.Value.Prices.Perhour.FirstOrDefault(item => item.Key == location).Value;
                    if(price == null)
                        return null;
                    return new ResourcePriceViewModel
                    {
                        ResourceName = $"{slug.ToUpper()}: {item.Value.Cores} Cores, {item.Value.Ram} GB RAM, {item.Value.DiskSize} GB Temporary storage",
                        Series = item.Value.Series,
                        PriceHour = price.Value,
                        Location = location
                    };
                })
                .Where(item => item != null)
                .OrderBy(item => item.PriceHour)
                .ToList();
            return result;
        }

        public async Task<List<ResourcePriceLocationViewModel>> ListRegions()
        {
            var pricesJson = await GetAzurePrices("kubernetes-service");
            var locations = pricesJson.JsonGet<List<ResourceLocationModel>>("regions");
            var result = locations.Select(item => new ResourcePriceLocationViewModel
            {
                Location = item.Slug,
                DisplayName = item.DisplayName
            }).ToList();
            return result;
        }

        private async Task<string> GetAzurePrices(string type)
        {
            var cacheKey = $"prices-{type}";
            var pricesJson = await _cache.GetStringAsync(cacheKey);

            if (string.IsNullOrEmpty(pricesJson))
            {
                var client = _httpClientFactory.CreateClient("RetryClient");
                var response = await client.GetAsync($"https://azure.microsoft.com/api/v3/pricing/{type}/calculator/?culture=en-us&discount=mca&billingAccount=&billingProfile=&v=20240517-1050-410891");
                response.EnsureSuccessStatusCode();
                pricesJson = await response.Content.ReadAsStringAsync();
                var options = new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(24));
                await _cache.SetStringAsync(cacheKey, pricesJson, options);
            }
            return pricesJson;
        }

        public class AksOfferModel
        {
            public int Cores { get; set; } = 0;
            public int DiskSize { get; set; } = 0;
            public decimal Ram { get; set; } = 0;
            public string Series { get; set; } = "";
            public AksOfferPricesModel Prices { get; set; }
            public class AksOfferPricesModel
            {
                public Dictionary<string, AksOfferPriceModel> Perhour { get; set; } = new();

            }
            public class AksOfferPriceModel
            {
                public decimal Value { get; set; } = 0;

            }
        }

        public class LlmOfferModel
        {
            public LlmOfferPricesModel Prices { get; set; }
            public class LlmOfferPricesModel
            {
                public Dictionary<string, LlmOfferPriceModel> Perthousandapitransactions { get; set; } = new();
            }
            public class LlmOfferPriceModel
            {
                public decimal Value { get; set; } = 0;
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
