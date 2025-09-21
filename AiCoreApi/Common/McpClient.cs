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

                var clientTransport = new SseClientTransport(sseClientTransportOptions);

                await using var mcpClient = await McpClientFactory.CreateAsync(clientTransport!);
                var tools = await mcpClient.ListToolsAsync();
                return tools.Select(tool => new McpActionViewModel
                {
                    Title = tool.Title,
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = ExtractParameters(tool.JsonSchema)
                }).ToList() ?? null;
            }
            catch (Exception ex)
            {
                throw new AiCoreUiException($"Unable to retrieve MCP Actions: {ex.Message}");
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
                        Description = obj.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
                        Type = obj.TryGetProperty("type", out var type) ? type.GetString() ?? "string" : "string",
                        Enum = obj.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array
                            ? enumEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                            : null
                    };

                    parameters.Add(param);
                }
            }

            return parameters;
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

                var clientTransport = new SseClientTransport(sseClientTransportOptions);
                await using var mcpClient = await McpClientFactory.CreateAsync(clientTransport, cancellationToken: cancellationToken);

                var actionRequest = JsonSerializer.Deserialize<McpActionRequest>(mcpServerActionJson)
                    ?? throw new AiCoreUiException("Invalid MCP action request JSON.");

                var paramDict = actionRequest.ActionParameters?
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
            [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
            [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
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
