﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCoreApi.Models.DbModels
{
    [Table("client_sso")]
    public class ClientSsoModel
    {
        [Key] public int ClientSsoId { get; set; }
        public string Name { get; set; } = string.Empty;
        public LoginTypeEnum LoginType { get; set; } = LoginTypeEnum.SsoMicrosoft;
        [Column(TypeName = "jsonb")]
        public Dictionary<string, string> Settings { get; set; } = new();
        public List<GroupModel> Groups { get; set; } = new();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = string.Empty;
    }
}