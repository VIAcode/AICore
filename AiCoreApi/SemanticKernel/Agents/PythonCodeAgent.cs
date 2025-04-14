using System.Diagnostics;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using Python.Runtime;
using System.Text.RegularExpressions;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class PythonCodeAgent : BaseAgent, IPythonCodeAgent
    {
        private static readonly object _lock = new object();
        private static List<string> _executedCommands = new();
        static PythonCodeAgent()
        {
            Runtime.PythonDLL = "/usr/lib/x86_64-linux-gnu/libpython3.11.so";
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
        }

        private string _debugMessageSenderName = "PythonCodeAgent";
        public static class AgentPromptPlaceholders
        {
            public const string HasFilesPlaceholder = "hasFiles";
            public const string FilesNamesPlaceholder = "filesNames";
            public const string FilesDataPlaceholder = "filesData";
        }

        private static class AgentContentParameters
        {
            public const string PythonCode = "pythonCode";
        }

        private readonly IPlannerHelpers _plannerHelpers;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ICacheAccessor _cacheAccessor;
        private readonly ILogger<PythonCodeAgent> _logger;

        public PythonCodeAgent(
            IPlannerHelpers plannerHelpers,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ICacheAccessor cacheAccessor, 
            ExtendedConfig extendedConfig,
            ILogger<PythonCodeAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _plannerHelpers = plannerHelpers;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _cacheAccessor = cacheAccessor;
            _cacheAccessor.KeyPrefix = "AgentExecution-";
            _logger = logger;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters) => await base.DoCallWrapper(agent, parameters);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var pythonCode = ApplyParameters(agent.Content[AgentContentParameters.PythonCode].Value, parameters);
            pythonCode = ApplyParameters(pythonCode, new Dictionary<string, string>
            {
                {AgentPromptPlaceholders.HasFilesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().HasFiles().ToString()},
                {AgentPromptPlaceholders.FilesDataPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileContents()},
                {AgentPromptPlaceholders.FilesNamesPlaceholder, _requestAccessor.MessageDialog.Messages.Last().GetFileNames()}
            });
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Python Code", pythonCode);


            var result = string.Empty;
            try
            {
                using (Py.GIL())
                {
                    using (PyModule scope = Py.CreateScope())
                    {
                        pythonCode = RunCmd(pythonCode);
                        var importPattern = @"^\s*(?:import|from)\s+([\w\.]+)";
                        var matches = Regex.Matches(pythonCode, importPattern, RegexOptions.Multiline);
                        var imports = new List<string>();
                        foreach (Match match in matches)
                        {
                            if (match.Success)
                            {
                                var library = match.Groups[1].Value;
                                imports.Add(library);
                            }
                        }
                        dynamic builtIns = scope.Import("builtins");
                        foreach (var lib in imports)
                        {
                            scope.Import(lib);
                        }

                        builtIns.LogCritical = new Action<string>(LogCritical);
                        builtIns.LogError = new Action<string>(LogError);
                        builtIns.LogWarning = new Action<string>(LogWarning);
                        builtIns.LogDebug = new Action<string>(LogDebug);
                        builtIns.LogInformation = new Action<string>(LogInformation);
                        builtIns.LogTrace = new Action<string>(LogTrace);

                        builtIns.ExecuteAgent = new Func<string, string[]?, string>(ExecuteAgent);
                        builtIns.GetCacheValue = new Func<string, string>(_cacheAccessor.GetCacheValue);
                        builtIns.SetCacheValue = new Func<string, string, int, string>(_cacheAccessor.SetCacheValue);
                        builtIns.Log = new Func<string, string[]?, string>(ExecuteAgent);
                        PyObject requestAccessorPy = _requestAccessor.ToPython();
                        PyObject responseAccessorPy = _responseAccessor.ToPython();
                        PyObject parametersPy = parameters.ToPython();
                        scope.Set("RequestAccessor", requestAccessorPy);
                        scope.Set("ResponseAccessor", responseAccessorPy);
                        scope.Set("Parameters", parametersPy);
                        var pyModule = scope.Exec(pythonCode);
                        result = pyModule.Eval($@"str(result)").ToString();
                    }
                }
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Python Code Error", $"Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}");
                throw;
            }
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Python Code Result", result);
            return result;
        }

        private string RunCmd(string pythonCode)
        {
            lock (_lock)
            {
                var cmdPattern = @"^# cmd:\s*(.*)";
                var commands = Regex.Matches(pythonCode, cmdPattern, RegexOptions.Multiline);
                foreach (Match command in commands)
                {
                    var cmd = command.Groups[1].Value.Trim();
                    if(_executedCommands.Contains(cmd))
                        continue;
                    _executedCommands.Add(cmd);
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Python Code Command", cmd);
                    if (cmd.StartsWith("pip"))
                        cmd += " --break-system-packages --root-user-action=ignore";
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "/bin/bash",
                            Arguments = $"-c \"{cmd}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    // Read asynchronously to avoid deadlocks
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    var cmdResult = outputTask.Result;
                    var cmdError = errorTask.Result;

                    if (!string.IsNullOrWhiteSpace(cmdError))
                    {
                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Python Code Command Error", cmdError);
                    }
                    else
                    {
                        _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Python Code Command Result", cmdResult);
                    }
                }
                pythonCode = Regex.Replace(pythonCode, cmdPattern, string.Empty);
                return pythonCode;
            }
        }

        private void LogCritical(string text) => _logger.LogCritical(text);
        private void LogError(string text) => _logger.LogError(text);
        private void LogWarning(string text) => _logger.LogWarning(text);
        private void LogDebug(string text) => _logger.LogDebug(text);
        private void LogInformation(string text) => _logger.LogInformation(text);
        private void LogTrace(string text) => _logger.LogTrace(text);

        private string ExecuteAgent(string agentName, string[] parameters = null)
        {
            _plannerHelpers.PythonCodeAgent = this;
            try
            {
                return _plannerHelpers.ExecuteAgent(agentName, parameters.ToList()).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName,
                    "Python Code ExecuteAgent Error", $"Agent: {agentName}\r\n\r\n Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}");
                throw;
            }
        }
    }

    public interface IPythonCodeAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
