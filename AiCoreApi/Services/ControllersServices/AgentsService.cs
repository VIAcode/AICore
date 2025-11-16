using System.IO.Compression;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;
using AiCoreApi.Common.Extensions;
using Microsoft.Extensions.Caching.Distributed;
using AiCoreApi.Common;
using AiCoreApi.SemanticKernel;
using LibGit2Sharp;

namespace AiCoreApi.Services.ControllersServices;

public class AgentsService : IAgentsService
{
    private readonly IMapper _mapper;
    private readonly ILogger<AgentsService> _logger;
    private readonly IMcpClient _mcpClient;
    private readonly ExtendedConfig _extendedConfig;
    private readonly IAgentsProcessor _agentsProcessor;
    private readonly IDistributedCache _distributedCache;
    private readonly ILoginProcessor _loginProcessor;
    private readonly IConnectionProcessor _connectionProcessor;
    private readonly ITagsProcessor _tagsProcessor;
    private readonly RequestAccessor _requestAccessor;
    private readonly IAgentsFlowDescriber _agentsFlowDescriber;
    private readonly IAgentLifecycleService _agentLifecycleService;

    private const int MaxCallsLimit = 1000;

    public AgentsService(
        IMcpClient mcpClient,
        ExtendedConfig extendedConfig,
        IAgentsProcessor agentsProcessor, 
        IMapper mapper,
        ILogger<AgentsService> logger,
        IDistributedCache distributedCache,
        ILoginProcessor loginProcessor,
        IConnectionProcessor connectionProcessor,
        ITagsProcessor tagsProcessor,
        RequestAccessor requestAccessor,
        IAgentsFlowDescriber agentsFlowDescriber,
        IAgentLifecycleService agentLifecycleService)
    {
        _mcpClient = mcpClient;
        _extendedConfig = extendedConfig;
        _agentsProcessor = agentsProcessor;
        _mapper = mapper;
        _logger = logger;
        _distributedCache = distributedCache;
        _loginProcessor = loginProcessor;
        _connectionProcessor = connectionProcessor;
        _tagsProcessor = tagsProcessor;
        _requestAccessor = requestAccessor;
        _agentsFlowDescriber = agentsFlowDescriber;
        _agentLifecycleService = agentLifecycleService;
    }

    private static readonly SemaphoreSlim GitRepoLock = new(1, 1);
    private string GetRepoPath() => Path.Combine(Path.GetTempPath(), $"git-cache-{_extendedConfig.GitStoragePath.GetHashCode()}");

    public async Task<AgentViewModel> AddAgent(AgentViewModel agentViewModel, int workspaceId)
    {
        if (agentViewModel.AgentId != 0)
            throw new ArgumentException("Value should be 0.", nameof(AgentViewModel.AgentId));

        var agentModel = _mapper.Map<AgentModel>(agentViewModel);
        await _agentLifecycleService.OnAddUpdateAsync(agentModel);
        var savedModel = await _agentsProcessor.Add(agentModel, workspaceId);
        var result = _mapper.Map<AgentViewModel>(savedModel);
        await SaveGit(workspaceId);
        return result;
    }

    public async Task<AgentViewModel> UpdateAgent(AgentViewModel agentViewModel)
    {
        if (agentViewModel.AgentId == 0)
            throw new ArgumentException("Value should be not 0.", nameof(AgentViewModel.AgentId));

        var agentModel = _mapper.Map<AgentModel>(agentViewModel);
        await _agentLifecycleService.OnAddUpdateAsync(agentModel);
        var savedModel = await _agentsProcessor.Update(agentModel);
        var result = _mapper.Map<AgentViewModel>(savedModel);
        await SaveGit(agentModel.WorkspaceId);
        return result;
    }

    public async Task<List<AgentViewModel>> ListAgents(int? workspaceId)
    {
        var agents = await _agentsProcessor.List(workspaceId);
        var agentsViewModelList = _mapper.Map<List<AgentViewModel>>(agents);
        return agentsViewModelList.OrderBy(agent => agent.AgentId).ToList();
    }

