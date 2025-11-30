using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Services.IngestionServices;
using AutoMapper;
using Microsoft.Extensions.Caching.Distributed;
using System.IO.Compression;

namespace AiCoreApi.Services.ControllersServices;

public class IngestionService : IIngestionService
{
    private readonly RequestAccessor _requestAccessor;
    private readonly IIngestionProcessor _ingestionProcessor;
    private readonly IConnectionProcessor _connectionProcessor;
    private readonly ITagsProcessor _tagsProcessor;
    private readonly ITaskProcessor _taskProcessor;
    private readonly IDataIngestionService _dataIngestionService;
    private readonly IMapper _mapper;
    private readonly IDistributedCache _distributedCache;

    public IngestionService(
        RequestAccessor requestAccessor,
        IIngestionProcessor ingestionProcessor,
        IConnectionProcessor connectionProcessor,
        ITagsProcessor tagsProcessor,
        ITaskProcessor taskService,
        IDataIngestionService dataIngestionService,
        IMapper mapper,
        IDistributedCache distributedCache
        )
    {
        _requestAccessor = requestAccessor;
        _ingestionProcessor = ingestionProcessor;
        _connectionProcessor = connectionProcessor;
        _tagsProcessor = tagsProcessor;
        _taskProcessor = taskService;
        _dataIngestionService = dataIngestionService;
        _mapper = mapper;
        _distributedCache = distributedCache;
    }

