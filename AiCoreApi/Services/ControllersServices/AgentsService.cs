using System.IO.Compression;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.SemanticKernel.Agents;
using AutoMapper;
using AiCoreApi.Common.Extensions;
using static AiCoreApi.SemanticKernel.Agents.CompositeAgent;
using Microsoft.Extensions.Caching.Distributed;
using AiCoreApi.Common;
using LibGit2Sharp;

namespace AiCoreApi.Services.ControllersServices;

public class AgentsService : IAgentsService
{
    private readonly IMapper _mapper;
    private readonly ExtendedConfig _extendedConfig;
    private readonly IAgentsProcessor _agentsProcessor;
    private readonly IDistributedCache _distributedCache;
    private readonly ILoginProcessor _loginProcessor;
    private readonly RequestAccessor _requestAccessor;

    public AgentsService(
        ExtendedConfig extendedConfig,
        IAgentsProcessor agentsProcessor, 
        IMapper mapper,
        IDistributedCache distributedCache,
        ILoginProcessor loginProcessor,
        RequestAccessor requestAccessor)
    {
        _extendedConfig = extendedConfig;
        _agentsProcessor = agentsProcessor;
        _mapper = mapper;
        _distributedCache = distributedCache;
        _loginProcessor = loginProcessor;
        _requestAccessor = requestAccessor;
    }

    private static readonly SemaphoreSlim GitRepoLock = new(1, 1);
    private string GetRepoPath() => Path.Combine(Path.GetTempPath(), $"git-cache-{_extendedConfig.GitStoragePath.GetHashCode()}");

    public async Task<AgentViewModel> AddAgent(AgentViewModel agentViewModel)
    {
        if (agentViewModel.AgentId != 0)
            throw new ArgumentException("Value should be 0.", nameof(AgentViewModel.AgentId));

        var agentModel = _mapper.Map<AgentModel>(agentViewModel);
        var savedModel = await _agentsProcessor.Add(agentModel);
        var result = _mapper.Map<AgentViewModel>(savedModel);
        await SaveGit();
        return result;
    }

    public async Task<AgentViewModel> UpdateAgent(AgentViewModel agentViewModel)
    {
        if (agentViewModel.AgentId == 0)
            throw new ArgumentException("Value should be not 0.", nameof(AgentViewModel.AgentId));

        var agentModel = _mapper.Map<AgentModel>(agentViewModel);
        var savedModel = await _agentsProcessor.Update(agentModel);
        var result = _mapper.Map<AgentViewModel>(savedModel);
        await SaveGit();
        return result;
    }

    public async Task<List<AgentViewModel>> ListAgents()
    {
        var agents = await _agentsProcessor.List();
        var agentsViewModelList = _mapper.Map<List<AgentViewModel>>(agents);
        return agentsViewModelList.OrderBy(agent => agent.AgentId).ToList();
    }

    public async Task DeleteAgent(int agentId)
    {
        await _agentsProcessor.Delete(agentId);
        await SaveGit();
    }

