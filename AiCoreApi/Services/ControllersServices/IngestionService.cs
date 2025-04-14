using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;
using AiCoreApi.Services.IngestionServices;

namespace AiCoreApi.Services.ControllersServices;

public class IngestionService : IIngestionService
{
    private readonly IIngestionProcessor _ingestionProcessor;
    private readonly ITaskProcessor _taskProcessor;
    private readonly IDataIngestionService _dataIngestionService;
    private readonly IMapper _mapper;

    public IngestionService(
        IIngestionProcessor ingestionProcessor,
        ITaskProcessor taskService,
        IDataIngestionService dataIngestionService,
        IMapper mapper)
    {
        _ingestionProcessor = ingestionProcessor;
        _taskProcessor = taskService;
        _dataIngestionService = dataIngestionService;
        _mapper = mapper;
    }

    public async Task<IngestionViewModel?> GetIngestionById(int ingestionId)
    {
        var ingestion = await _ingestionProcessor.GetIngestionById(ingestionId, excludeFile:true);
        var viewModel = _mapper.Map<IngestionViewModel>(ingestion);
        viewModel.Status = GetIngestionStatus(ingestionId);
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

        viewModels.ForEach(v => {
            v.Status = GetIngestionStatus(v.IngestionId);
            var lastTask = tasksFailed.SingleOrDefault(e => e.IngestionId == v.IngestionId);
            if(lastTask != null)
            {
                if (lastTask.State == TaskState.Failed)
                {
                    v.IsLastSyncFailed = true;
                    v.LastSyncFailedMessage = lastTask.ErrorMessage;
                }
            }
        });
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

    private IngestionStatus GetIngestionStatus(int ingestionId)
    {
        var tasks =
            _taskProcessor.GetByIngestion(ingestionId);

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
}
