using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class ContainsAgent : BaseAgent, IContainsAgent
    {
        private string _debugMessageSenderName = "ContainsAgent";

        private readonly ResponseAccessor _responseAccessor;
        public ContainsAgent(
            IBaseAgentHelper baseAgentHelper,
            ResponseAccessor responseAccessor,
            ILogger<ContainsAgent> logger) : base(baseAgentHelper, logger)
        {
            _responseAccessor = responseAccessor;
        }

        private static class AgentContentParameters
        {
            public const string RegexCheck = "regexCheck";
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";
            try
            {
                var inputText = parameters["parameter1"];
                var regexCheck = agent.Content[AgentContentParameters.RegexCheck].Value;

                var result = Regex.IsMatch(inputText, regexCheck);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall", $"Input: {inputText}\r\nRegex Check:{regexCheck}\r\nOutput:{result}");
                return result.ToString();
            }
            catch(Exception e)
            {
                // Suppress for invalid Regex
                return "false";
            }
        }
    }

    public interface IContainsAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