    public async Task<List<ParameterModel>?> GetParameters(int agentId)
    {
        var agentViewModel = (await ListAgents()).FirstOrDefault(agent => agent.AgentId == agentId);
        if (agentViewModel == null)
            return null;        
        if(!agentViewModel.Content.ContainsKey("parameterDescription") || string.IsNullOrEmpty(agentViewModel.Content["parameterDescription"].Value))
            return null;
        var parameterId = 1;
        var result = agentViewModel.Content["parameterDescription"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(parameter => new ParameterModel
        {
            Name = $"parameter{parameterId++}",
            Description = parameter
        }).ToList();
        return result;

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
        await SaveGit();
    }

    public async Task<bool> IsAgentEnabled(string agentName)
    {
        var agents = await _agentsProcessor.List();
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
        var agents = await _agentsProcessor.List();

        var agentsToExport = agents
            .Where(agent => agentIdsList.Contains(agent.AgentId) && agent.Type != Models.DbModels.AgentType.Composite)
            .ToList();

        var compositeAgents = agents
            .Where(agent => agentIdsList.Contains(agent.AgentId) && agent.Type == Models.DbModels.AgentType.Composite)
            .ToList();

        await HandleCompositeAgents(agentsToExport, agents, compositeAgents);

        var agentsToExportResult = _mapper.Map<List<AgentExportModel>>(agentsToExport);
        var fileMap = new Dictionary<string, string>();

        foreach (var agent in agentsToExportResult)
        {
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


    private async Task HandleCompositeAgents(List<AgentModel> agentsToExport, List<AgentModel> allAgents, List<AgentModel> compositeAgents)
    {
        foreach (var compositeAgent in compositeAgents)
        {
            var subAgentIds = compositeAgent.Content[CompositeAgent.AgentContentParameters.AgentsList]
                .Value.JsonGet<Dictionary<string, bool>>()!
                .Select(item => item.Key).ToList();

            var subAgentsNames = subAgentIds.Select(subAgentId => allAgents.FirstOrDefault(agent => agent.AgentId.ToString() == subAgentId)?.Name).ToList();
            compositeAgent.Content[AgentContentParameters.AgentsList].Value = subAgentsNames.ToJson();
            agentsToExport.Add(compositeAgent);

            // Add Agents to export list if they are not composite
            var subAgentsToExport = allAgents.Where(agent => 
                    subAgentIds.Contains(agent.AgentId.ToString()) 
                    && agent.Type != Models.DbModels.AgentType.Composite
                    && !agentsToExport.Select(item => item.AgentId).Contains(agent.AgentId)
                )
                .ToList();
            agentsToExport.AddRange(subAgentsToExport);

            // Handle Composite Agents
            var subCompositeSubAgents = allAgents.Where(agent => 
                    subAgentIds.Contains(agent.AgentId.ToString()) 
                    && agent.Type == Models.DbModels.AgentType.Composite
                    && !agentsToExport.Select(item => item.AgentId).Contains(agent.AgentId)
                ).ToList();

            await HandleCompositeAgents(agentsToExport, allAgents, subCompositeSubAgents);
        }
    }

    public async Task<ImportAgentsResultModel> ImportAgents(IFormFile file, Dictionary<Models.ViewModels.AgentType, int> agentVersions)
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
        foreach (var agentExportModel in agentExportModels)
        {
            foreach (var agentExportModelContent in agentExportModel.Content)
            {
                if (!string.IsNullOrEmpty(agentExportModelContent.Value.Extension) && content.ContainsKey(agentExportModelContent.Value.Value))
                {
                    agentExportModelContent.Value.Value = content[agentExportModelContent.Value.Value];
                }
            }
        }

        var agents = await _agentsProcessor.List();
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
            await ImportAgentsConfirmed(agentExportModels);
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

    public async Task ConfirmImportAgents(string confirmationId)
    {
        var importAgents = await _distributedCache.GetStringAsync(GetAgentImportCacheKey(confirmationId));
        if (string.IsNullOrEmpty(importAgents))
            throw new ArgumentException("ConfirmationId is not valid.", nameof(confirmationId));
        var importAgentsList = importAgents.JsonGet<List<AgentExportModel>>();
        await ImportAgentsConfirmed(importAgentsList);
    }

    private string GetAgentImportCacheKey(string confirmationId) => $"agentImport-{confirmationId}";

    private async Task ImportAgentsConfirmed(List<AgentExportModel> agentExportModels)
    {
        var agents = (await _agentsProcessor.List()).Select(agent => agent.Name).ToList();
        var nonCompositeAgents = agentExportModels.Where(agent => agent.Type != Models.ViewModels.AgentType.Composite.ToString()).ToList();
        var compositeAgents = agentExportModels.Where(agent => agent.Type == Models.ViewModels.AgentType.Composite.ToString()).ToList();
        foreach (var agent in nonCompositeAgents)
        {
            await ImportAgentConfirmed(agent);
            agents.Add(agent.Name);
        }
        var lastLoopWasNonEmpty = true;
        while(compositeAgents.Count > 0 && lastLoopWasNonEmpty)
        {
            lastLoopWasNonEmpty = false;
            foreach (var agent in compositeAgents.ToList())
            {
                var subAgentNames = agent.Content[AgentContentParameters.AgentsList].Value.JsonGet<List<string>>();
                // Check if all subagents are present in the system
                if (subAgentNames.Any(subAgent => !agents.Contains(subAgent)))
                    continue;

                var subAgentsIds = (await _agentsProcessor.List())
                    .Where(subAgent => subAgentNames.Contains(subAgent.Name))
                    .Select(subAgent => subAgent.AgentId)
                    .Distinct()
                    .ToDictionary(key => key, value => true);
                agent.Content[AgentContentParameters.AgentsList].Value = subAgentsIds.ToJson();

                await ImportAgentConfirmed(agent);
                agents.Add(agent.Name);
                compositeAgents.Remove(agent);
                lastLoopWasNonEmpty = true;
            }
        }
        await SaveGit();
    }

    private async Task ImportAgentConfirmed(AgentExportModel agentExportModel)
    {
        var agentModel = _mapper.Map<AgentModel>(agentExportModel);
        var agent = await _agentsProcessor.GetByName(agentExportModel.Name);
        if (agent == null)
        {
            await _agentsProcessor.Add(agentModel);
        }
        else
        {
            agentModel.AgentId = agent.AgentId;
            await _agentsProcessor.Update(agentModel);
        }
    }

    private async Task<string> EnsureClonedGitRepo()
    {
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
                using var repo = new Repository(repoPath);
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
            }
        }
        finally
        {
            GitRepoLock.Release();
        }

        return repoPath;
    }

    private async Task SaveGit()
    {
        if (!_extendedConfig.UseGitStorage)
            return;
        var repoPath = await EnsureClonedGitRepo();
        var fileMap = await PrepareAgentExportFiles((await _agentsProcessor.List()).Select(a => a.AgentId).ToList());
        foreach (var kvp in fileMap)
        {
            var fullPath = Path.Combine(repoPath, _extendedConfig.GitStoragePath.Trim('/'), kvp.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, kvp.Value);
        }
        using (var repo = new Repository(repoPath))
        {
            Commands.Stage(repo, "*");
            var author = new Signature(_requestAccessor.Login, _requestAccessor.Login, DateTimeOffset.Now);
            repo.Commit($"Changes from {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} by {_requestAccessor.Login}", author, author);

            repo.Network.Push(repo.Head, new PushOptions
            {
                CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                {
                    Username = _extendedConfig.GitStorageUsername,
                    Password = _extendedConfig.GitStoragePassword
                }
            });
        }
    }

    public async Task<List<string>> GetHistory(int agentId)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null || !_extendedConfig.UseGitStorage)
            return new();

