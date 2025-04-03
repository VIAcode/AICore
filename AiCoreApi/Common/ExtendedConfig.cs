using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Json.Schema.Generation;
using Json.Schema.Generation.Intents;
using System.Collections.Concurrent;
using AiCoreApi.SemanticKernel;
using DescriptionAttribute = Json.Schema.Generation.DescriptionAttribute;
using Newtonsoft.Json.Linq;

namespace AiCoreApi.Common;

public class ExtendedConfig
{
    private DateTime _nextRefresh = DateTime.MinValue;
    private ConcurrentDictionary<string, string> _configValues = new();
    private readonly ISettingsProcessor _settingsProcessor;
    private const int RefreshTimeSec = 15;
    private readonly object _lock = new();

    private readonly string _appSettings = File.ReadAllText("appsettings.json");

    public ExtendedConfig(ISettingsProcessor settingsProcessor)
    {
        _settingsProcessor = settingsProcessor;
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (DateTime.Now > _nextRefresh)
            {
                _configValues = new ConcurrentDictionary<string, string>(_settingsProcessor.Get(SettingType.Common));
                _nextRefresh = DateTime.Now.AddSeconds(RefreshTimeSec);
            }
        }
    }

    private T GetValue<T>(string key) => GetValue(key, default(T));
    private T GetValue<T>(string key, T defaultValue)
    {
        if (DateTime.Now > _nextRefresh)
            Reset();

        if (!_configValues.TryGetValue(key, out var value))
        {
            value = Environment.GetEnvironmentVariable(key.ToUpper());
        }
        if (value != null)
            return (T)Convert.ChangeType(value, typeof(T));

        var val = JObject.Parse(_appSettings).SelectToken(key);
        if (val != null)
            return val.ToObject<T>()!;
        if (defaultValue != null)
            return defaultValue;
        return default;
    }

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Search tab in main menu")]
    [Tooltip("Indicates if the Search tab should be visible in the main menu. In Search mode, we utilize a highly efficient vector search against the Qdrant database, avoiding the cost of token-based Chat.")]
    public bool UseSearchTab => GetValue<bool>("UseSearchTab");

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [Description("Proxy")]
    [Tooltip("Proxy is used to route all requests through a proxy server. Can be used for debug purposes, i.e. to use Fiddler. Sample: http://host.docker.internal:8888")]
    public string Proxy => GetValue<string>("Proxy");

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [Description("No information found text")]
    [Tooltip("Text that is displayed when no information is found or something went wrong within some Agent or Planner.")]
    public string NoInformationFoundText => GetValue<string>("NoInformationFoundText");

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [Description("Planner prompt")]
    [Tooltip("The Planner prompt serves as a template for the root Planner, providing it with instructions for LLM on how to generate a plan of action for the Agents to accomplish the desired outcome. This prompt can incorporate various placeholders: {{currentQuestion}}: The last message exchanged in the Chat dialog. {{pluginsInstructions}}: A combined text derived from the Plugins Instructions sections of all Agents. {{hasFiles}}: A boolean value indicating whether any files were attached to the last message. {{filesNames}}: A list containing the names of all files attached to the last message. {{filesData}}: The parsed text content of all files attached to the last message. It's important to note that not all placeholders may be necessary for every Planner prompt.")]
    public string PlannerPrompt => GetValue<string>("PlannerPrompt", PlannerHelpers.PlannerPromptPlaceholders.PluginsInstructionsPlaceholder);

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Collapse files section by default")]
    [Tooltip("Specifies if the Files section should be collapsed by default. When enabled, the Files section is collapsed by default, requiring the user to manually expand it.")]
    public bool CollapseFilesSection => GetValue<bool>("CollapseFilesSection", true);

    [Category(CategoryAttribute.ConfigCategoryEnum.Common)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Automatically render images in Chat")]
    [Tooltip("Specifies if images should be automatically rendered in Chat. When enabled, images are automatically rendered in the Chat window.")]
    public bool AutoRenderImages => GetValue<bool>("AutoRenderImages", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Ingestion delay in hours")]
    [Tooltip("The ingestion delay is the time in hours that the system waits between starting the ingestion processes. This delay is used to prevent overloading the system with ingestion tasks.")]
    public int IngestionDelay => GetValue<int>("IngestionDelay");

    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Max task history in hours")]
    [Tooltip("The maximum number of hours that the system keeps the history of ingestion tasks. This history is used to track the status of ingestion tasks and to provide information about the ingestion process.")]
    public int MaxTaskHistory => GetValue<int>("MaxTaskHistory");

    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Max file size in bytes")]
    [Tooltip("The maximum file size in bytes that can be ingested into the system. Files larger than this size will not be ingested.")]
    public int MaxFileSize => GetValue<int>("MaxFileSize");

    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Hidden)]
    [Description("File Ingestion Service Url")]
    [Tooltip("The File Ingestion Service Url is used to specify the url that will be used by the File Ingestion Service.")]
    public string FileIngestionUrl => GetValue<string>("FileIngestionUrl");

    // Note: consider moving to Config.cs if no plans to expose it on the UI
    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Hidden)]
    [Description("Max number of parallel file ingestion requests")]
    [Tooltip("The maximum number of parallel file ingestion requests that can be processed by the system at the same time.")]
    public int MaxParallelFileIngestionRequests => GetValue<int>("MaxParallelFileIngestionRequests");

    // Note: consider moving to Config.cs if no plans to expose it on the UI
    [Category(CategoryAttribute.ConfigCategoryEnum.Ingestion)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Hidden)]
    [Description("File ingestion request timeout in minutes")]
    [Tooltip("The timeout in minutes for file ingestion requests. If the request takes longer than this time, it will be canceled.")]
    public int FileIngestionRequestTimeout => GetValue<int>("FileIngestionRequestTimeout");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Microsoft SSO accounts")]
    [Tooltip("Specifies if Microsoft SSO is enabled for authentication. When activated, a domain account login button becomes available on the Login Screen. Moreover, the \"Users\" section in the Main Menu gains a new \"SSO Clients\" subtab for managing allowed domains.")]
    public bool UseMicrosoftSso => GetValue<bool>("UseMicrosoftSso");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Google SSO accounts")]
    [Tooltip("Specifies if Google SSO is enabled for authentication. When activated, a Google account login button becomes available on the Login Screen.")]
    public bool UseGoogleSso => GetValue<bool>("UseGoogleSso");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use internal user accounts")]
    [Tooltip("Controls the use of internal user accounts for authentication. If disabled, only domain accounts are available for login (login and password textboxes removed from Login screen). ")]
    public bool UseInternalUsers => GetValue<bool>("UseInternalUsers");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Token expiration time in minutes")]
    [Tooltip("The regular token expiration time specifies the duration in minutes after which the token becomes invalid. Once expired, the user must log in again. Each time a new access token is obtained using a refresh token, session lifetime is renewed.")]
    public int TokenExpirationTimeMinutes => GetValue<int>("TokenExpirationTimeMinutes");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Permanent Refresh Token expiration time in days")]
    [Tooltip("The permanent refresh token expiration time determines the number of days before a permanent refresh token becomes invalid. This is used in scenarios requiring long-lived tokens, such as external applications using OIDC for login. Each time the new access token generated, the refresh token lifetime is extended.")]
    public int PermanentTokenExpirationTimeDays => GetValue<int>("PermanentTokenExpirationTimeDays", 365);
    
    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [Description("Auth Issuer")]
    [Tooltip("The Auth Issuer is used to specify the issuer of the authentication token. This is used to verify the token.")]
    public string AuthIssuer => GetValue<string>("AuthIssuer");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [Description("Auth Audience")]
    [Tooltip("The Auth Audience is used to specify the audience of the authentication token. This is used to verify the token.")]
    public string AuthAudience => GetValue<string>("AuthAudience");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("Auth security key")]
    [Tooltip("The Auth Security Key serves as a security key for the SHA-256 hashing algorithm during the creation of access tokens to sign-in JWT token.")]
    public string AuthSecurityKey => GetValue<string>("AuthSecurityKey");
    
    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [Description("Tenant Id (Microsoft SSO)")]
    [Tooltip("The Tenant Id is used to specify the tenant for the Microsoft SSO service. This Id is used to authenticate the application with the Microsoft SSO service. If empty, then user`s default Tenant will be used.")]
    public string TenantId => GetValue<string>("TenantId", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [Description("App Registration Client Id (Microsoft SSO)")]
    [Tooltip("The App Registration Client Id is used to authenticate the application with the Microsoft SSO service.")]
    public string ClientId => GetValue<string>("ClientId");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("App Registration Client Secret (Microsoft SSO)")]
    [Tooltip("The App Registration Client Secret is used to authenticate the application with the Microsoft SSO service.")]
    public string ClientSecret => GetValue<string>("ClientSecret");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("App Registration Scope (Microsoft SSO)")]
    [Tooltip("The App Registration Scope is used to specify the scope of the application. This scope is used to authenticate the application with the Microsoft SSO service.")]
    public string ClientScope => GetValue<string>("ClientScope", "Directory.Read.All+GroupMember.Read.All+Group.Read.All");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [Description("Google App Client Id (Google SSO)")]
    [Tooltip("The Google Client Id is used to authenticate the application with the Google SSO service.")]
    public string GoogleClientId => GetValue<string>("GoogleClientId");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("Google App Client Secret (Google SSO)")]
    [Tooltip("The Google Client Secret is used to authenticate the application with the Google SSO service.")]
    public string GoogleClientSecret => GetValue<string>("GoogleClientSecret");

    [Category(CategoryAttribute.ConfigCategoryEnum.Authentication)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Allow Basic Auth for Chat / Search")]
    [Tooltip("Specifies if Basic Authentication is allowed for Chat and Search. When enabled, the user can log in using a username and password. Only a few endpoints are available with Basic auth.")]
    public bool AllowBasicAuth => GetValue<bool>("AllowBasicAuth", true);

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Main Color")]
    [Tooltip("Main Color is used to specify the main color of the application. This color is used for the main elements of the user interface, such as the header.")]
    public string MainColor => GetValue<string>("MainColor");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Main Text Color")]
    [Tooltip("Main Text Color is used to specify the main text color of the application. This color is used for the main text elements of the user interface.")]
    public string MainTextColor => GetValue<string>("MainTextColor");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Page Title")]
    [Tooltip("Page Title is used to specify the title of the application. This title is displayed in the browser tab.")]
    public string PageTitle => GetValue<string>("PageTitle");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Secondary Text Color")]
    [Tooltip("Secondary Text Color is used to specify the secondary text color of the application. This color is used for the secondary text elements of the user interface.")]
    public string SecondaryTextColor => GetValue<string>("SecondaryTextColor");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Contrast Text Color")]
    [Tooltip("Contrast Text Color is used to specify the contrast text color of the application. This color is used for the contrast text elements of the user interface.")]
    public string ContrastTextColor => GetValue<string>("ContrastTextColor");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Menu Background Color 1")]
    [Tooltip("Menu Background Color 1 is used to specify the first color of the menu background. This color is used for the first color of the menu background.")]
    public string MenuBackColor1 => GetValue<string>("MenuBackColor1");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Menu Background Color 2")]
    [Tooltip("Menu Background Color 2 is used to specify the second color of the menu background. This color is used for the second color of the menu background.")]
    public string MenuBackColor2 => GetValue<string>("MenuBackColor2");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Color)]
    [Description("Background Color")]
    [Tooltip("Background Color is used to specify the background color of the application. This color is used for the background of the user interface.")]
    public string BackgroundColor => GetValue<string>("BackgroundColor");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Url)]
    [Description("Logo Url")]
    [Tooltip("The Logo URL is the web address that points to the image file representing the application's logo. This logo is prominently displayed in the main menu header and on the login page, providing a visual identifier for the application.")]
    public string LogoUrl => GetValue<string>("LogoUrl");

    [Category(CategoryAttribute.ConfigCategoryEnum.UiTheme)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Url)]
    [Description("Favicon Url")]
    [Tooltip("The Favicon URL is the web address that points to the image file representing the application's favicon. This favicon is displayed in the browser tab, providing a visual identifier for the application.")]
    public string FavIconUrl => GetValue<string>("FavIconUrl", "/logo.ico");

    [Category(CategoryAttribute.ConfigCategoryEnum.Jira)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("JIRA Connector url")]
    [Tooltip("The JIRA Connector URL is used to specify the URL of the JIRA Connector. This URL is used to connect to the JIRA service.")]
    public string JiraConnectorUrl => GetValue<string>("JiraConnectorUrl");

    [Category(CategoryAttribute.ConfigCategoryEnum.Jira)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("JIRA Connector Credentials")]
    [Tooltip("The JIRA Connector Credentials are used to authenticate the JIRA Connector (Basic auth, login:password here). These credentials are used to connect to the JIRA service.")]
    public string JiraConnectorCredentials => GetValue<string>("JiraConnectorCredentials");

    [Category(CategoryAttribute.ConfigCategoryEnum.Jira)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Link)]
    [Description("Set credentials for JIRA")]
    [Tooltip("Click here to set credentials for JIRA Connector.")]
    public string JiraAuthUrl => GetValue<string>("JiraAuthUrl");

    [Category(CategoryAttribute.ConfigCategoryEnum.Debug)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Allow Debug Mode")]
    [Tooltip("Specifies if Debug Mode is allowed. When enabled, the Debug Mode enabler becomes available in the Chat window. Debug Mode allows you to see the raw response from the AI model.")]
    public bool AllowDebugMode => GetValue<bool>("AllowDebugMode", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Debug)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Int)]
    [Description("Store Debug Messages for N days")]
    [Tooltip("Debug Messages are stored in the system for a specified number of days. After this period, the Debug Messages are automatically deleted.")]
    public int DebugMessagesStoreDays => GetValue<int>("DebugMessagesStoreDays", 30);

    [Category(CategoryAttribute.ConfigCategoryEnum.Debug)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Debug Messages Storage Enabled")]
    [Tooltip("When enabled, the system stores debug messages for a specified number of days.")]
    public bool DebugMessagesStorageEnabled => GetValue<bool>("DebugMessagesStorageEnabled", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Nuget)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Custom Nuget Feed")]
    [Tooltip("Specifies if a custom Nuget feed is used. When enabled, the system uses a custom Nuget feed for package management.")]
    public bool UseCustomNugetFeed => GetValue<bool>("UseCustomNugetFeed", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Nuget)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Nuget.org when no package in Custom Nuget Feed")]
    [Tooltip("Specifies if the system should use Nuget.org when a package is not found in the custom Nuget feed. When enabled, the system will use Nuget.org as a fallback.")]
    public bool UseNugetOrgFallback => GetValue<bool>("UseNugetOrgFallback", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Nuget)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Custom Nuget Feed URL")]
    [Tooltip("The Custom Nuget Feed URL is used to specify the URL of the custom Nuget feed. This URL is used for package management.")]
    public string CustomNugetFeedUrl => GetValue<string>("CustomNugetFeedUrl", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.Nuget)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("Custom Nuget Feed API Key")]
    [Tooltip("The Custom Nuget Feed API Key is used to authenticate the custom Nuget feed. This key is used for package management.")]
    public string CustomNugetFeedApiKey => GetValue<string>("CustomNugetFeedApiKey", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [Description("Log Level")]
    [Tooltip("Log Level is used to specify the level of logging that the system should use. The log level determines the amount of information that is logged by the system. The available log levels are: Debug, Information, Warning, Error, and Critical.")]
    public string LogLevel => GetValue<string>("LogLevel", "Debug");

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Login/Logout events")]
    [Tooltip("Log Login/Logout events specifies if the system should log login and logout events. When enabled, the system logs information about user logins and logouts.")]
    public bool LogLoginLogout => GetValue<bool>("LogLoginLogout", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Access Token check events")]
    [Tooltip("Log Access Token check events specifies if the system should log access token check events. When enabled, the system logs information about access token checks.")]
    public bool LogAccessTokenCheck => GetValue<bool>("LogAccessTokenCheck", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent Run events")]
    [Tooltip("Log Agent Run specifies if the system should log Agent run events. When enabled, the system logs information about Agent runs.")]
    public bool LogAgentRun => GetValue<bool>("LogAgentRun", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent Result events")]
    [Tooltip("Log Agent Result specifies if the system should log Agent result events. When enabled, the system logs information about Agent results.")]
    public bool LogAgentResult => GetValue<bool>("LogAgentResult", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.Logging)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Log Agent PII info (input parameters/result)")]
    [Tooltip("Log Agent PII info specifies if the system should log Agent PII info. When enabled, the system logs Agent input parameters and result.")]
    public bool LogAgentPii => GetValue<bool>("LogAgentPii", false);

    [Category(CategoryAttribute.ConfigCategoryEnum.KeyVaultStorage)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Key Vault Url")]
    [Tooltip("The Key Vault Url is used to specify the URL of the Key Vault Storage. This URL is used to connect to the Key Vault Storage.")]
    public string KeyVaultUrl => GetValue<string>("KeyVaultUrl", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.KeyVaultStorage)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Key Vault App Registration Tenant Id (Optional)")]
    [Tooltip("The Key Vault App Tenant Id is used to authenticate the application with the Key Vault Storage. This Id is used to connect to the Key Vault Storage. In not specified, the system will use the Default Managed Identity.")]
    public string KeyVaultAppTenantId => GetValue<string>("KeyVaultAppTenantId", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.KeyVaultStorage)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.String)]
    [Description("Key Vault App Registration Client Id (Optional)")]
    [Tooltip("The Key Vault App Client Id is used to authenticate the application with the Key Vault Storage. This Id is used to connect to the Key Vault Storage. In not specified, the system will use the Default Managed Identity.")]
    public string KeyVaultAppClientId => GetValue<string>("KeyVaultAppClientId", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.KeyVaultStorage)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Password)]
    [Description("Key Vault App Registration Client Secret (Optional)")]
    [Tooltip("The Key Vault App Client Secret is used to authenticate the application with the Key Vault Storage. This Secret is used to connect to the Key Vault Storage. In not specified, the system will use the Default Managed Identity.")]
    public string KeyVaultAppClientSecret => GetValue<string>("KeyVaultAppClientSecret", "");

    [Category(CategoryAttribute.ConfigCategoryEnum.KeyVaultStorage)]
    [DataType(DataTypeAttribute.ConfigDataTypeEnum.Boolean)]
    [Description("Use Key Vault App Registration")]
    [Tooltip("Specifies if the system should use the Key Vault App Registration. When enabled, the system uses the Key Vault App Registration for authentication.")]
    public bool UseKeyVaultAppRegistration => GetValue<bool>("UseKeyVaultAppRegistration", false);
}

