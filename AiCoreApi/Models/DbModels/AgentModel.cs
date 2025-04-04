using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCoreApi.Models.DbModels
{
    [Table("agents")]
    public class AgentModel
    {
        [Key]
        public int AgentId { get; set; } = 0;
        public bool IsEnabled { get; set; } = true;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? LlmType { get; set; }
        public AgentType Type { get; set; } = AgentType.Prompt;
        [Column(TypeName = "jsonb")]
        public Dictionary<string, ConfigurableSetting> Content { get; set; } = new();
        public List<TagModel> Tags { get; set; } = new();
        public int Version { get; set; } = 0;

    }

    public class ConfigurableSetting
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Extension { get; set; }
    }

    public enum AgentType
    {      
        ApiCall = 2,
        Prompt = 3,
        JsonTransform = 4,
        Contains = 5,
        Composite = 6,
        PythonCode = 7,
        CsharpCode = 8,
        BingSearch = 9,
        History = 10,
        RagPrompt = 11,
        Ocr = 12,
        BackgroundWorker = 13,
        ContentSafety = 14,
        ImageToText = 15,
        Whisper = 16,
        VectorSearch = 17,
        StorageAccount = 18,
        Scheduler = 19,
        PostgreSql = 20,
        SqlServer = 21,
        Redis = 22,
        AzureAiTranslator = 23,
        AzureAiSpeechCreateSpeech = 24,
        AzureAiSearch = 25,
        AzureServiceBusNotification = 26,
        AzureServiceBusListener = 27,
        RabbitMqNotification = 28,
        RabbitMqListener = 29, 
        AudioPromptAgent = 30,
        OcrClassifyDocument = 31,
        WebCrawler = 32,
        StabilityAiImages = 33,
        OcrBuildClassifierAgent = 34,
        AzureLogAnalytics = 35,
    }
}