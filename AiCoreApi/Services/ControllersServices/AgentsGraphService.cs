using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel.Agents;
using AutoMapper;
using System.Text.RegularExpressions;
using AgentTypeEnum = AiCoreApi.Models.DbModels.AgentType;

namespace AiCoreApi.Services.ControllersServices;

public class AgentsGraphService : IAgentsGraphService
{
    private readonly IAgentsProcessor _agentsProcessor;
    private readonly IMapper _mapper;

    public AgentsGraphService(IAgentsProcessor agentsProcessor, IMapper mapper)
    {
        _agentsProcessor = agentsProcessor;
        _mapper = mapper;
    }

    public async Task<DependencyGraphViewModel> GetDependencyGraph(int workspaceId)
    {
        var agents = await _agentsProcessor.List(workspaceId);
        return BuildDependencyGraph(agents);
    }

    private DependencyGraphViewModel BuildDependencyGraph(List<AgentModel> agents)
    {
        var nodes = new List<GraphNodeViewModel>();
        var edges = new List<GraphEdgeViewModel>();
        var agentMap = agents.ToDictionary(a => a.Name, a => a);

        foreach (var agent in agents)
        {
            var nodeViewModel = new GraphNodeViewModel
            {
                Id = agent.AgentId,
                Label = agent.Name,
                Type = (int)agent.Type,
                IsEnabled = agent.IsEnabled,
                Description = agent.Description,
                FlowName = agent.FlowName,
                Tags = _mapper.Map<List<TagViewModel>>(agent.Tags),
                Content = _mapper.Map<Dictionary<string, ConfigurableSettingView>>(agent.Content)
            };
            nodes.Add(nodeViewModel);

            if (agent.Type == AgentTypeEnum.Composite ||
                agent.Type == AgentTypeEnum.CompositeCSharp ||
                agent.Type == AgentTypeEnum.CompositeLoop ||
                agent.Type == AgentTypeEnum.CompositePython)
            {
                var enabledAgents = agent.Content[BaseEnabledAgentsAgent.GetParameterName(agent)].Value.JsonGet<Dictionary<string, bool>>() ?? [];
                foreach (var enabledAgent in enabledAgents.Where(x => x.Value))
                {
                    var internalAgent = agents.FirstOrDefault(x => x.AgentId.ToString() == enabledAgent.Key);
                    if (internalAgent != null)
                    {
                        edges.Add(new GraphEdgeViewModel
                        {
                            From = agent.AgentId,
                            To = internalAgent.AgentId,
                            FromLabel = agent.Name,
                            ToLabel = internalAgent.Name
                        });
                    }
                }
                ;
            }

            if (agent.Type == AgentTypeEnum.Scheduler)
            {
                var compositeAgentName = agent.Content[BackgroundWorkerAgent.AgentContentParameters.CompositeAgentName].Value;
                var compositeAgent = agents.FirstOrDefault(x => x.Name == compositeAgentName);
                if (compositeAgent != null)
                {
                    edges.Add(new GraphEdgeViewModel
                    {
                        From = agent.AgentId,
                        To = compositeAgent.AgentId,
                        FromLabel = agent.Name,
                        ToLabel = compositeAgent.Name
                    });
                }
            }

            if (agent.Type == AgentTypeEnum.Flow)
            {
                var agentToCall = agents.FirstOrDefault(x => x.Name == agent.Content["agentToCall"].Value);
                if (agentToCall != null)
                {
                    edges.Add(new GraphEdgeViewModel
                    {
                        From = agent.AgentId,
                        To = agentToCall.AgentId,
                        FromLabel = agent.Name,
                        ToLabel = agentToCall.Name
                    });
                }
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
                        edges.Add(new GraphEdgeViewModel
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

        return new DependencyGraphViewModel
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
}
