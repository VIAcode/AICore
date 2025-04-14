using System.Net;
using AiCoreApi.Common;
using System.Reflection;
using AiCoreApi.Authorization;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using AiCoreApi.Common.KernelMemory;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Common.Data;
using AspNetCore.Authentication.Basic;
using Polly;
using Polly.Extensions.Http;
using Microsoft.OpenApi.Models;
using Prometheus;

namespace AiCoreApi;

public class Startup
{
    private readonly Config _config;
    private static ILogger<Startup> _logger;
    public Startup(IConfiguration configuration)
    {
        _config = new Config();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        AddServices(services);
        services.AddControllers();
    }

    private void AddServices(IServiceCollection services)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("CorsPolicy", builder => builder
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
            );
        });

        services.AddSingleton(_config);
        // Added feature flags and datasource provider first
        // so ForInterfacesMatching does nothing with them
        services.AddSingleton<IFeatureFlags, FeatureFlags>();
        // we must use only one DataSourceProvider instance otherwise we will start getting the following error:
        // System.InvalidOperationException: An error was generated for warning 'Microsoft.EntityFrameworkCore.Infrastructure.ManyServiceProvidersCreatedWarning':
        // More than twenty 'IServiceProvider' instances have been created for internal use by Entity Framework. ...
        // See: https://stackoverflow.com/questions/60047465/more-than-twenty-iserviceprovider-instances-have-been-created-for-internal-use
        services.AddSingleton<IDataSourceProvider, DataSourceProvider>(e => new DataSourceProvider(_config));
        services.ForInterfacesMatching("^I").OfAssemblies(Assembly.GetExecutingAssembly()).AddTransients();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = _config.DistributedCacheUrl;
            options.ConfigurationOptions = new StackExchange.Redis.ConfigurationOptions
            {
                EndPoints = { _config.DistributedCacheUrl },
                Password = _config.DistributedCachePassword,
            };
        });
        services.AddTransient<Db>();
        services.AddHttpContextAccessor();
        services.AddSingleton(sp => sp);
        services.AddScoped<RequestAccessor>();
        services.AddScoped<UserContextAccessor>();
        services.AddScoped<ResponseAccessor>();
        services.AddSingleton<ExtendedConfig>();
        // TODO: avoid mixing IoC strategies https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines#recommendations
        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<Startup>>();
        var extendedConfig = serviceProvider.GetRequiredService<ExtendedConfig>();
        services.AddSingleton<IFileIngestionClient>(sp => new FileIngestionClient(sp));

        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateLifetime = true,
            LifetimeValidator = (notBefore, expires, _, _) => notBefore <= DateTime.UtcNow && expires > DateTime.UtcNow,
            ValidateIssuerSigningKey = true,
            ValidAudience = extendedConfig.AuthAudience,
            ValidIssuer = extendedConfig.AuthIssuer,
            IssuerSigningKey = extendedConfig.AuthSecurityKey.GetSymmetricSecurityKey(),
            ClockSkew = TimeSpan.Zero,
        };

        services.AddSingleton(tokenValidationParameters);
        services.AddHttpContextAccessor();
        services.AddScoped<OpenAiHttpCallHandler>();
        //services.AddTransient(sp => new OpenAiHttpCallHandler(extendedConfig, sp));
        services.AddHttpClient("RetryClient", httpClient =>
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3); // wait 3 min instead of 100 sec by default
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(4))
            .AddPolicyHandler(GetRetryPolicy())
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                Proxy = string.IsNullOrEmpty(extendedConfig.Proxy) ? null : new WebProxy(new Uri(extendedConfig.Proxy)),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            .ConfigurePrimaryHttpMessageHandler<OpenAiHttpCallHandler>();

        services.AddHttpClient("NoRetryClient", httpClient =>
            {
                httpClient.Timeout = TimeSpan.FromMinutes(3); // wait 3 min instead of 100 sec by default
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(4))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                Proxy = string.IsNullOrEmpty(extendedConfig.Proxy) ? null : new WebProxy(new Uri(extendedConfig.Proxy)),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            .ConfigurePrimaryHttpMessageHandler<OpenAiHttpCallHandler>();

        var combinedAuthenticationScheme = "Combined";
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = combinedAuthenticationScheme;
                options.DefaultChallengeScheme = combinedAuthenticationScheme;
                options.DefaultScheme = combinedAuthenticationScheme;
            })
            .AddPolicyScheme(combinedAuthenticationScheme, "Bearer / Basic", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var isCombinedAuthorize = context.GetEndpoint()?.Metadata.FirstOrDefault(x => x.GetType() == typeof(CombinedAuthorizeAttribute)) != null;
                    var header = (string?)context.Request.Headers.Authorization ?? "";
                    if (header.StartsWith("Basic") && isCombinedAuthorize)
                        return BasicDefaults.AuthenticationScheme;
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(options => { options.TokenValidationParameters = tokenValidationParameters; })
            .AddBasic<BasicUserValidationService>(options => { options.SuppressWWWAuthenticateHeader = true; });

        //services.AddTransient(sp => new HttpClient(new OpenAiHttpCallHandler(extendedConfig, sp)));
        services.AddAutoMapper(typeof(Startup));
        services.AddHealthChecks();
        services.AddControllers().AddNewtonsoftJson(options => options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "AI Core API",
                Description = "API to work with AI Core. Most endpoints are for administration and configuration. Its required to use Auth Code Flow + PKCE to use them.\n\n" +
                    "Endpoint for API integration using Basic Authentication:\n\n" +
                    "/api/v1/agents/{agentName}/isEnabled\n\n" +
                    "/api/v1/tags/my\n\n" + 
                    "/api/v1/copilot/chat\n\n" +
                    "/api/v1/copilot/search\n\n" +
                    "/api/v1/copilot/transcript",
                Contact = new OpenApiContact
                {
                    Name = "VIACode",
                    Url = new Uri("https://viacode.com")
                },
            });
            options.EnableAnnotations();
            options.AddSecurityDefinition("Basic", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Basic",
                In = ParameterLocation.Header,
                Description = "Basic Authorization header, login:password encoded with Base64."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Basic"
                        }
                    },
                    new string[] {}
                }
            });
        });
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConsole(opt => opt.LogToStandardErrorThreshold = Enum.Parse<LogLevel>(extendedConfig.LogLevel));
            loggingBuilder.AddDebug();
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.Use((context, next) =>
        {
            context.Request.EnableBuffering();
            return next();
        });
        app.UseRouting();
        app.UseSwagger(options =>
        {
            options.RouteTemplate = "/api/swagger/{documentName}/swagger.json";
        });
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "api/swagger";
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCors("CorsPolicy");

        app.UseMetricServer();
        app.UseHttpMetrics(); 
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
            });
        });
    }

    private IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() => HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg =>
        {
            var nonSuccessRequest = 
                msg.StatusCode != HttpStatusCode.OK && 
                msg.StatusCode != HttpStatusCode.Accepted &&
                msg.StatusCode != (HttpStatusCode)424 &&
                msg.StatusCode != HttpStatusCode.NoContent;
            if (nonSuccessRequest)
            {
                _logger.LogWarning("Startup: {0}, url: {1}, request headers: {2}, code: {3}, body: {4}, response headers: {5}", "GetRetryPolicy",
                    msg.RequestMessage.RequestUri, msg.RequestMessage.Headers, msg.StatusCode, msg.Content.ReadAsStringAsync().Result, msg.Headers);
            }
            return nonSuccessRequest;
        })
        .WaitAndRetryAsync(7, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
}