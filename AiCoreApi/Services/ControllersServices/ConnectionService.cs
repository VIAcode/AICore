using System.IO.Compression;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;
using AiCoreApi.Common.Extensions;
using Microsoft.Extensions.Caching.Distributed;

namespace AiCoreApi.Services.ControllersServices;

public class ConnectionService : IConnectionService
{
    private readonly IIngestionProcessor _ingestionProcessor;
    private readonly IConnectionProcessor _connectionProcessor;
    private readonly IMapper _mapper;
    private readonly IDistributedCache _distributedCache;

    public ConnectionService(
        IIngestionProcessor ingestionProcessor,
        IConnectionProcessor connectionProcessor,
        IMapper mapper,
        IDistributedCache distributedCache)
    {
        _ingestionProcessor = ingestionProcessor;
        _connectionProcessor = connectionProcessor;
        _mapper = mapper;
        _distributedCache = distributedCache;
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

    public async Task<byte[]> ExportConnections(List<int> connectionIdsList)
    {
        var fileMap = await PrepareConnectionExportFiles(connectionIdsList);
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

    private async Task<Dictionary<string, string>> PrepareConnectionExportFiles(List<int> connectionIdsList)
    {
        var connections = await _connectionProcessor.ListAll();
        var connectionsToExport = connections
            .Where(connection => connectionIdsList.Contains(connection.ConnectionId))
            .ToList();

        var fileMap = new Dictionary<string, string>();
        foreach (var connection in connectionsToExport)
        {
            var connectionExport = _mapper.Map<ConnectionExportModel>(connection);
            var fileName = $"{connection.Name.Replace(" ", "_")}.json";
            fileMap[fileName] = connectionExport.ToJson();
        }
        return fileMap;
    }

    public async Task<ImportConnectionsResultModel> ImportConnections(IFormFile file, int workspaceId)
    {
        var connectionExportModels = new List<ConnectionExportModel>();
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
                        var connection = fileContent.JsonGet<ConnectionExportModel>();
                        if (connection != null)
                            connectionExportModels.Add(connection);
                    }
                }
            }
        }

        var existingConnections = await _connectionProcessor.List(workspaceId);
        var overridingConnections = connectionExportModels
            .Where(connection => existingConnections
                .Any(item => item.Name == connection.Name))
            .ToList();

        if (overridingConnections.Count == 0)
        {
            await ImportConnectionsConfirmed(connectionExportModels, workspaceId);
            return new ImportConnectionsResultModel
            {
                IsSuccess = true
            };
        }

        var connectionImportId = Guid.NewGuid().ToString();
        await _distributedCache.SetStringAsync(GetConnectionImportCacheKey(connectionImportId), connectionExportModels.ToJson(), 
            new DistributedCacheEntryOptions { SlidingExpiration = new TimeSpan(0, 20, 0) });

        var confirmationMessages = new List<string>();
        confirmationMessages.Add($"The following connections are already present in the system:");
        foreach (var overridingConnection in overridingConnections)
            confirmationMessages.Add($"- {overridingConnection.Name} ({overridingConnection.Type})");
        confirmationMessages.Add("");
        confirmationMessages.Add($"Do you want to proceed?");

        return new ImportConnectionsResultModel
        {
            IsSuccess = false,
            ConfirmationId = connectionImportId,
            ConfirmationText = confirmationMessages,
        };
    }

    public async Task ConfirmImportConnections(string confirmationId, int workspaceId)
    {
        var importConnections = await _distributedCache.GetStringAsync(GetConnectionImportCacheKey(confirmationId));
        if (string.IsNullOrEmpty(importConnections))
            throw new ArgumentException("ConfirmationId is not valid.", nameof(confirmationId));
        var importConnectionsList = importConnections.JsonGet<List<ConnectionExportModel>>();
        await ImportConnectionsConfirmed(importConnectionsList, workspaceId);
    }

    private string GetConnectionImportCacheKey(string confirmationId) => $"connectionImport-{confirmationId}";

    private async Task ImportConnectionsConfirmed(List<ConnectionExportModel> connectionExportModels, int workspaceId)
    {
        foreach (var connectionExport in connectionExportModels)
        {
            var connectionModel = _mapper.Map<ConnectionModel>(connectionExport);
            var existingConnection = await _connectionProcessor.GetByName(connectionExport.Name, workspaceId);
            if (existingConnection != null)
            {
                connectionModel.ConnectionId = existingConnection.ConnectionId;
                connectionModel.Created = existingConnection.Created; 
                connectionModel.CreatedBy = existingConnection.CreatedBy;
            }
            else
            {
                connectionModel.ConnectionId = 0;
            }
            await _connectionProcessor.Set(connectionModel, workspaceId);
        }
    }
}

public interface IConnectionService
{
    Task<List<ConnectionViewModel>> ListConnections(int workspaceId);
    Task<ConnectionViewModel> AddConnection(ConnectionViewModel connectionViewModel, string initiator, int workspaceId);
    Task<ConnectionViewModel> UpdateConnection(ConnectionViewModel connectionViewModel);
    Task DeleteConnection(int connectionId);
    Task<ConnectionViewModel?> GetConnectionById(int connectionId);
    Task<byte[]> ExportConnections(List<int> connectionIdsList);
    Task<ImportConnectionsResultModel> ImportConnections(IFormFile file, int workspaceId);
    Task ConfirmImportConnections(string confirmationId, int workspaceId);
}
