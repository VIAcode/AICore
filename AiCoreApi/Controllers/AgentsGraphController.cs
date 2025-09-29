using AiCoreApi.Authorization;
using AiCoreApi.Authorization.Attributes;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using System.Text.RegularExpressions;

namespace AiCoreApi.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/v1/agents-graph")]
    public class AgentsGraphController : ControllerBase
    {
        private readonly IAgentsProcessor agentsProcessor;

        public AgentsGraphController(IAgentsProcessor agentsProcessor)
        {
            this.agentsProcessor = agentsProcessor;
        }

        [HttpGet]
        [CombinedAuthorize]
        [RoleAuthorize(Role.Admin, Role.Developer)]
        [SwaggerOperation(Summary = "Get agents dependency graph", Description = "Returns a graph structure showing dependencies between agents and flows")]
        [SwaggerResponse(200, "Successfully retrieved dependency graph", typeof(DependencyGraph))]
        [SwaggerResponse(401, "Unauthorized")]
        [SwaggerResponse(403, "Forbidden")]
        public async Task<ActionResult<DependencyGraph>> GetDependencyGraph([FromQuery(Name = "workspace_id")] int workspaceId = 0)
        {
            var agents = await agentsProcessor.List(workspaceId);
            var dependencyGraph = BuildDependencyGraph(agents);
            return Ok(dependencyGraph);
        }

        private DependencyGraph BuildDependencyGraph(List<AgentModel> agents)
        {
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            var agentMap = agents.ToDictionary(a => a.Name, a => a);
            var flowGroups = agents
                .Where(x => !string.IsNullOrEmpty(x.FlowName))
                .GroupBy(x => x.FlowName ?? string.Empty)
                .ToDictionary(x => x.Key, v => v);

            foreach (var agent in agents)
            {
                nodes.Add(new GraphNode
                {
                    Id = agent.AgentId,
                    Label = agent.Name,
                    Type = (int)agent.Type,
                    IsEnabled = agent.IsEnabled,
                    Description = agent.Description,
                    FlowName = agent.FlowName,
                    Tags = agent.Tags,
                    Content = agent.Content
                });

                if (agent.Type == AgentType.Flow && flowGroups.TryGetValue(agent.Name, out var flowAgents))
                {
                    edges.AddRange(flowAgents
                        .Select(x => new GraphEdge
                        {
                            From = agent.AgentId,
                            To = x.AgentId,
                            FromLabel = agent.Name,
                            ToLabel = x.Name
                        })
                        .ToList());
                }

                var dependencies = ExtractAgentDependencies(agent, agentMap);
                var sourceNodeId = agent.AgentId; // Always use individual agent ID

                foreach (var depName in dependencies)
                {
                    if (agentMap.TryGetValue(depName, out var depAgent))
                    {
                        var targetNodeId = depAgent.AgentId; // Always use individual agent ID

                        // Avoid self-references
                        if (sourceNodeId != targetNodeId)
                        {
                            edges.Add(new GraphEdge
                            {
                                From = sourceNodeId,
                                To = targetNodeId,
                                FromLabel = agent.Name,
                                ToLabel = depAgent.Name
                            });
                        }
                    }
                }

            }

            return new DependencyGraph
            {
                Nodes = nodes,
                Edges = edges
            };
        }


        private HashSet<string> ExtractAgentDependencies(AgentModel agent, Dictionary<string, AgentModel> agentMap)
        {
            var dependencies = new HashSet<string>();

            if (agent.Content == null) return dependencies;

            // Patterns for agent execution methods (based on actual data analysis)
            var patterns = new[]
            {
                // ExecuteAgent patterns used in both Python and C# code
                @"ExecuteAgent\(\s*""([^""]+)""",  // ExecuteAgent("AgentName")
                @"ExecuteAgent\('([^']+)'",       // ExecuteAgent('AgentName')
            };

            foreach (var field in agent.Content.Values)
            {
                if (string.IsNullOrEmpty(field?.Value)) continue;

                foreach (var pattern in patterns)
                {
                    var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    var matches = regex.Matches(field.Value);
                    foreach (Match match in matches)
                    {
                        var agentName = match.Groups[1].Value;
                        if (agentMap.ContainsKey(agentName) && agentName != agent.Name)
                        {
                            dependencies.Add(agentName);
                        }
                    }
                }
            }

            return dependencies;
        }


        // Simple graph visualization models
        public class DependencyGraph
        {
            public List<GraphNode> Nodes { get; set; } = new();
            public List<GraphEdge> Edges { get; set; } = new();
        }

        public class GraphNode
        {
            public int Id { get; set; }
            public string Label { get; set; } = string.Empty;
            public int Type { get; set; }
            public bool IsEnabled { get; set; } = true;
            public string Description { get; set; } = string.Empty;
            public string? FlowName { get; set; }
            public List<TagModel> Tags { get; set; } = new();
            public Dictionary<string, ConfigurableSetting> Content { get; set; } = new();
        }

        public class GraphEdge
        {
            public int From { get; set; }
            public int To { get; set; }
            public string FromLabel { get; set; } = string.Empty;
            public string ToLabel { get; set; } = string.Empty;
        }
    }
}