[AttributeUsage(AttributeTargets.Property)]
public class DataTypeAttribute : Attribute, IAttributeHandler
{
    public DataTypeAttribute() { }

    public ConfigDataTypeEnum DataType { get; }
    public DataTypeAttribute(ConfigDataTypeEnum dataType)
    {
        DataType = dataType;
    }

    void IAttributeHandler.AddConstraints(SchemaGenerationContextBase context, Attribute attribute)
    {
        context.Intents.Add(new DescriptionIntent(DataType.ToString()));
    }

    public enum ConfigDataTypeEnum
    {
        String,
        Password,
        Int,
        Boolean,
        Double,
        Url,
        Color,
        Hidden,
        Link
    }
}

[AttributeUsage(AttributeTargets.Property)]
public class TooltipAttribute : Attribute, IAttributeHandler
{
    public string TooltipText { get; }
    public TooltipAttribute(string tooltipText)
    {
        TooltipText = tooltipText;
    }

    void IAttributeHandler.AddConstraints(SchemaGenerationContextBase context, Attribute attribute)
    {
        context.Intents.Add(new DescriptionIntent(TooltipText));
    }
}

[AttributeUsage(AttributeTargets.Property)]
public class CategoryAttribute : Attribute, IAttributeHandler
{
    public CategoryAttribute() { }

    public ConfigCategoryEnum Category { get; }
    public CategoryAttribute(ConfigCategoryEnum category)
    {
        Category = category;
    }

    void IAttributeHandler.AddConstraints(SchemaGenerationContextBase context, Attribute attribute)
    {
        context.Intents.Add(new DescriptionIntent(Category.ToString()));
    }

    public enum ConfigCategoryEnum
    {
        [System.ComponentModel.Description("Common settings")]
        Common,
        [System.ComponentModel.Description("UI Theme")]
        UiTheme,
        [System.ComponentModel.Description("Ingestion")]
        Ingestion,
        [System.ComponentModel.Description("Authentication")]
        Authentication,
        [System.ComponentModel.Description("JIRA Connector")]
        Jira,
        [System.ComponentModel.Description("Debug")]
        Debug,
        [System.ComponentModel.Description("Nuget")]
        Nuget,
        [System.ComponentModel.Description("Logging")]
        Logging,
        [System.ComponentModel.Description("Key Vault Storage")]
        KeyVaultStorage,
    }
}