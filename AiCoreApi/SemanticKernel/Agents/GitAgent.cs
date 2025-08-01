using LibGit2Sharp;
using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using AiCoreApi.Common;
using AiCoreApi.Data.Processors;
using AiCoreApi.Common.Extensions;
using Newtonsoft.Json;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class GitAgent : BaseAgent, IGitAgent
    {
        private readonly IConnectionProcessor _connectionProcessor;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private string _debugMessageSenderName = "GitAgent";
        private static readonly SemaphoreSlim GitRepoLock = new(1, 1);

        private static class AgentContentParameters
        {
            public const string Action = "action";
            public const string ConnectionName = "connectionName";
            public const string Path = "path";
            public const string Branch = "branch";
            public const string Payload = "payload";
            public const string PayloadType = "payloadType";
            public const string CommitMessage = "commitMessage";
            public const string CommitEmail = "commitEmail";
        }

        private static class ConnectionContentParameters
        {
            public const string GitStorageUrl = "gitStorageUrl";
            public const string Login = "login";
            public const string Password = "password";
        }

        public GitAgent(
            IBaseAgentHelper baseAgentHelper,
            IConnectionProcessor connectionProcessor,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ILogger<GitAgent> logger)
            : base(baseAgentHelper, logger)
        {
            _connectionProcessor = connectionProcessor;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
        }

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            var action = agent.Content[AgentContentParameters.Action].Value;
            var connectionName = agent.Content[AgentContentParameters.ConnectionName].Value;
            var path = GetParameterValue(AgentContentParameters.Path);
            var branch = GetParameterValue(AgentContentParameters.Branch);
            var payload = GetParameterValue(AgentContentParameters.Payload);
            var payloadType = GetParameterValue(AgentContentParameters.PayloadType);
            var commitMessage = GetParameterValue(AgentContentParameters.CommitMessage, "AI Core commit");
            var commitEmail = GetParameterValue(AgentContentParameters.CommitEmail);

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Request",
                $"Action: {action}, ConnectionName: {connectionName}, Path: {path}, Branch: {branch}, CommitMessage: {commitMessage}");

            var connections = await _connectionProcessor.List(_requestAccessor.WorkspaceId);
            var connection = GetConnection(_requestAccessor, _responseAccessor, connections, ConnectionType.Git, _debugMessageSenderName, connectionName: connectionName);
            var login = connection.Content[ConnectionContentParameters.Login];
            var password = connection.Content[ConnectionContentParameters.Password];
            var gitStorageUrl = connection.Content[ConnectionContentParameters.GitStorageUrl];

            var repoPath = await EnsureClonedGitRepo(gitStorageUrl, branch, login, password);
            using var repo = new Repository(repoPath);

            string result = action switch
            {
                "LIST" => ListFiles(repo, path),
                "GET" => GetFile(repo, path),
                "ANNOTATE" => AnnotateFile(repo, path),
                "ADD_OR_UPDATE" => AddOrUpdateOrDeleteFile(repo, repoPath, path, payload, payloadType, commitMessage, commitEmail, login, password, branch, isDelete: false),
                "DELETE" => AddOrUpdateOrDeleteFile(repo, repoPath, path, null, null, commitMessage, commitEmail, login, password, branch, isDelete: true),
                _ => throw new ExceptionHandlingMiddleware.AiCoreUiException($"Unsupported action '{action}' for Git")
            };

            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "DoCall Response", result);
            return result;
        }

        private async Task<string> EnsureClonedGitRepo(string gitStorageUrl, string branch, string username, string password)
        {
            string repoKey = gitStorageUrl + branch;
            var repoPath = Path.Combine(Path.GetTempPath(), $"git-cache-{repoKey.GetHashCode()}");

            await GitRepoLock.WaitAsync();
            try
            {
                if (!Repository.IsValid(repoPath))
                {
                    if (Directory.Exists(repoPath)) Directory.Delete(repoPath, true);

                    Repository.Clone(gitStorageUrl, repoPath, new CloneOptions
                    {
                        BranchName = branch,
                        FetchOptions =
                        {
                            CredentialsProvider = (_, _, _) =>
                                new UsernamePasswordCredentials
                                {
                                    Username = username,
                                    Password = password
                                }
                        }
                    });
                }
                else
                {
                    using var repo = new Repository(repoPath);
                    Commands.Fetch(repo, "origin", repo.Network.Remotes["origin"].FetchRefSpecs.Select(x => x.Specification), new FetchOptions
                    {
                        CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = username, Password = password }
                    }, null);
                    EnsureBranchCheckedOut(repo, branch);
                }
            }
            finally
            {
                GitRepoLock.Release();
            }
            return repoPath;
        }

        private Branch EnsureBranchCheckedOut(Repository repo, string branch)
        {
            var local = repo.Branches[branch] ?? repo.CreateBranch(branch, repo.Branches[$"origin/{branch}"].Tip);
            Commands.Checkout(repo, local);
            return local;
        }

        private string AddOrUpdateOrDeleteFile(Repository repo, string repoPath, string path, string? payload, string? payloadType, string commitMessage, string commitEmail, string login, string password, string branch, bool isDelete)
        {
            path = path.TrimStart('/');
            string absPath = Path.Combine(repoPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

            if (isDelete)
            {
                if (!File.Exists(absPath)) return $"File {path} not found.";
                File.Delete(absPath);
            }
            else
            {
                if (payloadType == "text") File.WriteAllText(absPath, payload);
                else if (payloadType == "binary") File.WriteAllBytes(absPath, Convert.FromBase64String(payload!));
                else throw new ExceptionHandlingMiddleware.AiCoreUiException("Payload Type incorrect.");
            }

            Commands.Stage(repo, path);
            var author = new Signature(commitEmail, commitEmail, DateTimeOffset.Now);
            repo.Commit(commitMessage, author, author);
            repo.Network.Push(repo.Network.Remotes["origin"], $"+refs/heads/{branch}:refs/heads/{branch}", new PushOptions
            {
                CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = login, Password = password }
            });

            return isDelete ? $"File {path} deleted" : $"File {path} committed.";
        }

        private string GetFile(Repository repo, string path)
        {
            path = path.TrimStart('/');
            var blob = repo.Head[path]?.Target as Blob;
            if (blob == null) return "";

            using var stream = blob.GetContentStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            return blob.IsBinary ? Convert.ToBase64String(ms.ToArray()) : blob.GetContentText();
        }

        private string ListFiles(Repository repo, string path)
        {
            path = path.TrimStart('/');
            var result = new List<GitFile>();
            var queue = new Queue<TreeEntry>(repo.Head.Tip.Tree);

            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                if (entry.TargetType == TreeEntryTargetType.Blob)
                {
                    var blob = (Blob)entry.Target;
                    result.Add(new GitFile
                    {
                        Path = entry.Path,
                        Size = blob.Size,
                        IsBinary = blob.IsBinary
                    });
                }
                else if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    foreach (var sub in (Tree)entry.Target) queue.Enqueue(sub);
                }
            }

            return result.Where(f => f.Path != null && f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList().ToJson();
        }

        private string AnnotateFile(Repository repo, string path)
        {
            path = path.TrimStart('/');
            var annotations = new List<GitAnnotationChunk>();
            var lines = GetFile(repo, path).Split('\n');
            var blame = repo.Blame(path);

            foreach (var hunk in blame)
            {
                var chunkLines = lines.Skip(hunk.FinalStartLineNumber - 1).Take(hunk.LineCount).Select(l => l.TrimEnd('\r')).ToList();
                annotations.Add(new GitAnnotationChunk
                {
                    StartLine = hunk.FinalStartLineNumber,
                    EndLine = hunk.FinalStartLineNumber + hunk.LineCount - 1,
                    Author = hunk.FinalCommit.Author.Name,
                    CommitSha = hunk.FinalCommit.Sha,
                    CommitDate = hunk.FinalCommit.Author.When.UtcDateTime,
                    Content = string.Join("\n", chunkLines)
                });
            }

            return annotations.ToJson();
        }

        public class GitAnnotationChunk
        {
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public string Author { get; set; } = string.Empty;
            public string CommitSha { get; set; } = string.Empty;
            public DateTime CommitDate { get; set; }
            public string Content { get; set; } = string.Empty;
        }

        public class GitFile
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? Path { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public long? Size { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public bool? IsBinary { get; set; }
        }

        public Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions) => Task.CompletedTask;
    }

    public interface IGitAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
    }
}
