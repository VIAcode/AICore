﻿using AiCoreApi.Models.ViewModels;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AutoMapper;

namespace AiCoreApi.Services.ControllersServices;

public class DebugLogService : IDebugLogService
{
    private readonly IMapper _mapper;
    private readonly IDebugLogProcessor _debugLogProcessor;

    public DebugLogService(IDebugLogProcessor debugLogProcessor, IMapper mapper)
    {
        _debugLogProcessor = debugLogProcessor;
        _mapper = mapper;
    }

    public async Task<List<DebugLogViewModel>> List(DebugLogFilterViewModel filterViewModel)
    {
        var filterModel = _mapper.Map<DebugLogFilterModel>(filterViewModel);
        var debugLogsModelList = await _debugLogProcessor.List(filterModel);
        var debugLogsViewModelList = _mapper.Map<List<DebugLogViewModel>>(debugLogsModelList);
        return debugLogsViewModelList;
    }

    public async Task<int> PagesCount(DebugLogFilterViewModel filterViewModel)
    {
        var filterModel = _mapper.Map<DebugLogFilterModel>(filterViewModel);
        var pagesCount = await _debugLogProcessor.PagesCount(filterModel);
        return pagesCount;
    }
}

public interface IDebugLogService
{
    Task<List<DebugLogViewModel>> List(DebugLogFilterViewModel filterViewModel);
    Task<int> PagesCount(DebugLogFilterViewModel filterViewModel);
}