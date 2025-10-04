namespace AiCoreApi.Models.ViewModels;

public class DependencyGraphViewModel
{
    public List<GraphNodeViewModel> Nodes { get; set; } = new();
    public List<GraphEdgeViewModel> Edges { get; set; } = new();
}

public class GraphNodeViewModel
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public string? FlowName { get; set; }
    public List<TagViewModel> Tags { get; set; } = new();
    public Dictionary<string, ConfigurableSettingView> Content { get; set; } = new();
}

public class GraphEdgeViewModel
{
    public int From { get; set; }
    public int To { get; set; }
    public string FromLabel { get; set; } = string.Empty;
    public string ToLabel { get; set; } = string.Empty;
}
