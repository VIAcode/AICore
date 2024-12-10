﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json;

namespace AiCoreApi.Models.DbModels
{
    [Table("connection")]
    public class ConnectionModel
    {
        [JsonIgnore]
        [Key]
        public int ConnectionId { get; set; } = 0;
        public string Name { get; set; } = string.Empty;
        public ConnectionType Type { get; set; } = ConnectionType.SharePoint;
        [Column(TypeName = "jsonb")]
        public Dictionary<string, string> Content { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
    }

    public enum ConnectionType
    {
        SharePoint = 1,
        AzureOpenAiLlm = 2,
        AzureOpenAiEmbedding = 3,
        BingApi = 4,
        AzureWhisper = 5,
        DocumentIntelligence = 6,
        ContentSafety = 7,
        StorageAccount = 8,
        PostgreSql = 9,
        SqlServer = 10,
        Redis = 11,
        AzureAiTranslator = 12,
        AzureAiSpeech = 13,
        AzureAiSearch = 14,
    }
}
