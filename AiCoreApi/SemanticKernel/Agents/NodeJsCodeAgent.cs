using System.Diagnostics;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class NodeJsCodeAgent : BaseAgent, INodeJsCodeAgent
    {
        private static readonly object Lock = new();
        private static readonly List<string> ExecutedCommands = new();

        private readonly IAgentExecutor _agentExecutor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ICacheAccessor _cacheAccessor;
        private readonly ILogger<NodeJsCodeAgent> _logger;

        private string _debugMessageSenderName = "NodeJsCodeAgent";

        private string AgentPath(string id) => $"/tmp/{id}";
        private string AgentExecPath(string id) => $"/tmp/{id}/agent-exec";
        private string AgentResultPath(string id) => $"/tmp/{id}/agent-exec-result";

        private static class AgentContentParameters { public const string JsCode = "nodeJsCode"; }

        public NodeJsCodeAgent(
            IBaseAgentHelper baseAgentHelper,
            IAgentExecutor agentExecutor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ICacheAccessor cacheAccessor,
            ILogger<NodeJsCodeAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _agentExecutor = agentExecutor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _cacheAccessor = cacheAccessor;
            _cacheAccessor.KeyPrefix = "AgentExecution-";
            _logger = logger;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> p)
            => await base.DoCallWrapper(agent, p);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var jsCode = await GetParameterValueAsync(AgentContentParameters.JsCode);
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute Node.js Code", jsCode);

            jsCode = RunCmd(jsCode);     // handle # cmd:

            var runId = Guid.NewGuid().ToString();
            var runDir = AgentPath(runId);
            if (!Directory.Exists(runDir))
                Directory.CreateDirectory(runDir);
            var tempFile = Path.Combine(Path.GetTempPath(), $"agent-{runId}.js");
            // inject Parameters
            var requestAccessorJson = JsonSerializer.Serialize(_requestAccessor);
            var injectRequestAccessor = $"globalThis.requestAccessor = JSON.parse(\"{EscapeForJavaScript(requestAccessorJson)}\");";
            var paramJson = JsonSerializer.Serialize(parameters);
            var injectParams = $"globalThis.Parameters = JSON.parse(\"{EscapeForJavaScript(paramJson)}\");";
            var prelude = $@"
{injectRequestAccessor}
{injectParams}
globalThis.logCritical = (m)=>globalThis.executeAgent('log', ['Critical', m]);
globalThis.logError = (m)=>globalThis.executeAgent('log', ['Error', m]);
globalThis.logWarning  = (m)=>globalThis.executeAgent('log', ['Warning', m]);
globalThis.logDebug = (m)=>globalThis.executeAgent('log', ['Debug', m]);
globalThis.logInformation = (m)=>globalThis.executeAgent('log', ['Information', m]);
globalThis.logTrace  = (m)=>globalThis.executeAgent('log', ['Trace', m]);
globalThis.log = (m)=>globalThis.executeAgent('log', ['', m]);
globalThis.getCacheValue=(k)=>globalThis.executeAgent('getCache', [k]);
globalThis.setCacheValue=(k,v,ttl)=>globalThis.executeAgent('setCache', [k, v, ttl]);

globalThis.executeAgent=(name,args=[])=>{{
  const fs=require('fs');
  fs.writeFileSync('{AgentExecPath(runId)}',JSON.stringify({{name,args}}),'utf8');
  const start=Date.now();
  while(true){{
     if(fs.existsSync('{AgentResultPath(runId)}')){{
        const res=fs.readFileSync('{AgentResultPath(runId)}','utf8');
        fs.unlinkSync('{AgentResultPath(runId)}');
        return res;
     }}
     if(Date.now()-start>120000) throw new Error('executeAgent timeout');
  }}
}};
";
            await File.WriteAllTextAsync(tempFile, prelude + "\n" + jsCode);

            var proc = new Process
            {
                StartInfo = new()
                {
                    FileName = "node",
                    Arguments = tempFile,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.StartInfo.Environment["NODE_PATH"] = "/usr/lib/node_modules";
            proc.Start();

            var watcher = Task.Run(async () =>
            {
                while (!proc.HasExited)
                {
                    if (File.Exists($"{AgentExecPath(runId)}"))
                    {
                        try
                        {
                            var callJson = await File.ReadAllTextAsync($"{AgentExecPath(runId)}");
                            File.Delete($"{AgentExecPath(runId)}");

                            var call = JsonSerializer.Deserialize<AgentCall>(callJson);
                            if (call is { })
                            {
                                var res = "";
                                if (call.name == "log" && call.args.Length == 2)
                                {
                                    switch (call.args[0])
                                    {
                                        case "Critical": _logger.LogCritical(call.args[1]); break;
                                        case "Error": _logger.LogError(call.args[1]); break;
                                        case "Warning": _logger.LogWarning(call.args[1]); break;
                                        case "Debug": _logger.LogDebug(call.args[1]); break;
                                        case "Information": _logger.LogInformation(call.args[1]); break;
                                        case "Trace": _logger.LogTrace(call.args[1]); break;
                                        default: _logger.LogCritical(call.args[1]); break;
                                    }
                                }
                                else if (call.name == "getCache" && call.args.Length == 1)
                                {
                                    res = _cacheAccessor.GetCacheValue(call.args[0]);
                                }
                                else if (call.name == "setCache" && call.args.Length == 3)
                                {
                                    int.TryParse(call.args[2], out var ttl);
                                    res = _cacheAccessor.SetCacheValue(call.args[0], call.args[1], ttl);
                                }
                                else
                                {
                                    res = ExecuteAgent(call.name, call.args);
                                }
                                await File.WriteAllTextAsync($"{AgentResultPath(runId)}", res);
                            }
                        }
                        catch (Exception ex)
                        {
                            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Agent Exec Error", ex.ToString());
                        }
                    }
                    await Task.Delay(10);
                }
            });

            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();
            await watcher;

            var stdErr = await stdErrTask;
            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Node.js Error", stdErr);
                throw new Exception(stdErr);
            }

            var result = (await stdOutTask).Trim();
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Node.js Result", result);

            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists($"{AgentExecPath(runId)}")) File.Delete($"{AgentExecPath(runId)}");
            if (File.Exists($"{AgentResultPath(runId)}")) File.Delete($"{AgentResultPath(runId)}");

            return result;
        }

        private string EscapeForJavaScript(string json) =>
            json.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

        private string RunCmd(string code)
        {
            lock (Lock)
            {
                var pat = @"^// cmd:\s*(.*)";
                foreach (Match m in Regex.Matches(code, pat, RegexOptions.Multiline))
                {
                    var cmd = m.Groups[1].Value.Trim();
                    if (ExecutedCommands.Contains(cmd)) continue;

                    ExecutedCommands.Add(cmd);
                    _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Cmd", cmd);

                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{cmd}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    var outText = p!.StandardOutput.ReadToEnd();
                    var errText = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    _responseAccessor.AddDebugMessage(_debugMessageSenderName,
                        string.IsNullOrWhiteSpace(errText) ? "Cmd Output" : "Cmd Error", cmd + Environment.NewLine +
                        (string.IsNullOrWhiteSpace(errText) ? outText : errText));
                }
                return Regex.Replace(code, pat, string.Empty);
            }
        }

        private string ExecuteAgent(string agentName, string[] parameters)
        {
            return _agentExecutor.ExecuteAsync(agentName, parameters.ToList()).GetAwaiter().GetResult();
        }

        private class AgentCall
        {
            public string name { get; set; } 
            public string[] args { get; set; }
        }
    }

    public interface INodeJsCodeAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
