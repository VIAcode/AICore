using AiCoreApi.Common.Extensions;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using JUST;
using AiCoreApi.Common;
using Newtonsoft.Json.Linq;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class JsonTransformAgent : BaseAgent, IJsonTransformAgent
    {
        private string _debugMessageSenderName = "JsonTransformAgent";

        private readonly ResponseAccessor _responseAccessor;
        public JsonTransformAgent(
            IBaseAgentHelper baseAgentHelper,
            ResponseAccessor responseAccessor,
            ILogger<JsonTransformAgent> logger) : base(baseAgentHelper, logger)
        {
            _responseAccessor = responseAccessor;
        }

        private static class AgentContentParameters
        {
            public const string Transformer = "transformer";
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            try
            {
                var inputJson = parameters["parameter1"];
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Input", inputJson);
                inputJson = inputJson.FixJsonSyntax();

                var transformer = agent.Content[AgentContentParameters.Transformer].Value;
                var useValue = false; 
                if(transformer.StartsWith("#valueof="))
                {
                    transformer = transformer.Substring("#valueof=".Length);
                    useValue = true;
                }
                transformer = transformer.FixJsonSyntax();
                var transformedString = new JsonTransformer().Transform(transformer, inputJson);
                if(useValue)
                {
                    // take first child value
                    transformedString = JObject.Parse(transformedString).Cast<JProperty>().First().Value.ToString();
                }
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Transformed", transformedString);
                return transformedString;
            }
            catch(Exception e)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Error", e.Message);
                // Suppress for invalid json
                return string.Empty;
            }
        }
    }

    public interface IJsonTransformAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