    public async Task<IngestionViewModel?> GetIngestionById(int ingestionId)
    {
        var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId, excludeFile:true);
        var viewModel = _mapper.Map<IngestionViewModel>(ingestion);
        viewModel.Status = await GetIngestionStatus(ingestionId);
        return viewModel;
    }

    public async Task<IngestionViewModel> AddIngestion(IngestionViewModel ingestionViewModel, string initiator, int workspaceId)
    {
        if (ingestionViewModel.IngestionId != 0)
        {
            throw new ArgumentException("Value should be 0.", nameof(IngestionViewModel.IngestionId));
        }

        ingestionViewModel.CreatedBy = initiator;
        var ingestionModel = _mapper.Map<IngestionModel>(ingestionViewModel);
        var savedModel = await _ingestionProcessor.Set(ingestionModel, workspaceId);
        await SyncIngestion(savedModel.IngestionId, initiator);
        var result = _mapper.Map<IngestionViewModel>(savedModel);
        return result;
    }

    public async Task<IngestionViewModel> UpdateIngestion(IngestionViewModel ingestionViewModel, string initiator)
    {
        if (ingestionViewModel.IngestionId == 0)
        {
            throw new ArgumentException("Value should be not 0.", nameof(IngestionViewModel.IngestionId));
        }

        var ingestionModel = _mapper.Map<IngestionModel>(ingestionViewModel);
        var savedModel = await _ingestionProcessor.Set(ingestionModel, null);
        await HandleUpdate(savedModel.IngestionId, initiator);
        var result = _mapper.Map<IngestionViewModel>(savedModel);
        return result;
    }

    public async Task<List<IngestionViewModel>> ListIngestions(int workspaceId)
    {
        var ingestions = await _ingestionProcessor.List(workspaceId);
        var tasksFailed = await _taskProcessor
            .LastTaskList(ingestions.Select(e => e.IngestionId)
                .Distinct().ToList());
        var viewModels = _mapper.Map<List<IngestionViewModel>>(ingestions);

        foreach (var v in viewModels)
        {
            v.Status = await GetIngestionStatus(v.IngestionId);
            var lastTask = tasksFailed.SingleOrDefault(e => e.IngestionId == v.IngestionId);
            if(lastTask is { State: TaskState.Failed })
            {
                v.IsLastSyncFailed = true;
                v.LastSyncFailedMessage = lastTask.ErrorMessage;
            }
        }

        return viewModels;
    }

    public async Task<List<IngestionTaskViewModel>> ListIngestionTasks(int workspaceId)
    {
        var taskWithIngestions = await _taskProcessor.ListWithIngestion(workspaceId);
        var viewModels = _mapper.Map<List<IngestionTaskViewModel>>(taskWithIngestions);
        return viewModels;
    }

    public async Task<List<string>> GetAutoComplete(string parameterName, IngestionViewModel ingestionViewModel)
    {
        var ingestionModel = _mapper.Map<IngestionModel>(ingestionViewModel);
        return await _dataIngestionService.GetAutoComplete(parameterName, ingestionModel);
    }

    public async Task SyncIngestion(int ingestionId, string initiator)
    {
        var task = new TaskModel
        {
            IngestionId = ingestionId,
            Ingestion = null,
            Type = TaskType.DataSync,
            CreatedBy = initiator,
            IsRetriable = true,
        };
        await _taskProcessor.ScheduleTask(task);
    }
    
    public async Task DeleteIngestion(int ingestionId, string initiator)
    {
        var task = new TaskModel
        {
            IngestionId = ingestionId,
            Type = TaskType.Remove,
            CreatedBy = initiator,
            IsRetriable = true,
        };
        await _taskProcessor.ScheduleTask(task);
    }

    private async Task HandleUpdate(int ingestionId, string initiator)
    {
        await SyncIngestion(ingestionId, initiator);
        var task = new TaskModel
        {
            IngestionId = ingestionId,
            Type = TaskType.TagSync,
            CreatedBy = initiator,
            IsRetriable = true,
        };
        await _taskProcessor.ScheduleTask(task);
    }

    private async Task<IngestionStatus> GetIngestionStatus(int ingestionId)
    {
        var tasks = await _taskProcessor.GetByIngestion(ingestionId);

        var active = tasks.FirstOrDefault(t => t.State == TaskState.InProgress);
        if (active != null)
            return active.Type switch
            {
                TaskType.Remove => IngestionStatus.Removing,
                _ => IngestionStatus.Syncing,
            };

        var pending = tasks.FirstOrDefault(t => t.State == TaskState.New);
        if (pending != null)
            return pending.Type switch
            {
                TaskType.Remove => IngestionStatus.PendingRemove,
                _ => IngestionStatus.PendingSync,
            };

        return IngestionStatus.Ready;
    }

    public async Task<byte[]> ExportIngestions(List<int> ingestionIdsList)
    {
        var fileMap = await PrepareConnectionExportFiles(ingestionIdsList);
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

    private async Task<Dictionary<string, string>> PrepareConnectionExportFiles(List<int> ingestionIdsList)
    {
        var connections = await _connectionProcessor.List(null);
        var ingestions = await _ingestionProcessor.List(null);
        var ingestionsToExport = ingestions
            .Where(ingestion => ingestionIdsList.Contains(ingestion.IngestionId))
            .ToList();

        var fileMap = new Dictionary<string, string>();
        foreach (var ingestion in ingestionsToExport)
        {
            var ingestionExport = _mapper.Map<IngestionExportModel>(ingestion);
            foreach (var content in ingestionExport.Content.ToList())
            {
                if ((content.Key.EndsWith("Connection") || content.Key.EndsWith("ConnectionName")) && int.TryParse(content.Value, out var connectionId))
                {
                    var connection = connections.FirstOrDefault(c => c.ConnectionId == connectionId);
                    if (connection != null)
                    {
                        ingestionExport.Content[content.Key] = connection.Name;
                    }
                }
            }
            var fileName = $"{ingestion.Name.Replace(" ", "_")}.json";
            fileMap[fileName] = ingestionExport.ToJson(true);
        }
        return fileMap;
    }

    public async Task<ImportIngestionsResultModel> ImportIngestions(IFormFile file, int workspaceId)
    {
        var ingestionExportModels = new List<IngestionExportModel>();
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
                        var ingestion = fileContent.JsonGet<IngestionExportModel>();
                        if (ingestion != null)
                            ingestionExportModels.Add(ingestion);
                    }
                }
            }
        }

        var existingIngestions = await _ingestionProcessor.List(workspaceId);
        var overridingIngestions = ingestionExportModels
            .Where(ingestion => existingIngestions
                .Any(item => item.Name == ingestion.Name))
            .ToList();

        if (overridingIngestions.Count == 0)
        {
            await ImportIngestionsConfirmed(ingestionExportModels, workspaceId);
            return new ImportIngestionsResultModel
            {
                IsSuccess = true
            };
        }

        var ingestionImportId = Guid.NewGuid().ToString();
        await _distributedCache.SetStringAsync(GetConnectionImportCacheKey(ingestionImportId), ingestionExportModels.ToJson(),
            new DistributedCacheEntryOptions { SlidingExpiration = new TimeSpan(0, 20, 0) });

        var confirmationMessages = new List<string> { $"The following ingestions are already present in the system:" };
        foreach (var overridingIngestion in overridingIngestions)
            confirmationMessages.Add($"- {overridingIngestion.Name} ({overridingIngestion.Type})");
        confirmationMessages.Add("");
        confirmationMessages.Add($"Do you want to proceed?");

        return new ImportIngestionsResultModel
        {
            IsSuccess = false,
            ConfirmationId = ingestionImportId,
            ConfirmationText = confirmationMessages,
        };
    }

    public async Task ConfirmImportIngestions(string confirmationId, int workspaceId)
    {
        var importIngestions = await _distributedCache.GetStringAsync(GetConnectionImportCacheKey(confirmationId));
        if (string.IsNullOrEmpty(importIngestions)) 
            throw new ArgumentException("ConfirmationId is not valid.", nameof(confirmationId));
        var importIngestionsList = importIngestions.JsonGet<List<IngestionExportModel>>();
        await ImportIngestionsConfirmed(importIngestionsList, workspaceId);
    }

    private string GetConnectionImportCacheKey(string confirmationId) => $"ingestionImport-{confirmationId}";

    private async Task ImportIngestionsConfirmed(List<IngestionExportModel> ingestionExportModels, int workspaceId)
    {
        var workspaceConnections = await _connectionProcessor.List(workspaceId);
        var tags = await _tagsProcessor.List();

        foreach (var ingestionExport in ingestionExportModels)
        {
            var ingestionModel = _mapper.Map<IngestionModel>(ingestionExport);
            ingestionModel.WorkspaceId = workspaceId;
            ResolveConnectionReferences(ingestionModel, workspaceConnections);
            ResolveTagsReferences(ingestionModel, tags);
            var existing = await _ingestionProcessor.Get(ingestionExport.Name, workspaceId);
            if (existing != null)
            {
                ingestionModel.IngestionId = existing.IngestionId;
                ingestionModel.Created = existing.Created;
                ingestionModel.CreatedBy = existing.CreatedBy;
                ingestionModel.WorkspaceId = workspaceId;
            }
            else
            {
                ingestionModel.IngestionId = 0;
                ingestionModel.Created = DateTime.UtcNow;
                ingestionModel.CreatedBy = ingestionExport.CreatedBy ?? _requestAccessor.Login ?? "import";
            }

            await _ingestionProcessor.Set(ingestionModel, workspaceId);
        }
    }

    private void ResolveTagsReferences(IngestionModel ingestion, List<TagModel> allTags)
    {
        foreach (var tag in ingestion.Tags)
        {
            var existingTag = allTags.FirstOrDefault(t => t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase));
            if (existingTag != null)
            {
                tag.TagId = existingTag.TagId;
            }
        }
    }

    private void ResolveConnectionReferences(IngestionModel ingestion, List<ConnectionModel> allConnections)
    {
        foreach (var key in ingestion.Content.Keys.ToList())
        {
            if (key.EndsWith("Connection") || key.EndsWith("ConnectionName"))
            {
                var value = ingestion.Content[key];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var conn = allConnections.FirstOrDefault(
                    c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase));

                if (conn != null)
                    ingestion.Content[key] = conn.ConnectionId.ToString();
            }
        }
    }
}

public interface IIngestionService
{
    Task<IngestionViewModel?> GetIngestionById(int ingestionId);
    Task<IngestionViewModel> AddIngestion(IngestionViewModel ingestionViewModel, string initiator, int workspaceId);
    Task<IngestionViewModel> UpdateIngestion(IngestionViewModel ingestionViewModel, string initiator);
    Task<List<IngestionViewModel>> ListIngestions(int workspaceId);
    Task SyncIngestion(int ingestionId, string initiator);
    Task DeleteIngestion(int ingestionId, string initiator);
    Task<List<IngestionTaskViewModel>> ListIngestionTasks(int workspaceId);
    Task<List<string>> GetAutoComplete(string parameterName, IngestionViewModel ingestionViewModel);
    Task<byte[]> ExportIngestions(List<int> ingestionIdsList);
    Task<ImportIngestionsResultModel> ImportIngestions(IFormFile file, int workspaceId);
    Task ConfirmImportIngestions(string confirmationId, int workspaceId);
}
