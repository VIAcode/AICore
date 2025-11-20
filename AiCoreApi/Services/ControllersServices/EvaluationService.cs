using AiCoreApi.Common;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Models.ViewModels;
using AiCoreApi.SemanticKernel;
using AutoMapper;
using System.Diagnostics;
using static AiCoreApi.Common.ExceptionHandlingMiddleware;

namespace AiCoreApi.Services.ControllersServices;

public class EvaluationService : IEvaluationService
{
    private readonly IMapper _mapper;
    private readonly RequestAccessor _requestAccessor;
    private readonly ResponseAccessor _responseAccessor;
    private readonly IEvaluationProcessor _evaluationProcessor;
    private readonly IEvaluationHistoryProcessor _evaluationHistoryProcessor;
    private readonly ISemanticKernelProvider _semanticKernelProvider;
    private readonly IConnectionProcessor _connectionProcessor;
    private readonly IAgentsProcessor _agentsProcessor;
    private readonly IAgentExecutor _agentExecutor;
    public const string ParameterDescription = "parameterDescription";

    public EvaluationService(
        IMapper mapper,
        RequestAccessor requestAccessor,
        ResponseAccessor responseAccessor,
        IEvaluationProcessor evaluationProcessor,
        IEvaluationHistoryProcessor evaluationHistoryProcessor,
        ISemanticKernelProvider semanticKernelProvider,
        IConnectionProcessor connectionProcessor,
        IAgentsProcessor agentsProcessor,
        IAgentExecutor agentExecutor)
    {
        _mapper = mapper;
        _requestAccessor = requestAccessor;
        _responseAccessor = responseAccessor;
        _evaluationProcessor = evaluationProcessor;
        _evaluationHistoryProcessor = evaluationHistoryProcessor;
        _semanticKernelProvider = semanticKernelProvider;
        _connectionProcessor = connectionProcessor;
        _agentsProcessor = agentsProcessor;
        _agentExecutor = agentExecutor;
    }

    public async Task<EvaluationViewModel?> GetById(int evaluationId)
    {
        var evaluation = await _evaluationProcessor.Get(evaluationId);
        if (evaluation == null) return null;

        var evaluationViewModel = _mapper.Map<EvaluationViewModel>(evaluation);
        return evaluationViewModel;
    }

    public async Task<List<EvaluationHistoryViewModel>> ListHistory(int evaluationId)
    {
        var evaluationHistoryList = await _evaluationHistoryProcessor.List(evaluationId, _requestAccessor.WorkspaceId ?? 0);
        var evaluationHistoryViewModelList = _mapper.Map<List<EvaluationHistoryViewModel>>(evaluationHistoryList);
        return evaluationHistoryViewModelList;
    }

    public async Task<EvaluationHistoryViewModel> GetHistoryItem(int evaluationHistoryId)
    {
        var evaluationHistory = await _evaluationHistoryProcessor.GetShort(evaluationHistoryId);
        var evaluationHistoryViewModel = _mapper.Map<EvaluationHistoryViewModel>(evaluationHistory);
        var i = 1;
        evaluationHistoryViewModel.Questions.ForEach(x => x.Id = i++);
        return evaluationHistoryViewModel;
    }

    public async Task<List<EvaluationViewModel>> List()
    {
        var evaluationList = await _evaluationProcessor.List(_requestAccessor.WorkspaceId ?? 0);
        var evaluationViewModelList = _mapper.Map<List<EvaluationViewModel>>(evaluationList);
        return evaluationViewModelList;
    }

    public async Task Delete(int evaluationId)
    {
        await _evaluationProcessor.Delete(evaluationId);
    }

    public async Task<EvaluationViewModel> Add(EvaluationViewModel evaluationViewModel)
    {
        var evaluationModel = _mapper.Map<EvaluationModel>(evaluationViewModel);
        evaluationModel.WorkspaceId = _requestAccessor.WorkspaceId ?? 0;
        var addedEvaluation = await _evaluationProcessor.Add(evaluationModel);
        var addedEvaluationViewModel = _mapper.Map<EvaluationViewModel>(addedEvaluation);
        return addedEvaluationViewModel;
    }

    public async Task<EvaluationViewModel> Update(EvaluationViewModel evaluationViewModel)
    {
        var evaluationModel = _mapper.Map<EvaluationModel>(evaluationViewModel);
        var updatedEvaluation = await _evaluationProcessor.Update(evaluationModel);
        var updatedEvaluationViewModel = _mapper.Map<EvaluationViewModel>(updatedEvaluation);
        return updatedEvaluationViewModel;
    }

