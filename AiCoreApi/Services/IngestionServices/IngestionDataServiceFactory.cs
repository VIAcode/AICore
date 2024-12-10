﻿using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.IngestionServices
{
    public class IngestionDataServiceFactory : IIngestionDataServiceFactory
    {
        private readonly IDataIngestionService _ingestionService;
        private readonly IDataRemovalService _removalService;
        private readonly ITagService _tagService;

        public IngestionDataServiceFactory(
            IDataIngestionService ingestionService, 
            IDataRemovalService removalService, 
            ITagService tagService)
        {
            _ingestionService = ingestionService;
            _removalService = removalService;
            _tagService = tagService;
        }

        public IIngestionDataService GetService(TaskModel task)
        {
            switch (task.Type)
            {
                case TaskType.DataSync:
                    return _ingestionService;
                case TaskType.Remove:
                    return _removalService;
                case TaskType.TagSync:
                    return _tagService;
                default:
                    throw new InvalidOperationException($"Unsupported service type '{task.Type}'.");
            }
        }
    }

    public interface IIngestionDataService
    {
        Task Process(int ingestionId, int taskId);
    }

    public interface IIngestionDataServiceFactory
    {
        IIngestionDataService GetService(TaskModel task);
    }
}