        var repoPath = await EnsureClonedGitRepo();
        var content = agent.Content.First();
        var fileName = $"{agent.Name}-{content.Value.Code}.{content.Value.Extension}";
        var relativePath = Path.Combine(_extendedConfig.GitStoragePath.Trim('/'), fileName).Replace("\\", "/");

        var history = new List<string>();
        using (var repo = new Repository(repoPath))
        {
            var commits = repo.Commits.QueryBy(new CommitFilter
            {
                SortBy = CommitSortStrategies.Time,
                FirstParentOnly = true
            });
            foreach (var commit in commits)
            {
                if (commit[relativePath] != null)
                    history.Add(commit.MessageShort);
            }
            return history;
        }
    }

    public async Task<string> GetHistoryCode(int agentId, string gitTitle)
    {
        var agent = await _agentsProcessor.GetById(agentId);
        if (agent == null || !_extendedConfig.UseGitStorage)
            return string.Empty;

        var repoPath = await EnsureClonedGitRepo();
        var content = agent.Content.First();
        var fileName = $"{agent.Name}-{content.Value.Code}.{content.Value.Extension}";
        var relativePath = Path.Combine(_extendedConfig.GitStoragePath.Trim('/'), fileName).Replace("\\", "/");

        using (var repo = new Repository(repoPath))
        {
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
}

public interface IAgentsService
{
    Task<List<AgentViewModel>> ListAgents();
    Task<AgentViewModel> AddAgent(AgentViewModel agentViewModel);
    Task<AgentViewModel> UpdateAgent(AgentViewModel agentViewModel);
    Task DeleteAgent(int agentId); 
    Task<List<ParameterModel>?> GetParameters(int agentId); 
    Task SwitchEnableAgent(int agentId, bool isEnabled);
    Task<bool> IsAgentEnabled(string agentName);
    Task<byte[]> ExportAgents(List<int> agentIdsList);
    Task<ImportAgentsResultModel> ImportAgents(IFormFile file, Dictionary<Models.ViewModels.AgentType, int> agentVersions);
    Task ConfirmImportAgents(string confirmationId);
    Task<List<string>> GetHistory(int agentId);
    Task<string> GetHistoryCode(int agentId, string gitTitle);
}