    public async Task DeleteAgent(int agentId)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        await _agentLifecycleService.OnDeleteAsync(agentId);
        await _agentsProcessor.Delete(agentId);
        await SaveGit(agent?.WorkspaceId);
    }

    public async Task DeleteAgentsInWorkspace(int workspaceId)
    {
        var agents = await _agentsProcessor.List(workspaceId);
        foreach (var agent in agents)
        {
            await _agentLifecycleService.OnDeleteAsync(agent.AgentId);
            await _agentsProcessor.Delete(agent.AgentId);
        }
        await SaveGit(workspaceId);
    }

    public async Task<List<ParameterModel>?> GetParameters(int agentId)
    {
        var agentViewModel = (await ListAgents(null)).FirstOrDefault(agent => agent.AgentId == agentId);
        if (agentViewModel == null)
            return null;        
        if(!agentViewModel.Content.ContainsKey("parameterDescription") || string.IsNullOrEmpty(agentViewModel.Content["parameterDescription"].Value))
            return null;
        var parameterId = 1;
        var parameterRecordModels = ParameterRecordModel.Parse(agentViewModel.Content["parameterDescription"].Value);
        return parameterRecordModels.Select(parameter => new ParameterModel
        {
            Name = $"parameter{parameterId++}",
            Description = parameter.Name
        }).ToList();
    }

    public async Task SwitchEnableAgent(int agentId, bool isEnabled)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null)
        {            
            return;
        }
        agent.IsEnabled = isEnabled;
        await _agentsProcessor.Update(agent);
        await SaveGit(agent.WorkspaceId);
    }

    public async Task<bool> IsAgentEnabled(string agentName)
    {
        var agents = await _agentsProcessor.List(_requestAccessor.WorkspaceId);
        var agent = agents.FirstOrDefault(a => a.Name == agentName);
        if (agent == null)
            return true;
        var allUserTags = await _loginProcessor.GetTagsByLogin(_requestAccessor.Login, _requestAccessor.LoginType);
        return agent.IsEnabled && (agent.Tags.Count == 0 || agent.Tags.Select(x => x.TagId).Any(allUserTags.Select(x => x.TagId).Contains));
    }

    public async Task<byte[]> ExportAgents(List<int> agentIdsList)
    {
        var fileMap = await PrepareAgentExportFiles(agentIdsList);
        using (var zipStream = new MemoryStream())
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var kvp in fileMap)
                {
                    var entry = archive.CreateEntry(kvp.Key);
                    using (var entryStream = entry.Open())
                    {
                        using (var writer = new StreamWriter(entryStream))
                        {
                            await writer.WriteAsync(kvp.Value);
                        }
                    }
                }
            }
            return zipStream.ToArray();
        }
    }

    private async Task<Dictionary<string, string>> PrepareAgentExportFiles(List<int> agentIdsList)
    {
        var agents = await _agentsProcessor.ListAll();

        var agentsToExportDictionary = agents
            .Where(agent => agentIdsList.Contains(agent.AgentId))
            .ToDictionary(
                key => key.AgentId,
                value => new AgentModelProcessed
                {
                    AgentModel = value,
                    Processed = false
                });

        while (agentsToExportDictionary.Any(x => !x.Value.Processed))
        {
            var nonProcessedAgents = agentsToExportDictionary
                .Where(x => !x.Value.Processed)
                .Select(x => x.Value.AgentModel)
                .ToList();
            foreach (var agentModel in nonProcessedAgents)
            {
                await _agentLifecycleService.OnExportAsync(agentModel, agentsToExportDictionary);
            }
        }

        var agentsToExport = agentsToExportDictionary
            .Select(x => x.Value.AgentModel)
            .ToList();

        var agentsToExportResult = _mapper.Map<List<AgentExportModel>>(agentsToExport);
        var fileMap = new Dictionary<string, string>();
        var workspaceId = agentsToExport.FirstOrDefault(a => a.WorkspaceId != null)?.WorkspaceId;
        var connections = await _connectionProcessor.List(workspaceId);
        foreach (var agent in agentsToExportResult)
        {
            if (!string.IsNullOrEmpty(agent.LlmType))
            {
                var connectionId = int.Parse(agent.LlmType);
                var connection = connections.FirstOrDefault(c => c.ConnectionId == connectionId);
                agent.LlmType = connection?.Name ?? "0";
            }
            foreach (var content in agent.Content)
            {
                if (string.IsNullOrEmpty(content.Value.Extension))
                    continue;

                var fileName = $"{agent.Name}-{content.Value.Code}.{content.Value.Extension}";
                fileMap[fileName] = content.Value.Value;
                content.Value.Value = fileName; // Replace value with file name
            }

            var jsonName = $"{agent.Name}.json";
            fileMap[jsonName] = agent.ToJson(true);
        }
        return fileMap;
    }

    public async Task<ImportAgentsResultModel> ImportAgents(IFormFile file, Dictionary<Models.ViewModels.AgentType, int> agentVersions, int workspaceId)
    {
        var agentExportModels = new List<AgentExportModel>();
        var content = new Dictionary<string, string>();
        using (var stream = file.OpenReadStream())
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                using (var entryStream = entry.Open())
                {
                    using (var streamReader = new StreamReader(entryStream))
                    {
                        var fileContent = await streamReader.ReadToEndAsync();
                        var agent = fileContent.JsonGet<AgentExportModel>();
                        if (agent != null)
                            agentExportModels.Add(agent);
                        else
                            content.Add(entry.Name, fileContent);
                    }
                }
            }
        }
        var connections = await _connectionProcessor.List(workspaceId);
        foreach (var agentExportModel in agentExportModels)
        {
            if (!string.IsNullOrEmpty(agentExportModel.LlmType))
            {
                if (!int.TryParse(agentExportModel.LlmType, out _))
                {
                    var connection = connections.FirstOrDefault(c => c.Name == agentExportModel.LlmType);
                    agentExportModel.LlmType = connection?.ConnectionId.ToString() ?? "0";
                }
            }
            foreach (var agentExportModelContent in agentExportModel.Content)
            {
                if (!string.IsNullOrEmpty(agentExportModelContent.Value.Extension) && content.ContainsKey(agentExportModelContent.Value.Value))
                {
                    agentExportModelContent.Value.Value = content[agentExportModelContent.Value.Value];
                }
            }
        }

        var agents = await _agentsProcessor.List(workspaceId);
        var overridingAgents = agentExportModels
            .Where(agent => agents
                .Any(item => item.Name == agent.Name))
            .ToList();
        var agentsWithOldVersion = agentExportModels
            .Where(agent => agentVersions
                .FirstOrDefault(item => item.Key.ToString() == agent.Type).Value > agent.Version)
            .ToList();

        if(overridingAgents.Count == 0 && agentsWithOldVersion.Count == 0)
        {
            await ImportAgentsConfirmed(agentExportModels, workspaceId);
            return new ImportAgentsResultModel
            {
                IsSuccess = true
            };
        }
        var agentImportId = Guid.NewGuid().ToString();

        await _distributedCache.SetStringAsync(GetAgentImportCacheKey(agentImportId), agentExportModels.ToJson(), 
            new DistributedCacheEntryOptions {SlidingExpiration = new TimeSpan(0, 20,  0) });

        var confirmationMessages = new List<string>();
        if (overridingAgents.Count > 0)
        {
            confirmationMessages.Add($"The following agents are already present in the system:");
            foreach (var overridingAgent in overridingAgents)
                confirmationMessages.Add($"- {overridingAgent.Name} ({overridingAgent.Type})");
            confirmationMessages.Add("");
        }
        if (agentsWithOldVersion.Count > 0)
        {
            confirmationMessages.Add($"The following agents have older versions format:");
            foreach (var agentWithOldVersion in agentsWithOldVersion)
                confirmationMessages.Add($"- {agentWithOldVersion.Name} ({agentWithOldVersion.Type})");
            confirmationMessages.Add("");
        }
        confirmationMessages.Add($"Do you want to proceed?");
        return new ImportAgentsResultModel
        {
            IsSuccess = false,
            ConfirmationId = agentImportId,
            ConfirmationText = confirmationMessages,
        };
    }

    public async Task ConfirmImportAgents(string confirmationId, int workspaceId)
    {
        var importAgents = await _distributedCache.GetStringAsync(GetAgentImportCacheKey(confirmationId));
        if (string.IsNullOrEmpty(importAgents))
            throw new ArgumentException("ConfirmationId is not valid.", nameof(confirmationId));
        var importAgentsList = importAgents.JsonGet<List<AgentExportModel>>();
        await ImportAgentsConfirmed(importAgentsList, workspaceId);
    }

    private string GetAgentImportCacheKey(string confirmationId) => $"agentImport-{confirmationId}";

    private async Task ImportAgentsConfirmed(List<AgentExportModel> agentExportModels, int workspaceId)
    {
        var agentsToImportDictionary = _mapper.Map<List<AgentModel>>(agentExportModels)
            .ToDictionary(
                key => key.Name,
                value => new AgentModelProcessed
                {
                    AgentModel = value,
                    Processed = false
                });

        var callsLimit = MaxCallsLimit;
        while (agentsToImportDictionary.Any(x => !x.Value.Processed) && callsLimit > 0)
        {
            callsLimit--;
            var nonProcessedAgents = agentsToImportDictionary
                .Where(x => !x.Value.Processed)
                .Select(x => x.Value.AgentModel)
                .ToList();
            foreach (var agentModel in nonProcessedAgents)
            {
                await _agentLifecycleService.OnImportAsync(agentModel, agentsToImportDictionary);
                if (agentsToImportDictionary[agentModel.Name].Processed)
                {
                    await ImportAgentConfirmed(agentModel, workspaceId);
                }
            }
        }
        if (callsLimit <= 0)
        {
            _logger.LogError("Import agents process reached maximum calls limit. Some agents may not be imported.");
        }
        await SaveGit(workspaceId);
    }

    private string GeneratePastelColor()
    {
        var random = new Random();
        var r = random.Next(127, 255);
        var g = random.Next(127, 255);
        var b = random.Next(127, 255);

        return $"#{(r << 16 | g << 8 | b):X6}";
    }

    private async Task<List<TagModel>> HandleImportTags(AgentModel agentModel, List<TagModel> existingTags)
    {
        var newTags = new List<TagModel>();
        foreach (var agentTag in agentModel.Tags)
        {
            var tag = existingTags.FirstOrDefault(existingTag => existingTag.Name == agentTag.Name) ?? await _tagsProcessor.Set(new TagModel
            {
                TagId = 0,
                Name = agentTag.Name,
                Description = agentTag.Description,
                Created = DateTime.UtcNow,
                CreatedBy = _requestAccessor.Login ?? "",
                Color = GeneratePastelColor()
            });
            newTags.Add(tag);
        }
        return newTags;
    }

    private async Task ImportAgentConfirmed(AgentModel agentModel, int workspaceId)
    {
        var agent = await _agentsProcessor.GetByName(agentModel.Name, workspaceId);
        if (agentModel.Tags.Count > 0)
        {
            var existingTags = await _tagsProcessor.List();
            agentModel.Tags = await HandleImportTags(agentModel, existingTags);
        }
        if (agent == null)
        {
            await _agentsProcessor.Add(agentModel, workspaceId);
        }
        else
        {
            agentModel.AgentId = agent.AgentId;
            await _agentsProcessor.Update(agentModel);
        }
    }

    private async Task<string> EnsureClonedGitRepo()
    {
        var gitBranch = _extendedConfig.GitStorageBranch;
        var repoPath = GetRepoPath();
        await GitRepoLock.WaitAsync();
        try
        {
            if (!Repository.IsValid(repoPath))
            {
                if (Directory.Exists(repoPath))
                    Directory.Delete(repoPath, true);

                var co = new CloneOptions
                {
                    BranchName = gitBranch,
                    FetchOptions = {
                        CredentialsProvider = (_, _, _) =>
                            new UsernamePasswordCredentials
                            {
                                Username = _extendedConfig.GitStorageUsername,
                                Password = _extendedConfig.GitStoragePassword
                            }
                    }
                };
                Repository.Clone(_extendedConfig.GitStorageUrl, repoPath, co);
            }
            else
            {
                using (var repo = new Repository(repoPath))
                {
                    var remote = repo.Network.Remotes["origin"];
                    var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                    Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                    {
                        CredentialsProvider = (_, _, _) =>
                            new UsernamePasswordCredentials
                            {
                                Username = _extendedConfig.GitStorageUsername,
                                Password = _extendedConfig.GitStoragePassword
                            }
                    }, null);
                    EnsureBranchCheckedOut(repo, gitBranch);
                }
            }
        }
        finally
        {
            GitRepoLock.Release();
        }

        return repoPath;
    }

    private async Task SaveGit(int? workspaceId)
    {
        if (!_extendedConfig.UseGitStorage)
            return;
        try
        {
            var gitBranch = _extendedConfig.GitStorageBranch;
            var repoPath = await EnsureClonedGitRepo();
            var fileMap = await PrepareAgentExportFiles((await _agentsProcessor.List(workspaceId)).Select(a => a.AgentId).ToList());
            foreach (var kvp in fileMap)
            {
                var fullPath = Path.Combine(repoPath, _extendedConfig.GitStoragePath.Trim('/'), (workspaceId ?? 0).ToString(), kvp.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, kvp.Value);
            }
            using (var repo = new Repository(repoPath))
            {
                var localBranch = EnsureBranchCheckedOut(repo, gitBranch);
                Commands.Stage(repo, "*");
                var author = new Signature(_requestAccessor.Login, _requestAccessor.Login, DateTimeOffset.Now);
                repo.Commit($"Changes from {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} by {_requestAccessor.Login}", author, author);

                repo.Network.Push(localBranch, new PushOptions
                {
                    CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                    {
                        Username = _extendedConfig.GitStorageUsername,
                        Password = _extendedConfig.GitStoragePassword
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while saving to git repository");
        }
    }

    public async Task<List<string>> GetHistory(int agentId, string? parameterCode)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null || !_extendedConfig.UseGitStorage)
            return new();

        var gitBranch = _extendedConfig.GitStorageBranch;
        var repoPath = await EnsureClonedGitRepo();
        var content = string.IsNullOrEmpty(parameterCode) || !agent.Content.ContainsKey(parameterCode)
            ? agent.Content.FirstOrDefault(x => !string.IsNullOrEmpty(x.Value.Extension)).Value
            : agent.Content[parameterCode];
        if (content == null || string.IsNullOrEmpty(content.Extension))
            return new();
        var fileName = $"{agent.Name}-{content.Code}.{content.Extension}";
        var workspaceId = agent.WorkspaceId ?? 0;
        var relativePath = Path.Combine(_extendedConfig.GitStoragePath.Trim('/'), workspaceId.ToString(), fileName).Replace("\\", "/");

        var history = new List<string>();

        using (var repo = new Repository(repoPath))
        {
            EnsureBranchCheckedOut(repo, gitBranch);
            var commits = repo.Commits.QueryBy(new CommitFilter
            {
                SortBy = CommitSortStrategies.Time,
                FirstParentOnly = true
            });

            foreach (var commit in commits)
            {
                if (!commit.Parents.Any())
                    continue;

                var parent = commit.Parents.First();
                var changes = repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);

                if (changes.Any(change => change.Path == relativePath))
                {
                    history.Add(commit.MessageShort);
                }
            }
        }
        return history;
    }

    public async Task<string> GetHistoryCode(int agentId, string gitTitle, string? parameterCode)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null || !_extendedConfig.UseGitStorage)
            return string.Empty;

        var gitBranch = _extendedConfig.GitStorageBranch;
        var repoPath = await EnsureClonedGitRepo();
        var content = string.IsNullOrEmpty(parameterCode) || !agent.Content.ContainsKey(parameterCode)
            ? agent.Content.FirstOrDefault(x => !string.IsNullOrEmpty(x.Value.Extension)).Value
            : agent.Content[parameterCode];
        if (content == null || string.IsNullOrEmpty(content.Extension))
            return string.Empty;
        var fileName = $"{agent.Name}-{content.Code}.{content.Extension}";
        var workspaceId = agent.WorkspaceId ?? 0;
        var relativePath = Path.Combine(_extendedConfig.GitStoragePath.Trim('/'), workspaceId.ToString(), fileName).Replace("\\", "/");

        using (var repo = new Repository(repoPath))
        {
            EnsureBranchCheckedOut(repo, gitBranch);
            var commit = repo.Commits
                .QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Time, FirstParentOnly = true })
                .FirstOrDefault(c => c.MessageShort == gitTitle);

            if (commit?[relativePath]?.Target is not Blob blob)
                return string.Empty;

            using (var stream = blob.GetContentStream())
            using (var reader = new StreamReader(stream))
                return await reader.ReadToEndAsync();
        }
    }

    private Branch EnsureBranchCheckedOut(Repository repo, string gitBranch)
    {
        var localBranch = repo.Branches[gitBranch];
        if (localBranch == null)
        {
            var remoteBranch = repo.Branches["origin/" + gitBranch];
            if (remoteBranch == null)
                throw new Exception($"Branch '{gitBranch}' not found in remote.");

            localBranch = repo.CreateBranch(gitBranch, remoteBranch.Tip);
            repo.Branches.Update(localBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
        }

        Commands.Checkout(repo, localBranch);
        return localBranch;
    }

    public async Task<List<McpActionViewModel>> GetMcpActions(string connectionName)
    {
        var connection = string.IsNullOrEmpty(connectionName) 
            ? null 
            : (await _connectionProcessor.List(_requestAccessor.WorkspaceId)).FirstOrDefault(c => c.Name == connectionName);
        if (connection == null)
            return new List<McpActionViewModel>();

        var serverUrl = connection.Content["serverUrl"].TrimEnd('/');
        var customHeader = connection.Content.ContainsKey("customHeader") ? connection.Content["customHeader"] : "";

        var result = await _mcpClient.GetActions(serverUrl, customHeader);
        return result;
    }

    public async Task<string> GetCard(int agentId)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null)
            return "<div>Agent not found</div>";
        var agentsDescription = await _agentsFlowDescriber.GetAgentsDescription(agent.WorkspaceId ?? 0, agentId);

        var parameters = await GetParameters(agentId);
        var parametersHtml = parameters != null && parameters.Any() 
            ? string.Join("", parameters.Select(p => $"<li>{p.Description}</li>"))
            : "<li>No parameters defined</li>";

        var outputDescription = agent.Content.ContainsKey("outputDescription") 
            ? agent.Content["outputDescription"].Value 
            : "No output description available";

        var html = $@"
