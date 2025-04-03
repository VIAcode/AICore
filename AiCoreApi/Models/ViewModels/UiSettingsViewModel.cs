namespace AiCoreApi.Models.ViewModels
{
    public class UiSettingsViewModel
    {
        public string MainColor { get; set; } = string.Empty;
        public string MainTextColor { get; set; } = string.Empty;
        public string SecondaryTextColor { get; set; } = string.Empty;
        public string ContrastTextColor { get; set; } = string.Empty;
        public string MenuBackColor1 { get; set; } = string.Empty;
        public string MenuBackColor2 { get; set; } = string.Empty;
        public string BackgroundColor { get; set; } = string.Empty;
        public string PageTitle { get; set; } = string.Empty;
        public string LogoUrl { get; set; } = string.Empty;
        public bool AllowDebugMode { get; set; } = false; 
        public string FavIconUrl { get; set; } = string.Empty;
        public bool UseSearchTab { get; set; } = false;
        public bool UseMicrosoftSso { get; set; } = false;
        public bool UseGoogleSso { get; set; } = false;
        public bool UseInternalUsers { get; set; } = false;
        public bool DebugMessagesStorageEnabled { get; set; } = false;
        public bool CollapseFilesSection { get; set; } = false;
        public bool AutoRenderImages { get; set; } = false;
        public bool UseKeyVaultAppRegistration { get; set; } = false;
        public bool UseGitStorage { get; set; } = false;
        public Dictionary<string, bool> FeatureFlags { get; set; } = new ();
    }
}
