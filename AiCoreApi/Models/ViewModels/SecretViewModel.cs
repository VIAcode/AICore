namespace AiCoreApi.Models.ViewModels
{
    public class SecretViewModel
    {
        public int SecretId { get; set; } = 0;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    public class SecretExtendedItem : SecretViewModel
    {
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }
}
