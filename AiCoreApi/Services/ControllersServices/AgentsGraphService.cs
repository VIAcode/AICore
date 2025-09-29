using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AutoMapper;
using System.Text.RegularExpressions;

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
        var flowGroups = agents
            .Where(x => !string.IsNullOrEmpty(x.FlowName))
            .GroupBy(x => x.FlowName ?? string.Empty)
            .ToDictionary(x => x.Key, v => v);

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

            if (agent.Type == Models.DbModels.AgentType.Flow && flowGroups.TryGetValue(agent.Name, out var flowAgents))
            {
                edges.AddRange(flowAgents
                    .Select(x => new GraphEdgeViewModel
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