    public async Task<int> Run(int evaluationId, bool useRevert)
    {
        var evaluation = await _evaluationProcessor.Get(evaluationId);
        if (evaluation == null) 
            return 0;
        evaluation.LastRun = DateTime.UtcNow;
        _requestAccessor.WorkspaceId = evaluation.WorkspaceId;

        var connection = await GetConnection(evaluation);
        var agentParameters = await GetAgentParameters(evaluation);
        var overallScore = 0.0;

        var evaluationHistory = await _evaluationHistoryProcessor.Add(new EvaluationHistoryModel
        {
            AgentName = evaluation.AgentName,
            EvaluationId = evaluation.EvaluationId,
            EvaluationLlmModel = evaluation.EvaluationLlmModel,
            WorkspaceId = evaluation.WorkspaceId,
            Questions = new List<EvaluationHistoryQuestionModel>(),
            Created = DateTime.UtcNow,
            CreatedBy = _requestAccessor.Login ?? "Unknown",
            EvaluationPrompt = evaluation.EvaluationPrompt,
            Status = "Running"
        });

        var systemPrompt = "You are an expert evaluator. Your task is to assess how well a candidate answer matches a reference answer. Use semantic understanding, not just surface similarity. Differences in wording are acceptable if the meaning is preserved. Output only a score from 0 to 100, where:\r\n- 100 means completely correct in meaning,\r\n- 0 means completely incorrect or unrelated.\r\nNo explanation. Only return a single number.";

        int completionsCount = 0;
        int overallDurationMs = 0;
        int totalQuestions = evaluation.Questions.Sum(q => q.SampleSize);
        foreach (var question in evaluation.Questions.SelectMany(q => Enumerable.Repeat(q, q.SampleSize)))
        {
            completionsCount++;
            var answer = "";
            var score = 0;
            var input = "";
            var durationMs = 0;
            var i = 0;
            foreach (var agentParameter in agentParameters)
                input += $"{agentParameter.Trim()}: {question.Parameters[i++]}\n";

            try
            {
                var result = await RunAgent(evaluation, question);
                answer = result.Answer;
                durationMs = result.DurationMs;
                var evaluationPrompt = evaluation.EvaluationPrompt;
                evaluationPrompt = evaluationPrompt
                    .Replace("{{input}}", input)
                    .Replace("{{reference}}", question.ExpectedResult)
                    .Replace("{{candidate}}", answer);
                var promptResult = await _semanticKernelProvider.ExecutePrompt(connection, evaluationPrompt, 0, 1, systemPrompt);
                score = Convert.ToInt32(promptResult.Trim());
            }
            catch (Exception e)
            {
                answer = $"Exception: {e.Message}";
            }
            var debugMessages = _responseAccessor.CurrentMessage.DebugMessages.ToJson() ?? "";
            overallScore += score;
            overallDurationMs += durationMs;
            evaluationHistory.Questions.Add(new EvaluationHistoryQuestionModel
            {
                Parameters = question.Parameters,
                ExpectedResult = question.ExpectedResult,
                Result = answer,
                DebugMessages = debugMessages,
                Score = score,
                DurationTotalMs = durationMs,
            });
            evaluation.Progress = $"{(completionsCount == totalQuestions ? "Done" : "In Progress")} [{completionsCount}/{totalQuestions}]";
            await _evaluationProcessor.Update(evaluation);
            await _evaluationHistoryProcessor.Update(evaluationHistory);
        }
        if (completionsCount > 0)
        {
            overallScore /= completionsCount;
        }
        var overallScoreInt = Convert.ToInt32(overallScore);
        if (useRevert && evaluation.LastScore > overallScoreInt)
        {
            evaluationHistory.Status = $"Completed [{completionsCount}/{totalQuestions}], Score: {overallScoreInt}, Duration total: {overallDurationMs:n0} ms. Reverted.";
        }
        else
        {
            evaluation.LastScore = overallScoreInt;
            evaluationHistory.Status = $"Completed [{completionsCount}/{totalQuestions}], Score: {overallScoreInt}, Duration total: {overallDurationMs:n0} ms";
        }
        await _evaluationProcessor.Update(evaluation);
        await _evaluationHistoryProcessor.Update(evaluationHistory);
        return overallScoreInt;
    }