<div>
    <h2>{agent.Name}</h2>
    
    <div>
        <h3>Description</h3>
        <p>{agent.Description}</p>
    </div>
    
    <div>
        <h3>Parameters</h3>
        <ul>
            {parametersHtml}
        </ul>
    </div>
    <div>
        <h3>Agent Overview</h3>
        <p>{agentsDescription.Description}</p>
    </div>
    {(
        agentsDescription.CalledByAgents.Count > 0 
        ? $@"<div>
                <h3>Called By</h3>
                <ul>
                    {string.Join("", agentsDescription.CalledByAgents.Select(a => $"<li>{a}</li>"))}
                </ul>
            </div>"
        : ""
    )}
    {(
        agentsDescription.CallingAgents.Count > 0
        ? $@"<div>
                <h3>Calls</h3>
                <ul>
                    {string.Join("", agentsDescription.CallingAgents.Select(a => $"<li>{a}</li>"))}
                </ul>
            </div>"
        : ""
    )}    
    <div>
        <h3>Output</h3>
        <p>{outputDescription}</p>
    </div>
</div>";

        return html;
    }
}

public interface IAgentsService
{
    Task<List<AgentViewModel>> ListAgents(int? workspaceId);
    Task<AgentViewModel> AddAgent(AgentViewModel agentViewModel, int workspaceId);
    Task<AgentViewModel> UpdateAgent(AgentViewModel agentViewModel);
    Task DeleteAgent(int agentId);
    Task DeleteAgentsInWorkspace(int workspaceId);
    Task<List<ParameterModel>?> GetParameters(int agentId); 
    Task SwitchEnableAgent(int agentId, bool isEnabled);
    Task<bool> IsAgentEnabled(string agentName);
    Task<byte[]> ExportAgents(List<int> agentIdsList);
    Task<ImportAgentsResultModel> ImportAgents(IFormFile file, Dictionary<Models.ViewModels.AgentType, int> agentVersions, int workspaceId);
    Task ConfirmImportAgents(string confirmationId, int workspaceId);
    Task<List<string>> GetHistory(int agentId, string? parameterCode);
    Task<string> GetHistoryCode(int agentId, string gitTitle, string? parameterCode);
    Task<List<McpActionViewModel>> GetMcpActions(string connectionName);
    Task<string> GetCard(int agentId);
}