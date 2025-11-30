using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.SemanticKernel;

namespace AiCoreApi.Common
{
    public class AgentsFlowDescriber : IAgentsFlowDescriber
    {
        private readonly ExtendedConfig _extendedConfig;
        private readonly IAgentsProcessor _agentsProcessor;
        private readonly IConnectionManager _connectionManager;
        private readonly ISemanticKernelProvider _semanticKernelProvider;

        private const string SystemMessage = "You are a helpful assistant that helps to create and describe AI agents based on user requirements. Provide clear and concise descriptions.";
        private const string PromptTemplate = @"Analyze the following AI agent configuration and provide a structured response.

Agent Configuration:
{requirements}

Please provide:
1. A clear description of what this agent does and its purpose
2. List any other agents that this agent calls or executes (look for ExecuteAgent calls in code, or agent references in prompts)

Focus on the key features, functionalities and business logic. Do not take into account if agent is enabled or disabled. Be concise but informative.";

        private const string JsonSchema = @"{
            ""type"": ""object"",
            ""properties"": {
                ""description"": {
                    ""type"": ""string"",
                    ""description"": ""A clear and concise description of what this agent does and its purpose""
                },
                ""callingAgents"": {
                    ""type"": ""array"",
                    ""items"": {
                        ""type"": ""string""
                    },
                    ""description"": ""List of agent names that this agent calls or executes""
                }
            },
            ""required"": [""description"", ""callingAgents""]
        }";

        public class ConnectionParameters
        {
            public const string Temperature = "temperature";

        }

        public AgentsFlowDescriber(
            ExtendedConfig extendedConfig,
            IAgentsProcessor agentsProcessor,
            IConnectionManager connectionManager,
            ISemanticKernelProvider semanticKernelProvider)
        {
            _extendedConfig = extendedConfig;
            _agentsProcessor = agentsProcessor;
            _connectionManager = connectionManager;
            _semanticKernelProvider = semanticKernelProvider;
        }

        public async Task<WorkspaceAgent> GetAgentsDescription(int workspaceId, int agentId)
        {
            try
            {
                // Get the specific agent
                var agent = await _agentsProcessor.GetById(agentId);
                if (agent == null)
                {
                    return new WorkspaceAgent
                    {
                        Description = "Agent not found.",
                        CallingAgents = new List<string>(),
                        CalledByAgents = new List<string>()
                    };
                }

                var allAgents = await _agentsProcessor.List(workspaceId);
                var requirements = BuildAgentRequirements(agent, allAgents);
                var (callingAgents, calledByAgents) = FindAgentRelationships(agent, allAgents);
                var prompt = PromptTemplate.Replace("{requirements}", requirements);
                var llmResponse = await ExecutePrompt(workspaceId, prompt);
                var (description, llmCallingAgents) = ParseLlmResponse(llmResponse);
                var allCallingAgents = callingAgents.Union(llmCallingAgents).Distinct().ToList();
                
                return new WorkspaceAgent
                {
                    Description = description,
                    CallingAgents = allCallingAgents,
                    CalledByAgents = calledByAgents
                };
            }
            catch (Exception ex)
            {
                return new WorkspaceAgent
                {
                    Description = $"Error generating description: {ex.Message}",
                    CallingAgents = new List<string>(),
                    CalledByAgents = new List<string>()
                };
            }
        }

        private string BuildAgentRequirements(AgentModel agent, List<AgentModel> allAgents)
        {
            var requirements = new List<string>
            {
                $"Agent Name: {agent.Name}",
                $"Agent Type: {agent.Type}",
                $"Description: {agent.Description}",
                $"Version: {agent.Version}",
                $"Is Enabled: {agent.IsEnabled}"
            };
            if (agent.Tags.Any())
            {
                requirements.Add($"Tags: {string.Join(", ", agent.Tags.Select(t => t.Name))}");
            }
            
            if (!string.IsNullOrEmpty(agent.FlowName))
            {
                requirements.Add($"Flow Name: {agent.FlowName}");
            }
            
            if (agent.Content.Any())
            {
                requirements.Add("Configuration:");
                foreach (var content in agent.Content)
                {
                    if (!string.IsNullOrEmpty(content.Value.Name))
                    {
                        var value = content.Value.Value;
                        
                        // For code agents, provide more context about potential agent calls
                        if (IsCodeAgent(agent.Type) && !string.IsNullOrEmpty(value))
                        {
                            var agentReferences = FindAgentReferencesInCode(value, allAgents);
                            if (agentReferences.Any())
                            {
                                requirements.Add($"  - {content.Value.Name}: {value}");
                                requirements.Add($"    -> Potential agent calls: {string.Join(", ", agentReferences)}");
                            }
                            else
                            {
                                requirements.Add($"  - {content.Value.Name}: {value}");
                            }
                        }
                        else
                        {
                            requirements.Add($"  - {content.Value.Name}: {value}");
                        }
                    }
                }
            }
            if (agent.LlmType.HasValue)
            {
                requirements.Add($"LLM Connection ID: {agent.LlmType.Value}");
            }
            var otherAgents = allAgents.Where(a => a.AgentId != agent.AgentId).Select(a => a.Name).ToList();
            if (otherAgents.Any())
            {
                requirements.Add($"Available agents in workspace: {string.Join(", ", otherAgents)}");
            }
            return string.Join("\n", requirements);
        }
        