    private async Task<ConnectionModel?> GetConnection(EvaluationModel evaluation)
    {
        ConnectionModel? connection = null;
        if (evaluation.EvaluationLlmModel != "Default")
        {
            connection = await _connectionProcessor.GetByName(evaluation.EvaluationLlmModel, _requestAccessor.WorkspaceId);
        }
        if (connection == null)
        {
            connection = await _connectionProcessor.List(_requestAccessor.WorkspaceId)
                .ContinueWith(t => t.Result.FirstOrDefault(c => c.Type.IsLlmConnection()));
        }
        if (connection == null)
        {
            throw new AiCoreUiException("No LLM connection found for evaluation.");
        }
        return connection;
    }

    private async Task<string[]> GetAgentParameters(EvaluationModel evaluation)
    {
        var agent = await _agentsProcessor.GetByName(evaluation.AgentName, _requestAccessor.WorkspaceId);
        if (agent == null)
        {
            throw new AiCoreUiException($"Agent '{evaluation.AgentName}' not found.");
        }
        if (!agent.Content.ContainsKey(ParameterDescription))
        {
            throw new AiCoreUiException($"Agent '{evaluation.AgentName}' does not have a parameter description.");
        }
        var agentParameters = agent.Content[ParameterDescription].Value.Split(',');
        return agentParameters;
    }

    private async Task<(string Answer, int DurationMs)> RunAgent(EvaluationModel evaluation, EvaluationQuestionModel question)
    {
        if (_responseAccessor.CurrentMessage.DebugMessages != null)
            _responseAccessor.CurrentMessage.DebugMessages.Clear();
        _requestAccessor.UseDebug = true;
        _requestAccessor.MessageDialog = new MessageDialogViewModel
        {
            Messages = new List<MessageDialogViewModel.Message>
            {
                new()
                {
                    Sender = "User",
                    Text = string.Empty,
                    Options =
                    [
                        new()
                        {
                            Type = MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall,
                            Name = evaluation.AgentName,
                            Parameters = question.Parameters
                                .Select((p, i) => new {name = $"parameter{i + 1}", value = p})
                                .ToDictionary(p => p.name, p => p.value),
                        }
                    ]
                }
            }
        };
        var stopwatch = Stopwatch.StartNew();
        var result = await _agentExecutor.ExecuteAsync(evaluation.AgentName, question.Parameters);
        stopwatch.Stop();
        var durationMs = (int)stopwatch.ElapsedMilliseconds;
        return (result, durationMs);
    }

    public async Task<List<DebugMessageViewModel>> GetDebugMessages(int evaluationHistoryId, int logId)
    {
        var evaluationHistory = await _evaluationHistoryProcessor.Get(evaluationHistoryId);
        if (evaluationHistory == null)
            throw new AiCoreUiException($"Evaluation history with ID {evaluationHistoryId} not found.");
        if (logId <= 0 || logId > evaluationHistory.Questions.Count)
            throw new AiCoreUiException($"Invalid logId {logId}. It must be between 1 and {evaluationHistory.Questions.Count}.");
        var debugMessagesString = evaluationHistory.Questions[logId - 1].DebugMessages;
        if (string.IsNullOrEmpty(debugMessagesString) || debugMessagesString == "[]")
            return new List<DebugMessageViewModel>();
        var debugMessages = debugMessagesString.JsonGet<List<DebugMessage>>();
        if (debugMessages == null)
        {
            throw new AiCoreUiException($"Debug messages for evaluation history ID {evaluationHistoryId} and log ID {logId} are not available.");
        }
        var debugMessagesViewModel = _mapper.Map<List<DebugMessageViewModel>>(debugMessages);
        return debugMessagesViewModel;
    }
}

public interface IEvaluationService
{
    Task<EvaluationViewModel?> GetById(int evaluationId); 
    Task<List<EvaluationHistoryViewModel>> ListHistory(int evaluationId);
    Task<EvaluationHistoryViewModel> GetHistoryItem(int evaluationHistoryId);
    Task<List<EvaluationViewModel>> List();
    Task Delete(int evaluationId);
    Task<int> Run(int evaluationId, bool useRevert); 
    Task<EvaluationViewModel> Add(EvaluationViewModel evaluationViewModel);
    Task<EvaluationViewModel> Update(EvaluationViewModel evaluationViewModel);
    Task<List<DebugMessageViewModel>> GetDebugMessages(int evaluationHistoryId, int logId);
}
