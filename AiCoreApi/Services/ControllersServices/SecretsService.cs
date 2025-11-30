using AiCoreApi.Common;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.Services.ControllersServices;

public class SecretsService : ISecretsService
{
    private readonly ISettingsProcessor _settingsProcessor;
    private readonly IEntraTokenProvider _entraTokenProvider;
    private readonly ILogger<SecretsService> _logger;

    public static class SettingTypes
    {
        public const string AppRegistration = "App Registration";
        public const string Secret = "Secret";
    }

    private const int SecretsIdStart = 10000;

    public SecretsService(
        ISettingsProcessor settingsProcessor,
        IEntraTokenProvider entraTokenProvider,
        ILogger<SecretsService> logger)
    {
        _settingsProcessor = settingsProcessor;
        _entraTokenProvider = entraTokenProvider;
        _logger = logger;
    }

    public async Task<SecretExtendedItem> Add(SecretExtendedItem secretExtendedItem)
        => secretExtendedItem.Type == SettingTypes.AppRegistration
            ? await AddAppRegistration(secretExtendedItem)
            : await AddSecret(secretExtendedItem);

    private async Task<SecretExtendedItem> AddAppRegistration(SecretExtendedItem item)
    {
        try
        {
            await _entraTokenProvider.SetCredentialsToKeyVaultAsync(
                item.Name, item.TenantId, item.ClientId, item.ClientSecret);

            item.SecretId = GetNextId(SettingType.EntraCredentials);
            AddSetting(SettingType.EntraCredentials, item.SecretId, item.Name);

            return item;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to add {SettingTypes.AppRegistration}");
            throw new AiCoreUiException($"Failed to add {SettingTypes.AppRegistration}");
        }
    }

    private async Task<SecretExtendedItem> AddSecret(SecretExtendedItem item)
    {
        try
        {
            await _entraTokenProvider.SetSecretToKeyVaultAsync(item.Name, item.ClientSecret);

            item.SecretId = Math.Max(GetNextId(SettingType.SecretValue), SecretsIdStart);
            AddSetting(SettingType.SecretValue, item.SecretId, item.Name);

            return item;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to add {SettingTypes.Secret}");
            throw new AiCoreUiException($"Failed to add {SettingTypes.Secret}");
        }
    }

    public Task<List<SecretViewModel>> List()
    {
        var entraCredentials = _settingsProcessor.Get(SettingType.EntraCredentials)
            .Select(e => new SecretViewModel
            {
                SecretId = Convert.ToInt32(e.Key),
                Name = e.Value,
                Type = SettingTypes.AppRegistration
            });

        var secretValues = _settingsProcessor.Get(SettingType.SecretValue)
            .Select(e => new SecretViewModel
            {
                SecretId = Convert.ToInt32(e.Key),
                Name = e.Value,
                Type = SettingTypes.Secret
            });

        return Task.FromResult(entraCredentials.Concat(secretValues).ToList());
    }

    public async Task Delete(int secretId)
    {
        try
        {
            if (secretId >= SecretsIdStart)
            {
                await DeleteInternal(secretId, SettingType.SecretValue, name => _entraTokenProvider.RemoveItemFromKeyVaultAsync(name));
            }
            else
            {
                await DeleteInternal(secretId, SettingType.EntraCredentials, name => _entraTokenProvider.RemoveItemFromKeyVaultAsync(name));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete secret");
            throw new AiCoreUiException("Failed to delete secret");
        }
    }
    
    public async Task<SecretViewModel> AddToDatabase(SecretViewModel item)
    {
        var type = item.Type == SettingTypes.AppRegistration
            ? SettingType.EntraCredentials
            : SettingType.SecretValue;

        var settings = _settingsProcessor.Get(type);

        var existing = settings.FirstOrDefault(kvp => kvp.Value == item.Name);
        if (!string.IsNullOrEmpty(existing.Value))
        {
            item.SecretId = Convert.ToInt32(existing.Key);
            return item;
        }

        if (item.SecretId > 0 && settings.ContainsKey(item.SecretId.ToString()))
        {
            settings[item.SecretId.ToString()] = item.Name;
            _settingsProcessor.Set(type, settings);
            return item;
        }

        var newId = GetNextId(type);

        if (type == SettingType.SecretValue)
            newId = Math.Max(newId, SecretsIdStart);

        item.SecretId = newId;

        settings[newId.ToString()] = item.Name;
        _settingsProcessor.Set(type, settings);

        return item;
    }

    // --- Helpers ---

    private int GetNextId(SettingType type)
    {
        var settings = _settingsProcessor.Get(type);
        return settings.Select(e => Convert.ToInt32(e.Key)).DefaultIfEmpty().Max() + 1;
    }

    private void AddSetting(SettingType type, int id, string name)
    {
        var settings = _settingsProcessor.Get(type);
        settings[id.ToString()] = name;
        _settingsProcessor.Set(type, settings);
    }

    private async Task DeleteInternal(int id, SettingType type, Func<string, Task> keyVaultDelete)
    {
        var settings = _settingsProcessor.Get(type);
        if (!settings.TryGetValue(id.ToString(), out var name))
            throw new AiCoreUiException($"{type} with id {id} not found");

        settings.Remove(id.ToString());
        _settingsProcessor.Set(type, settings);
        await keyVaultDelete(name);
    }
}

public interface ISecretsService
{
    Task<SecretExtendedItem> Add(SecretExtendedItem secretExtendedItem);
    Task<List<SecretViewModel>> List();
    Task Delete(int secretId);
    Task<SecretViewModel> AddToDatabase(SecretViewModel secretExtendedItem);
}