        private bool IsCodeAgent(AgentType agentType)
        {
            return agentType == AgentType.PythonCode || 
                   agentType == AgentType.CsharpCode || 
                   agentType == AgentType.NodeJsCode ||
                   agentType == AgentType.CompositeCSharp ||
                   agentType == AgentType.CompositePython;
        }
        
        private List<string> FindAgentReferencesInCode(string code, List<AgentModel> allAgents)
        {
            var references = new List<string>();
            
            // Look for ExecuteAgent calls and other common patterns
            var patterns = new[]
            {
                @"ExecuteAgent\s*\(\s*[""']([^""']+)[""']",
                @"executeAgent\s*\(\s*[""']([^""']+)[""']",
            };
            
            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(code, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var agentName = match.Groups[1].Value;
                        if (allAgents.Any(a => a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase)))
                        {
                            references.Add(agentName);
                        }
                    }
                }
            }
            return references.Distinct().ToList();
        }
        
        private (List<string> callingAgents, List<string> calledByAgents) FindAgentRelationships(AgentModel targetAgent, List<AgentModel> allAgents)
        {
            var callingAgents = new List<string>();
            var calledByAgents = new List<string>();
            foreach (var agent in allAgents)
            {
                if (agent.AgentId == targetAgent.AgentId) 
                    continue;
                
                var agentContent = string.Join(" ", agent.Content.Values.Select(c => c.Value));
                if (agentContent.Contains(targetAgent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    calledByAgents.Add(agent.Name);
                }
                
                var targetContent = string.Join(" ", targetAgent.Content.Values.Select(c => c.Value));
                if (targetContent.Contains(agent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    callingAgents.Add(agent.Name);
                }
            }
            return (callingAgents, calledByAgents);
        }
        
        private (string description, List<string> callingAgents) ParseLlmResponse(string llmResponse)
        {
            try
            {
                // Try to parse as JSON first
                var jsonResponse = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(llmResponse);
                
                if (jsonResponse != null)
                {
                    var description = jsonResponse.description?.ToString() ?? "No description provided.";
                    var callingAgents = new List<string>();
                    
                    if (jsonResponse.callingAgents != null)
                    {
                        foreach (var agent in jsonResponse.callingAgents)
                        {
                            var agentName = agent?.ToString();
                            if (!string.IsNullOrEmpty(agentName))
                            {
                                callingAgents.Add(agentName);
                            }
                        }
                    }
                    
                    return (description, callingAgents);
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Not a JSON response, proceed to fallback parsing
            }
            return (llmResponse, new List<string>());
        }

        private async Task<string> ExecutePrompt(int workspaceId, string prompt)
        {
            if(!_extendedConfig.UseAgentsDescription)
                return "Agents description feature is disabled.";
            var connection = await _connectionManager.GetConnectionWithParams(workspaceId, connectionName: _extendedConfig.AgentsDescriptionLlmConnectionName);
            if (connection == null || !connection.Type.IsLlmConnection())
                return $"No valid LLM connection found with name '{_extendedConfig.AgentsDescriptionLlmConnectionName}'";
            var topP = 1;
            var temperature = connection.Content.ContainsKey(ConnectionParameters.Temperature) 
                ? Convert.ToDouble(connection.Content[ConnectionParameters.Temperature])
                : 1;

            var result = await _semanticKernelProvider.ExecutePrompt(connection, prompt, temperature, topP, SystemMessage, JsonSchema);
            return result;
        }

        public class WorkspaceAgents
        {
            public List<WorkspaceAgent> Agents { get; set; } = new();
        }

        public class WorkspaceAgent
        {
            public string Description { get; set; } = "";
            public List<string> CallingAgents { get; set; } = new();
            public List<string> CalledByAgents { get; set; } = new();
        }
    }

    public interface IAgentsFlowDescriber
    {
        Task<AgentsFlowDescriber.WorkspaceAgent> GetAgentsDescription(int workspaceId, int agentId);
    }
}
