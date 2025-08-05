namespace AiCoreApi.Models.ViewModels;

public class ConnectionCreateViewModel
{
    public string ManagedIdentity { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
}