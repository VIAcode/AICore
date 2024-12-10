﻿namespace AiCoreApi.Models.ViewModels
{
    public class LoginProcessViewModel
    {
        public string ClientId { get; set; }
        public string RedirectUri { get; set; } = string.Empty;
        public string CodeChallenge { get; set; } = string.Empty;
        public string CodeChallengeMethod { get; set; } = string.Empty;
        public List<string> AcrValues { get; set; } = new();
        public string Scope { get; set; } = string.Empty;
        public string ResponseType { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool IsPermanentToken { get; set; } = false;
    }
}