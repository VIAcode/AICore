using System.Net;
using System.Text.RegularExpressions;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.KernelMemory.AI;
using Newtonsoft.Json;

namespace AiCoreApi.Common
{
    public class OpenAiHttpCallHandler : DelegatingHandler
    {
        private readonly IServiceProvider _serviceProvider;

        public OpenAiHttpCallHandler(
            ExtendedConfig config, 
            IServiceProvider serviceProvider) : base(
            string.IsNullOrEmpty(config.Proxy)
                ? new HttpClientHandler()
                : new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    Proxy = new WebProxy(config.Proxy)
                })
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var httpContext = _serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext;
            var serviceProvider = httpContext?.RequestServices ?? _serviceProvider;

            var connectionProcessor = serviceProvider.GetService<IConnectionProcessor>();
            var connectionType = GetConnectionType(request);
            if (connectionType == null)
                return await base.SendAsync(request, cancellationToken);
            var modelDeploymentName = await GetModelDeploymentName(request, connectionType.Value, cancellationToken);
            // calculate tokens only for OpenAI models

            if (string.IsNullOrEmpty(modelDeploymentName))
                return await base.SendAsync(request, cancellationToken);
            var connections = await connectionProcessor.List();

            var connection = connections.FirstOrDefault(conn => conn.Type == connectionType.Value &&
                (conn.Type == ConnectionType.AzureOpenAiLlm && conn.Content["deploymentName"].ToLower() == modelDeploymentName) ||
                (conn.Type == ConnectionType.DeepSeekLlm && conn.Content["modelName"].ToLower() == modelDeploymentName) ||
                (conn.Type == ConnectionType.OpenAiLlm && conn.Content["modelName"].ToLower() == modelDeploymentName));
            connection = await ApplyAzureOpenAiLlmCarousel(request, connections, connection, modelDeploymentName);
            if (connection == null)
                throw new TokensLimitException($"Model Deployment was not found in LLM connections: {modelDeploymentName}");
            var tokenLimitPerDay = Convert.ToInt64(connection.Content["tokenLimitPerDay"]);
            var userContextAccessor = serviceProvider.GetService<UserContextAccessor>();
            var spentProcessor = serviceProvider.GetService<ISpentProcessor>();
            var loginProcessor = serviceProvider.GetService<ILoginProcessor>();
            var logger = serviceProvider.GetService<ILogger<OpenAiHttpCallHandler>>();

            var loginId = userContextAccessor?.LoginId ?? UserContextAccessor.AsyncScheduledLoginId.Value;
            if (loginId == null)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var spent = await spentProcessor.GetTodayByLoginId(loginId.Value, connection.Name);
            var login = await loginProcessor.GetById(loginId.Value);

            var maxTokensPerDay = login.TokensLimit != 0
                ? login.TokensLimit
                : tokenLimitPerDay;

            var requestTokensCount = await GetRequestTokensCount(request, cancellationToken);
            if (spent.TokensIncoming + spent.TokensOutgoing + requestTokensCount > maxTokensPerDay)
                throw new TokensLimitException($"Tokens limit exceeded for model {connection.Name}. Tokens limit per day: {maxTokensPerDay}. Spent: {spent.TokensIncoming + spent.TokensOutgoing}. Ongoing request: {requestTokensCount}");

            var response = await base.SendAsync(request, cancellationToken);

            var currentRequestSpent = await GetCurrentRequestSpent(request, response, cancellationToken);
            spent.TokensIncoming += currentRequestSpent.TokensIncoming;
            spent.TokensOutgoing += currentRequestSpent.TokensOutgoing;
            logger.LogInformation("{Login}, Action:{Action}, Model: {Model}, Incoming: {Incoming}, Outgoing: {Outgoing}",
                login.Login, "LLM", modelDeploymentName, currentRequestSpent.TokensIncoming, currentRequestSpent.TokensOutgoing);
            await spentProcessor.Update(spent);

