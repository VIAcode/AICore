using System.Net;
using Polly;
using Polly.Extensions.Http;

namespace AiCoreApi.Common;

public static class HttpClients
{
    public const string NoRetryClient = "NoRetryClient";
    public const string RetryClient = "RetryClient";

    public static IServiceCollection AddConfiguredHttpClients(this IServiceCollection services)
    {
        // Retry client
        services.AddHttpClient(RetryClient, (sp, httpClient) =>
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3);
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(4))
            .AddPolicyHandler((sp, _) => GetRetryPolicy(sp.GetRequiredService<ILogger<HttpClient>>()))
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp.GetRequiredService<ExtendedConfig>()))
            .AddHttpMessageHandler<LlmHttpCallHandler>();

        // No-retry client
        services.AddHttpClient(NoRetryClient, (sp, httpClient) =>
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3);
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(4))
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp.GetRequiredService<ExtendedConfig>()))
            .AddHttpMessageHandler<LlmHttpCallHandler>();

        return services;
    }

    private static HttpClientHandler CreateHandler(ExtendedConfig config)
    {
        return new HttpClientHandler
        {
            Proxy = string.IsNullOrEmpty(config.Proxy)
                ? null
                : new WebProxy(new Uri(config.Proxy)),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(ILogger logger) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg =>
            {
                try
                {
                    msg.EnsureSuccessStatusCode();
                    return false;
                }
                catch (HttpRequestException)
                {
                    logger.LogWarning(
                        "RetryPolicy: transient HTTP error. URL={Url}, Code={Code}",
                        msg.RequestMessage?.RequestUri,
                        msg.StatusCode);
                    return true;
                }
            })
            .WaitAndRetryAsync(
                retryCount: 7,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (outcome, timespan, attempt, _) =>
                {
                    logger.LogWarning(
                        "Retry attempt {Attempt} after {Delay}s for {Url}",
                        attempt,
                        timespan.TotalSeconds,
                        outcome?.Result?.RequestMessage?.RequestUri);
                });
}