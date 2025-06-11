using AiCoreApi.Models.ViewModels;
using AiCoreApi.Common;
using AiCoreApi.SemanticKernel;
using AiCoreApi.SemanticKernel.Agents;
using AiCoreApi.Common.Extensions;
using AiCoreApi.Data.Processors;
using System.Security.Claims;
using AiCoreApi.Models.DbModels;

namespace AiCoreApi.Services.ControllersServices
{
    public class CopilotService : ICopilotService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly IPlanner _planner;
        private readonly IWhisperAgent _whisperAgent;
        private readonly IPromptAgent _promptAgent;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IVectorSearchAgent _vectorSearchAgent;
        private readonly ILoginProcessor _loginProcessor;
        private readonly IDebugLogProcessor _debugLogProcessor;
        private readonly ILogger _logger;

        public CopilotService(
            IHttpContextAccessor httpContextAccessor,
            ExtendedConfig extendedConfig,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            IPlanner planner,
            IWhisperAgent whisperAgent,
            IPromptAgent promptAgent,
            IHttpClientFactory httpClientFactory,
            IVectorSearchAgent vectorSearchAgent,
            ILoginProcessor loginProcessor,
            IDebugLogProcessor debugLogProcessor,
            ILogger<CopilotService> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            _extendedConfig = extendedConfig;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _planner = planner;
            _whisperAgent = whisperAgent;
            _promptAgent = promptAgent;
            _httpClientFactory = httpClientFactory;
            _vectorSearchAgent = vectorSearchAgent;
            _loginProcessor = loginProcessor;
            _debugLogProcessor = debugLogProcessor;
            _logger = logger;
        }

        public async Task<string> Prompt(string prompt, double temperature, string connectionName)
        {
            var response = await _promptAgent.Prompt(prompt, temperature, connectionName);
            return response;
        }

        public async Task<MessageDialogViewModel> Chat()
        {
            var messageDialog = _requestAccessor.MessageDialog!;
            var requestMessage = messageDialog.Messages?.Last();
            try
            {
                var response = await _planner.GetChatResponse();
                messageDialog.Messages!.Add(response);
            }
            // Tokens limit reached
            catch (TokensLimitException ex)
            {
                messageDialog.Messages!.Add(new MessageDialogViewModel.Message
                {
                    Sender = PlannerHelpers.AssistantName, 
                    Text = ex.Message,
                    DebugMessages = _responseAccessor.CurrentMessage.DebugMessages
                });
            }
            messageDialog.ClearFilesContent();
            _logger.LogDebug("Chat response generated for: {Login}, Tokens spent: {Spent}, Request: {Request}, Response: {Response}", 
                _requestAccessor.Login, 
                _responseAccessor.CurrentMessage.SpentTokens.ToJson(),
                requestMessage?.Text, 
                _responseAccessor.CurrentMessage.Text);
            var message = requestMessage?.Text ?? "";
            if (requestMessage?.Options != null &&
                requestMessage.Options.Any(x => x.Type == MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall))
            {
                var messageItem = requestMessage.Options.First(x => x.Type == MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall);
                var parametersString = string.Join(Environment.NewLine, messageItem.Parameters.Select(x => $" - {x.Key}: {x.Value}"));
                message = $"Agent: {messageItem.Name}{Environment.NewLine}Parameters:{Environment.NewLine}{parametersString}";
            }
            await _debugLogProcessor.Add(_requestAccessor.Login, message, messageDialog, _requestAccessor.WorkspaceId ?? 0);
            return messageDialog;
        }

        public async Task<MessageDialogViewModel> Agent(string agentName, Dictionary<string, string>? parameters = null)
        {
            _requestAccessor.MessageDialog = new MessageDialogViewModel
            {
                Messages = new List<MessageDialogViewModel.Message>
                {
                    new()
                    {
                        Sender = "User",
                        Text = string.Empty,
                        Options = new MessageDialogViewModel.CallOptions[]
                        {
                            new()
                            {
                                Type = MessageDialogViewModel.CallOptions.CallOptionsType.AgentCall,
                                Name = agentName,
                                Parameters = parameters ?? new Dictionary<string, string>()
                            }
                        }
                    }
                }
            };
            return await Chat();
        }

        public async Task<List<SearchItemModel>?> Search()
        {
            var vectorSearchResult = await _vectorSearchAgent.Search(_requestAccessor.Query, -1, 0, "", "", "", null);
            return vectorSearchResult.JsonGet<List<SearchItemModel>>();
        }

