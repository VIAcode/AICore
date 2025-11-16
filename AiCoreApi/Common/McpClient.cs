using System.Text.Json;
using System.Text.Json.Serialization;
using AiCoreApi.Models.ViewModels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.Common
{
    public class McpClient: IMcpClient
    {
        private readonly ILogger<McpClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        public McpClient(
            ILogger<McpClient> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<List<McpActionViewModel>?> GetActions(string serverUrl, string? customHeader)
        {
            try
            {
                var sseClientTransportOptions = new SseClientTransportOptions
                {
                    Endpoint = new Uri(serverUrl),
                };
                if (!string.IsNullOrEmpty(customHeader))
                {
                    var keyValue = customHeader.Split(':', 2);
                    if (keyValue.Length == 2)
                    {
                        sseClientTransportOptions.AdditionalHeaders = new Dictionary<string, string>
                        {
                            { keyValue[0].Trim(), keyValue[1].Trim() }
                        };
                    }
                }

                using var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                var clientTransport = new SseClientTransport(sseClientTransportOptions, httpClient);

                await using var mcpClient = await McpClientFactory.CreateAsync(clientTransport!);
                var tools = await mcpClient.ListToolsAsync();
                return tools.Select(tool => new McpActionViewModel
                {
                    Title = tool.Title,
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = ExtractParameters(tool.JsonSchema),
                    OutputSchema = ExtractOutputSchema(tool.ReturnJsonSchema)
                }).ToList() ?? null;
            }
            catch (Exception ex)
            {
                throw new AiCoreUiException($"Unable to retrieve MCP Actions: {ex.Message}");
            }
        }

        private string ExtractOutputSchema(JsonElement? schema)
        {
            try
            {
                if (!schema.HasValue || schema.Value.ValueKind == JsonValueKind.Undefined ||
                    schema.Value.ValueKind == JsonValueKind.Null)
                    return string.Empty;

                var description = new System.Text.StringBuilder();

                var mainType = ExtractTypeValue(schema.Value);
                description.Append($"Type: {mainType}");

                if (schema.Value.TryGetProperty("description", out var desc) && !string.IsNullOrEmpty(desc.GetString()))
                {
                    description.Append($". {desc.GetString()}");
                }

                if (schema.Value.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    var requiredFields = new HashSet<string>();
                    if (schema.Value.TryGetProperty("required", out var requiredEl) &&
                        requiredEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var req in requiredEl.EnumerateArray())
                        {
                            if (req.ValueKind == JsonValueKind.String)
                                requiredFields.Add(req.GetString() ?? string.Empty);
                        }
                    }

                    description.Append(" Properties: ");
                    var propertyDescriptions = new List<string>();

                    foreach (var prop in props.EnumerateObject())
                    {
                        var propValue = prop.Value;
                        var propDesc = new System.Text.StringBuilder();

                        propDesc.Append($"{prop.Name} ({ExtractTypeValue(propValue)}");

                        if (requiredFields.Contains(prop.Name))
                            propDesc.Append(", required");

                        propDesc.Append(")");

                        if (propValue.TryGetProperty("description", out var propDescription) &&
                            !string.IsNullOrEmpty(propDescription.GetString()))
                        {
                            propDesc.Append($": {propDescription.GetString()}");
                        }

                        if (propValue.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
                        {
                            var enumValues = string.Join(", ", enumEl.EnumerateArray()
                                .Where(e => e.ValueKind == JsonValueKind.String)
                                .Select(e => e.GetString() ?? string.Empty));
                            propDesc.Append($" Possible values: [{enumValues}]");
                        }

                        // Handle array items
                        if (propValue.TryGetProperty("items", out var items))
                        {
                            propDesc.Append($" Items type: {ExtractTypeValue(items)}");
                        }

                        propertyDescriptions.Add(propDesc.ToString());
                    }

                    description.Append(string.Join("; ", propertyDescriptions));
                }

                return description.ToString();
            }
            catch (Exception ex)
            {
                // Just suppress any errors as MCP output schema is optional
                _logger.LogError(ex, $"Error extracting output schema from MCP action: {ex.Message}");
                return string.Empty;
            }
            
        }

        private List<McpActionParameterViewModel>? ExtractParameters(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.Undefined || schema.ValueKind == JsonValueKind.Null)
                return null;

            var parameters = new List<McpActionParameterViewModel>();
            if (schema.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    var obj = prop.Value;
                    var param = new McpActionParameterViewModel
                    {
                        Name = prop.Name,
                        Description = (obj.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty)
                            + (obj.TryGetProperty("minimum", out var minimum) ? $" Minimum: {minimum.GetRawText()}." : string.Empty)
                            + (obj.TryGetProperty("maximum", out var maximum) ? $" Maximum: {maximum.GetRawText()}." : string.Empty)
                            + (obj.TryGetProperty("default", out var defaultValue) ? $" Default value: {defaultValue.GetRawText()}." : string.Empty)
                            + (obj.TryGetProperty("items", out var arrayItems) ? $" Items format: {arrayItems.GetRawText()}." : string.Empty),
                        Type = ExtractTypeValue(obj),
                        Enum = obj.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array
                            ? enumEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                            : null,
                        CanBeNull = !(schema.TryGetProperty("required", out var requiredEl) && requiredEl.ValueKind == JsonValueKind.Array && requiredEl.EnumerateArray().Any(r => r.GetString() == prop.Name))
                    };
                    parameters.Add(param);
                }
            }
            return parameters;
        }

        private string ExtractTypeValue(JsonElement obj)
        {
            if (!obj.TryGetProperty("type", out var typeEl))
                return "string";

            // Handle both string and array types
            if (typeEl.ValueKind == JsonValueKind.String)
            {
                return typeEl.GetString() ?? "string";
            }
            else if (typeEl.ValueKind == JsonValueKind.Array)
            {
                // For array types, return the first non-null type
                var types = typeEl.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString())
                    .Where(t => t != null && t != "null")
                    .ToList();

                return types.FirstOrDefault() ?? "string";
            }

            return "string";
        }

        public async Task<string> ExecuteAction(string serverUrl, string? customHeader, string mcpServerActionJson, CancellationToken cancellationToken = default)
        {
            try
            {
                var sseClientTransportOptions = new SseClientTransportOptions
                {
                    Endpoint = new Uri(serverUrl),
                };

                if (!string.IsNullOrEmpty(customHeader))
                {
                    var keyValue = customHeader.Split(':', 2);
                    if (keyValue.Length == 2)
                    {
                        sseClientTransportOptions.AdditionalHeaders = new Dictionary<string, string>
                        {
                            { keyValue[0].Trim(), keyValue[1].Trim() }
                        };
                    }
                }

                using var httpClient = _httpClientFactory.CreateClient(HttpClients.NoRetryClient);
                var clientTransport = new SseClientTransport(sseClientTransportOptions, httpClient);
                await using var mcpClient = await McpClientFactory.CreateAsync(clientTransport, cancellationToken: cancellationToken);

                var actionRequest = JsonSerializer.Deserialize<McpActionRequest>(mcpServerActionJson)
                    ?? throw new AiCoreUiException("Invalid MCP action request JSON.");

                var paramDict = actionRequest.ActionParameters?
                        .Where(p => !((string.IsNullOrEmpty(p.Value) || p.Value.Equals("null", StringComparison.OrdinalIgnoreCase)) && p.CanBeNull))
                        .ToDictionary(p => p.Name, ConvertParameterValue)
                    ?? new Dictionary<string, object?>();

                var result = await mcpClient.CallToolAsync(
                    actionRequest.ActionName,
                    paramDict);

                return ConvertResult(result);
            }
            catch (Exception ex)
            {
                throw new AiCoreUiException($"Unable to execute MCP Action: {ex.Message}");
            }
        }

        private static object? ConvertParameterValue(McpActionParameter p)
        {
            if (string.IsNullOrEmpty(p.Value) || p.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return p.Type.ToLowerInvariant() switch
            {
                "number" => double.TryParse(p.Value, out var num) ? num : p.Value,
                "integer" => int.TryParse(p.Value, out var i) ? i : p.Value,
                "boolean" => bool.TryParse(p.Value, out var b) ? b : p.Value,
                "array" => JsonSerializer.Deserialize<List<object>>(p.Value) as object ?? p.Value,
                _ => p.Value,
            };
        }

        private static string ConvertResult(CallToolResult result)
        {
            var contentBlock = result.Content.FirstOrDefault();
            return contentBlock switch
            {
                TextContentBlock t => result.IsError.HasValue && result.IsError.Value ? "Error: " + t.Text : t.Text,
                ResourceLinkBlock r => r.Uri,
                ImageContentBlock i => $"{i.MimeType};{i.Data}",
                AudioContentBlock a => $"{a.MimeType};{a.Data}",
                EmbeddedResourceBlock e => $"{e.Resource.MimeType};{e.Resource}",
                _ => "{}"
            };
        }

        private class McpActionRequest
        {
            [JsonPropertyName("actionName")]
            public string ActionName { get; set; } = string.Empty;
            [JsonPropertyName("actionParameters")]
            public List<McpActionParameter>? ActionParameters { get; set; }
        }

        private class McpActionParameter
        {
            [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
            [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
            [JsonPropertyName("enum")] public string[] Enum { get; set; } = { };
            [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
            [JsonPropertyName("canBeNull")] public bool CanBeNull { get; set; } = false;

        }
    }

    public interface IMcpClient
    {
        Task<List<McpActionViewModel>?> GetActions(string serverUrl, string? customHeader);
        Task<string> ExecuteAction(
            string serverUrl,
            string? customHeader,
            string mcpServerActionJson,
            CancellationToken cancellationToken = default);
    }
}
