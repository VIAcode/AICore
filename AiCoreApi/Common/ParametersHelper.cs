using AiCoreApi.Models.DbModels;
using System.Text;
using System.Text.RegularExpressions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.Common
{
    public class ParametersHelper : ParametersHelperBase, IParametersHelper
    {
        public ParametersHelper(
            RequestAccessor requestAccessor,
            ICacheAccessor cacheAccessor,
            ResponseAccessor responseAccessor,
            ILogger<ParametersHelper> logger,
            IEntraTokenProvider entraTokenProvider)
            : base(requestAccessor, cacheAccessor, responseAccessor, logger, entraTokenProvider)
        {
        }

        protected override string ResolveUnknownKey(string key)
        {
            return $"{{{{{key}}}}}";
        }
    }

    public interface IParametersHelper
    {
        Task<ConnectionModel> ApplySecrets(ConnectionModel connectionModel);
        Task<string> ApplySecret(string value);
        Task<string> ApplyParametersAsync(string text, string debugSource, Dictionary<string, string>? parameters, Dictionary<string, string>? additionalParameters = null);
    }

    public class IngestionParametersHelper : ParametersHelperBase, IIngestionParametersHelper
    {
        private readonly IIngestionProcessor _ingestionProcessor;
        private readonly IDataIngestionWorkerFactory _workerFactory;

        public IngestionParametersHelper(
            RequestAccessor requestAccessor,
            ICacheAccessor cacheAccessor,
            ResponseAccessor responseAccessor,
            ILogger<IngestionParametersHelper> logger,
            IEntraTokenProvider entraTokenProvider,
            IIngestionProcessor ingestionProcessor,
            IDataIngestionWorkerFactory workerFactory)
            : base(requestAccessor, cacheAccessor, responseAccessor, logger, entraTokenProvider)
        {
            _ingestionProcessor = ingestionProcessor;
            _workerFactory = workerFactory;
        }

        protected override string ResolveUnknownKey(string key)
        {
            if (key.StartsWith("DS:"))
                return ResolveDataSourceValueAsync(key).GetAwaiter().GetResult();

            return base.ResolveUnknownKey(key);
        }

        private async Task<string> ResolveDataSourceValueAsync(string key)
        {
            var parts = key.Split(':');
            if (parts.Length < 3 || parts.Length > 4)
                return "Invalid Data Source parameters count";

            var dsName = parts[1];
            var dsPath = parts[2];
            var cacheSeconds = (parts.Length == 4 && int.TryParse(parts[3], out var c)) ? c : 0;
            var cacheKey = $"{dsName}_{dsPath}";

            var cachedValue = CacheAccessor.GetCacheValue(cacheKey);
            if (!string.IsNullOrEmpty(cachedValue))
                return cachedValue;

            var ingestion = await _ingestionProcessor.Get(dsName, RequestAccessor.WorkspaceId);
            if (ingestion == null)
                return $"Data Source '{dsName}' not found";

            var worker = _workerFactory.GetService(ingestion);
            var file = await worker.GetFileByPath(ingestion, dsPath);
            if (string.IsNullOrEmpty(file))
                return $"File '{dsPath}' not found in Data Source '{dsName}'";

            if (cacheSeconds > 0)
                CacheAccessor.SetCacheValue(cacheKey, file, cacheSeconds);

            return file;
        }
    }

    public interface IIngestionParametersHelper
    {
        Task<ConnectionModel> ApplySecrets(ConnectionModel connectionModel);
        Task<string> ApplySecret(string value);
        Task<string> ApplyParametersAsync(string text, string debugSource, Dictionary<string, string>? parameters, Dictionary<string, string>? additionalParameters = null);
    }

    public abstract class ParametersHelperBase
    {
        protected readonly RequestAccessor RequestAccessor;
        protected readonly ICacheAccessor CacheAccessor;
        protected readonly ResponseAccessor ResponseAccessor;
        protected readonly ILogger Logger;
        protected readonly IEntraTokenProvider EntraTokenProvider;

        protected ParametersHelperBase(
            RequestAccessor requestAccessor,
            ICacheAccessor cacheAccessor,
            ResponseAccessor responseAccessor,
            ILogger logger,
            IEntraTokenProvider entraTokenProvider)
        {
            RequestAccessor = requestAccessor;
            CacheAccessor = cacheAccessor;
            ResponseAccessor = responseAccessor;
            Logger = logger;
            EntraTokenProvider = entraTokenProvider;
        }

        public virtual async Task<string> ApplyParametersAsync(
            string text,
            string debugSource,
            Dictionary<string, string>? parameters,
            Dictionary<string, string>? additionalParameters = null)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;

            while (i < text.Length)
            {
                if (i + 2 < text.Length && text[i] == '\\' && text[i + 1] == '{' && text[i + 2] == '{')
                {
                    sb.Append("{{");
                    i += 3;
                    continue;
                }

                if (i + 2 < text.Length && text[i] == '\\' && text[i + 1] == '}' && text[i + 2] == '}')
                {
                    sb.Append("}}");
                    i += 3;
                    continue;
                }

                if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
                {
                    int start = i + 2;
                    int braceDepth = 1;
                    int j = start;

                    while (j < text.Length - 1)
                    {
                        if (text[j] == '{' && text[j + 1] == '{')
                        {
                            braceDepth++;
                            j += 2;
                        }
                        else if (text[j] == '}' && text[j + 1] == '}')
                        {
                            braceDepth--;
                            j += 2;
                            if (braceDepth == 0) break;
                        }
                        else
                        {
                            j++;
                        }
                    }

                    if (braceDepth != 0 || j > text.Length)
                    {
                        sb.Append(text.Substring(i));
                        break;
                    }

                    string key = text.Substring(start, j - start - 2).Trim();
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        sb.Append("{{}}");
                        i = j;
                        continue;
                    }

                    string? value = null;

                    if (parameters?.TryGetValue(key, out var paramValue) == true)
                    {
                        value = paramValue;
                    }
                    else if (additionalParameters != null &&
                             additionalParameters.TryGetValue(key, out var extra))
                    {
                        value = extra;
                    }
                    else if (TryGetContextValue(key, out var ctx, debugSource))
                    {
                        value = ctx;
                    }
                    else
                    {
                        value = ResolveUnknownKey(key);
                    }

                    sb.Append(value);
                    i = j;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        protected virtual string ResolveUnknownKey(string key)
        {
            // default behavior: leave as-is
            return $"{{{{{key}}}}}";
        }

        protected bool TryGetContextValue(string key, out string? value, string debugSource)
        {
            const string prefix = "context:";

            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length)
            {
                var k = key[prefix.Length..];
                if (ResponseAccessor.Context.TryGetValue(k, out value))
                    return true;

                var msg = $"Failed to resolve context value {k}.";
                ResponseAccessor.AddDebugMessage(debugSource, "Context", msg);
                Logger.LogWarning(msg);
            }

            value = null;
            return false;
        }

        public async Task<ConnectionModel> ApplySecrets(ConnectionModel connectionModel)
        {
            var keys = connectionModel.Content.Keys.ToList();
            foreach (var key in keys)
            {
                connectionModel.Content[key] = await ApplySecret(connectionModel.Content[key]);
            }
            return connectionModel;
        }

        public async Task<string> ApplySecret(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var regex = new Regex(@"\{\{secret:(?<name>[^}]+)\}\}", RegexOptions.Compiled);
            var matches = regex.Matches(value);
            if (matches.Count == 0)
                return value;

            foreach (Match match in matches)
            {
                var secretName = match.Groups["name"].Value.Trim();
                var secretValue = await EntraTokenProvider.GetSecretFromKeyVaultAsync(secretName);
                value = value.Replace(match.Value, secretValue);
            }
            return value;
        }
    }
}
