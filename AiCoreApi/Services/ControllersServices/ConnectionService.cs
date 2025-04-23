using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;

namespace AiCoreApi.Services.ControllersServices;

public class ConnectionService : IConnectionService
{
    private readonly IIngestionProcessor _ingestionProcessor;
    private readonly IConnectionProcessor _connectionProcessor;
    private readonly IMapper _mapper;

    public ConnectionService(
        IIngestionProcessor ingestionProcessor,
        IConnectionProcessor connectionProcessor,
        IMapper mapper)
    {
        _ingestionProcessor = ingestionProcessor;
        _connectionProcessor = connectionProcessor;
        _mapper = mapper;
    }

    public async Task<List<ConnectionViewModel>> ListConnections(int workspaceId)
    {
        var connections = await _connectionProcessor.List(workspaceId);
        var viewModels = _mapper.Map<List<ConnectionViewModel>>(connections);
        var activeConnectionIds = await _ingestionProcessor.GetActiveConnectionIds(workspaceId);
        viewModels.ForEach(e => e.CanBeDeleted = !activeConnectionIds.Contains(e.ConnectionId));
        return viewModels;
    }

    public async Task<ConnectionViewModel> AddConnection(ConnectionViewModel connectionViewModel, string initiator, int workspaceId)
    {
        if (connectionViewModel.ConnectionId != 0)
        {
            throw new ArgumentException("Value should be 0.", nameof(ConnectionViewModel.ConnectionId));
        }
        connectionViewModel.CreatedBy = initiator;
        var connectionModel = _mapper.Map<ConnectionModel>(connectionViewModel);
        var savedModel = await _connectionProcessor.Set(connectionModel, workspaceId);
        var result = _mapper.Map<ConnectionViewModel>(savedModel);
        return result;
    }

    public async Task<ConnectionViewModel> UpdateConnection(ConnectionViewModel connectionViewModel)
    {
        if (connectionViewModel.ConnectionId == 0)
        {
            throw new ArgumentException("Value should be not 0.", nameof(ConnectionViewModel.ConnectionId));
        }
        var connectionModel = _mapper.Map<ConnectionModel>(connectionViewModel);
        var savedModel = await _connectionProcessor.Set(connectionModel, null);
        var result = _mapper.Map<ConnectionViewModel>(savedModel);
        return result;
    }

    public async Task DeleteConnection(int connectionId)
    {
        var activeConnectionIds = await _ingestionProcessor.GetActiveConnectionIds(null);
        if (activeConnectionIds.Contains(connectionId))
            return;
        await _connectionProcessor.Remove(connectionId);
    }

    public async Task<ConnectionViewModel?> GetConnectionById(int connectionId)
    {
        var connection = await _connectionProcessor.GetById(connectionId);
        var viewModel = _mapper.Map<ConnectionViewModel>(connection);
        return viewModel;
    }
}

public interface IConnectionService
{
    Task<List<ConnectionViewModel>> ListConnections(int workspaceId);
    Task<ConnectionViewModel> AddConnection(ConnectionViewModel connectionViewModel, string initiator, int workspaceId);
    Task<ConnectionViewModel> UpdateConnection(ConnectionViewModel connectionViewModel);
    Task DeleteConnection(int connectionId);
    Task<ConnectionViewModel?> GetConnectionById(int connectionId);
}