        public async Task<string> Transcribe(IFormFile file)
        {
            if (file.Length == 0)
                return string.Empty;
            byte[] fileBytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                fileBytes = ms.ToArray();
            }
            return await _whisperAgent.Transcribe(Convert.ToBase64String(fileBytes), "webm", "audio/webm");
        }

        public async Task<string> Proxy(ProxyRequestModel proxyRequest)
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClients.RetryClient);
            var method = proxyRequest.Method switch
            {
                "GET" => HttpMethod.Get,
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                _ => HttpMethod.Get
            };
            foreach(var header in proxyRequest.Headers)
                httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            using var httpRequestMessage = new HttpRequestMessage(method, new Uri(proxyRequest.Url));
            if (!string.IsNullOrEmpty(proxyRequest.Body)){ 
                httpRequestMessage.Content = new StringContent(proxyRequest.Body, System.Text.Encoding.UTF8, proxyRequest.ContentType);
            }
            using var response = await httpClient.SendAsync(httpRequestMessage);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public async Task InitializeContext()
        {
            if (!(_httpContextAccessor.HttpContext?.User.Identity is ClaimsIdentity claimsIdentity) || !claimsIdentity.IsAuthenticated)
            {
                var authHeader = _httpContextAccessor.HttpContext?.Request.Headers["Authorization"].FirstOrDefault();
                // Public Access
                if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic "))
                {
                    if (!_extendedConfig.UsePublicCalls)
                        throw new ExceptionHandlingMiddleware.AiCoreAuthException("Public access is not configured in the system.");
                    var publicCallsUser = _extendedConfig.PublicCallsUser;
                    var publicLogin = await _loginProcessor.GetByLogin(publicCallsUser, LoginTypeEnum.Password);
                    if (publicLogin == null)
                        throw new ExceptionHandlingMiddleware.AiCoreAuthException($"Public login '{publicCallsUser}' not found.");
                    _requestAccessor.IsPublicCall = true;
                    _requestAccessor.Login = publicLogin.Login;
                    _requestAccessor.LoginTypeString = LoginTypeEnum.Password.ToString();
                    _requestAccessor.UserContext.SetLoginId(publicLogin.LoginId);
                    _requestAccessor.UserContext.SetTags(publicLogin.Tags);
                }
                // Basic Authentication
                else
                {
                    if (!_extendedConfig.AllowBasicAuth)
                        throw new ExceptionHandlingMiddleware.AiCoreAuthException("Basic Authentication is not allowed.");

                    var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                    var decodedCredentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
                    var credentialsParts = decodedCredentials.Split(':', 2);

                    if (credentialsParts.Length != 2)
                        throw new ExceptionHandlingMiddleware.AiCoreAuthException("Invalid Basic Authentication credentials format.");

                    var login = credentialsParts[0];
                    var password = credentialsParts[1];

                    var loginModel = await _loginProcessor.GetByLogin(login, LoginTypeEnum.Password);
                    if (loginModel == null || loginModel.LoginType != LoginTypeEnum.Password || loginModel.PasswordHash != password.GetHash())
                        throw new ExceptionHandlingMiddleware.AiCoreAuthException("Invalid login or password.");

                    _requestAccessor.IsPublicCall = false;
                    _requestAccessor.Login = login;
                    _requestAccessor.LoginTypeString = LoginTypeEnum.Password.ToString();
                    _requestAccessor.UserContext.SetLoginId(loginModel.LoginId);
                    _requestAccessor.UserContext.SetTags(await _loginProcessor.GetTagsByLogin(login, LoginTypeEnum.Password));
                }
            }
        }

        public async Task<List<MessageDialogViewModel.DebugMessage>> GetDebugMessages(string chatMessageId) => _responseAccessor.GetDebugMessages(chatMessageId);
    }

    public interface ICopilotService
    {
        Task<string> Prompt(string prompt, double temperature, string connectionName);
        Task<MessageDialogViewModel> Chat();
        Task<MessageDialogViewModel> Agent(string agentName, Dictionary<string, string>? parameters = null);
        Task<List<SearchItemModel>?> Search();
        Task<string> Transcribe(IFormFile file);
        Task<string> Proxy(ProxyRequestModel proxyRequest);
        Task<List<MessageDialogViewModel.DebugMessage>> GetDebugMessages(string chatMessageId); 
        Task InitializeContext();
    }
}