            // update spent tokens in response accessor
            if (httpContext != null)
            {
                var responseAccessor = serviceProvider.GetService<ResponseAccessor>();
                if (responseAccessor != null)
                    responseAccessor.AddSpentTokens(connection.Name, currentRequestSpent.TokensOutgoing, currentRequestSpent.TokensIncoming);

            }
            return response;
        }

        private async Task<ConnectionModel> ApplyAzureOpenAiLlmCarousel(HttpRequestMessage request, List<ConnectionModel> connections, ConnectionModel connection, string modelDeploymentName)
        {
            if (modelDeploymentName == nameof(ConnectionType.AzureOpenAiLlmCarousel) || request.Headers.Contains(nameof(ConnectionType.AzureOpenAiLlmCarousel)))
            {
                var headerName = modelDeploymentName == nameof(ConnectionType.AzureOpenAiLlmCarousel) ? "api-key" : nameof(ConnectionType.AzureOpenAiLlmCarousel);
                var connectionIds = request.Headers.GetValues(headerName).First().Split(",")
                    .Select(connectionId => Convert.ToInt32(connectionId))
                    .Where(connectionId => connections.Exists(x => x.ConnectionId == connectionId))
                    .ToList();

                var connectionId = connectionIds.GetRandomElement();
                connection = connections.FirstOrDefault(conn => conn.ConnectionId == connectionId);
                if (connection != null)
                {
                    request.RequestUri = new Uri(connection.Content["endpoint"]
                        + Regex.Replace(request.RequestUri.PathAndQuery, @"(/deployments/)([^/]+)(/chat/)", $"$1{connection.Content["deploymentName"]}$3"));
                    request.Headers.Remove("api-key");
                    request.Headers.Add("api-key", connection.Content["azureOpenAiKey"]);

                    if (modelDeploymentName == nameof(ConnectionType.AzureOpenAiLlmCarousel))
                    {
                        request.Headers.Add(nameof(ConnectionType.AzureOpenAiLlmCarousel), string.Join(",", connectionIds));
                    }
                }
            }
            return connection;
        }

        private ConnectionType? GetConnectionType(HttpRequestMessage request)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri == "https://api.openai.com/v1/chat/completions")
                return ConnectionType.OpenAiLlm;
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri.Contains("openai.azure.com/openai/deployments"))
                return ConnectionType.AzureOpenAiLlm;
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri.Contains("api.deepseek.com"))
                return ConnectionType.DeepSeekLlm;
            return null;
        }

        private async Task<string> GetModelDeploymentName(HttpRequestMessage request, ConnectionType connectionType, CancellationToken cancellationToken)
        {
            if (connectionType == ConnectionType.AzureOpenAiLlm)
            {
                var url = request.RequestUri.PathAndQuery;
                var azureOpenAiPattern = @"deployments\/(.*?)\/chat\/completions";
                var azureOpenAiRegex = new Regex(azureOpenAiPattern);
                var azureOpenAiMatch = azureOpenAiRegex.Match(url);
                if (azureOpenAiMatch.Success)
                    return azureOpenAiMatch.Groups[1].Value;
            }
            if (connectionType == ConnectionType.OpenAiLlm || 
                connectionType == ConnectionType.DeepSeekLlm)
            {
                var requestText = await request.Content?.ReadAsStringAsync(cancellationToken)!;
                return requestText.JsonGet<string>("model") ?? string.Empty;
            }
            return string.Empty;
        }

        private async Task<int> GetRequestTokensCount(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestText = await request.Content?.ReadAsStringAsync(cancellationToken)!;
            var requestTextContent = requestText.JsonGet<string>("messages[0].content") ?? string.Empty;
            var requestTokensCount = new O200KTokenizer().CountTokens(requestTextContent); // Default Tokenizer for gpt-4o-* models
            return requestTokensCount;
        }

        private async Task<SpentModel> GetCurrentRequestSpent(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var spentModel = new SpentModel();
            string responseTextContent;
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken)!;
            // one time call contains JSON output - parse it
            if (responseText.StartsWith("{"))
            {
                var responseUsage = responseText.JsonGet<ResponseUsage>();
                // if response contains usage info
                if (responseUsage?.Usage != null)
                {
                    spentModel.TokensIncoming = responseUsage.Usage.CompletionTokens;
                    spentModel.TokensOutgoing = responseUsage.Usage.PromptTokens;
                    return spentModel;
                }
            } 
            // streaming call contains multiple "data:" sections - pre-last one contains spent
            else if (responseText.StartsWith("data:"))
            {
                var responseSections = responseText.Split("data:");
                var responseUsage = responseSections[^2].JsonGet<ResponseUsage>();
                // if response contains usage info
                if (responseUsage?.Usage != null)
                {
                    spentModel.TokensIncoming = responseUsage.Usage.CompletionTokens;
                    spentModel.TokensOutgoing = responseUsage.Usage.PromptTokens;
                    return spentModel;
                }
            }

            // if response contains choices array with content in it then parse it and count tokens
            if (responseText.StartsWith("{\"choices\""))
            {
                responseTextContent = responseText.JsonGet<string>("choices[0].message.content") ?? string.Empty;
            }
            // if response contains data array with content in it then parse it and count tokens
            else
            {
                var responseTextContentParts = responseText
                    .Split("data:")
                    .Select(x => x.JsonGet<string>("choices[0].delta.content"))
                    .ToList();
                responseTextContent = string.Join("", responseTextContentParts);
            }
            // Manually calculated, because response doesn't contain usage info
            spentModel.TokensIncoming = new O200KTokenizer().CountTokens(responseTextContent);
            spentModel.TokensOutgoing = await GetRequestTokensCount(request, cancellationToken);
            return spentModel;
        }

        public class ResponseUsage
        {
            [JsonProperty("usage")]
            public Usage? Usage { get; set; }
        }

        public class Usage
        {
            [JsonProperty("completion_tokens")]
            public int CompletionTokens { get; set; }
            [JsonProperty("prompt_tokens")]
            public int PromptTokens { get; set; }
        }
    }

    public class TokensLimitException : Exception
    {
        public TokensLimitException(string message) : base(message)
        {
        }
    }
}
