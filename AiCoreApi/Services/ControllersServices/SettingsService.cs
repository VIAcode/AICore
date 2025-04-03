using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using Json.Schema.Generation;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.Services.ControllersServices
{
    public class SettingsService : ISettingsService
    {
        private readonly ExtendedConfig _extendedConfig;
        private readonly IInstanceSync _instanceSync;
        private readonly ISettingsProcessor _settingsProcessor;
        private readonly IFeatureFlags _featureFlags;

        public SettingsService(
            ExtendedConfig extendedConfig,
            IInstanceSync instanceSync,
            ISettingsProcessor settingsProcessor,
            IFeatureFlags featureFlags)
        {
            _extendedConfig = extendedConfig;
            _instanceSync = instanceSync;
            _settingsProcessor = settingsProcessor;
            _featureFlags = featureFlags;
        }

        public List<SettingsViewModel> ListAll()
        {
            var props = typeof(ExtendedConfig)
                .GetProperties()
                .ToList();
            _extendedConfig.Reset();

            var settings = _settingsProcessor.Get(SettingType.Common);
            return props
                .Select(prop => new SettingsViewModel
                {
                    SettingId = prop.Name,
                    Value = settings.ContainsKey(prop.Name)
                        ? settings[prop.Name]
                        : prop.GetValue(_extendedConfig)?.ToString() ?? "",
                    Tooltip = prop.GetCustomAttributes(false).OfType<TooltipAttribute>().FirstOrDefault()?.TooltipText ?? "",
                    DateType = prop.GetCustomAttributes(false).OfType<DataTypeAttribute>().FirstOrDefault()?.DataType.ToString() ?? DataTypeAttribute.ConfigDataTypeEnum.String.ToString(),
                    Description = prop.GetCustomAttributes(false).OfType<DescriptionAttribute>().FirstOrDefault()?.Description ?? "",
                    Category = prop.GetCustomAttributes(false).OfType<CategoryAttribute>().FirstOrDefault()?.Category.GetDescription() ?? CategoryAttribute.ConfigCategoryEnum.Common.GetDescription()
                })
                .ToList();

        }

        public void SaveAll(List<SettingsViewModel> settingsViewModels)
        {
            var settings = settingsViewModels
                .ToDictionary(x => x.SettingId, x => x.Value);
            _settingsProcessor.Set(SettingType.Common, settings);
            _extendedConfig.Reset();
        }

        public void Reboot()
        {
            _instanceSync.SetRestartNeeded();
        }

        public UiSettingsViewModel ListForUi()
        {
            return new UiSettingsViewModel
            {
                FavIconUrl = _extendedConfig.FavIconUrl,
                LogoUrl = _extendedConfig.LogoUrl,
                AllowDebugMode = _extendedConfig.AllowDebugMode,
                MainColor = _extendedConfig.MainColor,
                MainTextColor = _extendedConfig.MainTextColor,
                SecondaryTextColor = _extendedConfig.SecondaryTextColor,
                ContrastTextColor = _extendedConfig.ContrastTextColor,
                MenuBackColor1 = _extendedConfig.MenuBackColor1,
                MenuBackColor2 = _extendedConfig.MenuBackColor2,
                BackgroundColor = _extendedConfig.BackgroundColor,
                PageTitle = _extendedConfig.PageTitle,
                UseSearchTab = _extendedConfig.UseSearchTab,
                UseMicrosoftSso = _extendedConfig.UseMicrosoftSso,
                UseGoogleSso = _extendedConfig.UseGoogleSso,
                UseInternalUsers = _extendedConfig.UseInternalUsers,
                DebugMessagesStorageEnabled = _extendedConfig.DebugMessagesStorageEnabled,
                CollapseFilesSection = _extendedConfig.CollapseFilesSection,
                AutoRenderImages = _extendedConfig.AutoRenderImages,
                UseKeyVaultAppRegistration = _extendedConfig.UseKeyVaultAppRegistration,
                UseGitStorage = _extendedConfig.UseGitStorage,
                FeatureFlags = _featureFlags.GetValues()
            };
        }
    }

    public interface ISettingsService
    {
        List<SettingsViewModel> ListAll();
        void SaveAll(List<SettingsViewModel> settingsViewModels);
        void Reboot();
        UiSettingsViewModel ListForUi();
    }
}
