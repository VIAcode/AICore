using Microsoft.SemanticKernel;
using AiCoreApi.Models.DbModels;
using System.Web;
using AiCoreApi.Common;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using NuGet.Protocol.Core.Types;
using NuGet.Configuration;
using NuGet.Common;
using NuGet.Versioning;
using NuGet.Packaging;
using NuGet.Frameworks;
using System.Reflection;
using System.Text.RegularExpressions;
using ILogger = NuGet.Common.ILogger;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using AiCoreApi.Common.Extensions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Runtime.Loader;

namespace AiCoreApi.SemanticKernel.Agents
{
    public class CsharpCodeAgent : BaseAgent, ICsharpCodeAgent
    {
        private static ConcurrentDictionary<string, List<string>> _assemblyPaths = new();
        private static ConcurrentDictionary<string, Script<string>> _compiledScripts = new();

        private string _debugMessageSenderName = "CSharpCodeAgent";

        private static class AgentContentParameters
        {
            public const string CsharpCode = "csharpCode";
        }

        private readonly IPlannerHelpers _plannerHelpers;
        private readonly RequestAccessor _requestAccessor;
        private readonly ResponseAccessor _responseAccessor;
        private readonly ExtendedConfig _extendedConfig;
        private readonly ICacheAccessor _cacheAccessor;
        private readonly ILogger<CsharpCodeAgent> _logger;

        public CsharpCodeAgent(
            IPlannerHelpers plannerHelpers,
            RequestAccessor requestAccessor,
            ResponseAccessor responseAccessor,
            ExtendedConfig extendedConfig,
            ICacheAccessor cacheAccessor,
            ILogger<CsharpCodeAgent> logger) : base(responseAccessor, requestAccessor, extendedConfig, logger)
        {
            _plannerHelpers = plannerHelpers;
            _requestAccessor = requestAccessor;
            _responseAccessor = responseAccessor;
            _extendedConfig = extendedConfig;
            _cacheAccessor = cacheAccessor;
            _cacheAccessor.KeyPrefix = "AgentExecution-";
            _logger = logger;
        }

        public async Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters) => await base.DoCallWrapper(agent, parameters);

        public override async Task<string> DoCall(AgentModel agent, Dictionary<string, string> parameters)
        {
            parameters.ToList().ForEach(p => parameters[p.Key] = HttpUtility.HtmlDecode(p.Value));
            _debugMessageSenderName = $"{agent.Name} ({agent.Type})";

            // Insert user parameters into the code template
            var csharpCode = ApplyParameters(agent.Content[AgentContentParameters.CsharpCode].Value, parameters);

            // If the code does not define "class Agent {...}", switch to quick mode
            var quickMode = !csharpCode.Replace(" ", "").Contains("classAgent");

            return quickMode
                ? await QuickCall(parameters, csharpCode)
                : await Call(parameters, csharpCode);
        }

        private async Task<string> Call(Dictionary<string, string> parameters, string csharpCode)
        {
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute C# Code", csharpCode);

            // Clean the script code by removing #r "..." directives
            var cleanedCode = Regex.Replace(csharpCode, @"#r\s+""nuget:[^""]+""", "");
            cleanedCode = Regex.Replace(cleanedCode, @"#r\s+""[^""]+""", "");

            // Unique ID for compiled code
            var scriptCacheKey = cleanedCode.GetHash().Replace("=", "").Replace("/", "");
            var dllPath = $"/app/{scriptCacheKey}.dll";

            var nugetDirectives = ExtractNuGetDirectives(csharpCode);
            var assemblyPathsCacheKey = nugetDirectives.ToJson()?.GetHash() ?? "default";
            // If we haven't compiled a DLL for this code yet, do so
            if (!File.Exists(dllPath) || new FileInfo(dllPath).Length == 0 || (assemblyPathsCacheKey != "default" && !_assemblyPaths.ContainsKey(assemblyPathsCacheKey)))
            {
                // Extract and resolve any NuGet directives

                // See if we've already loaded & cached the assembly paths
                _assemblyPaths.TryGetValue(assemblyPathsCacheKey, out var assemblyPaths);
                if (assemblyPaths == null)
                {
                    assemblyPaths = await ResolveNuGetPackages(nugetDirectives);
                    _assemblyPaths.TryAdd(assemblyPathsCacheKey, assemblyPaths);
                }

                // Compile the code into a DLL
                var compiler = new DynamicCompiler();
                dllPath = compiler.CompileCodeToDll(cleanedCode, dllPath, assemblyPaths, scriptCacheKey);
            }

            var references = _assemblyPaths.FirstOrDefault(x => x.Key == assemblyPathsCacheKey).Value;
            try
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "C# Code Execution", "");

                // Prepare delegate references for Agent calls
                Func<string, List<string>?, string> executeAgent = ExecuteAgent;
                Func<string, string> getCacheValue = _cacheAccessor.GetCacheValue;
                Func<string, string, int, string> setCacheValue = _cacheAccessor.SetCacheValue;

                // Dynamically load and execute the compiled assembly
                var executor = new DynamicAssemblyExecutor();
                var methodInfo = executor.GetMethodInfo(dllPath, "Agent", "Run");

                if (methodInfo == null)
                    throw new InvalidOperationException("The 'Run' method could not be found.");

                // Based on how many parameters the Run(...) method expects
                var methodParameters = methodInfo.GetParameters();
                object?[] args;

                if (methodParameters.Length == 4)
                {
                    args = new object?[] { parameters, _requestAccessor, _responseAccessor, executeAgent };
                }
                else if (methodParameters.Length == 5)
                {
                    args = new object?[] { parameters, _requestAccessor, _responseAccessor, executeAgent, _logger };
                }
                else if (methodParameters.Length == 6)
                {
                    args = new object?[] { parameters, _requestAccessor, _responseAccessor, executeAgent, getCacheValue, setCacheValue };
                }
                else if (methodParameters.Length == 7)
                {
                    args = new object?[] { parameters, _requestAccessor, _responseAccessor, executeAgent, getCacheValue, setCacheValue, _logger };
                }
                else
                {
                    throw new InvalidOperationException("The 'Run' method has an unsupported parameter count.");
                }

                var result = executor.Execute(dllPath, "Agent", "Run", args, references)?.ToString();
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "C# Code Result", result);
                return result;
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(
                    _debugMessageSenderName,
                    "C# Code Error",
                    $"Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}"
                );
                throw;
            }
        }

        // Quick mode – interpret the snippet directly with Roslyn's C# scripting
        private async Task<string> QuickCall(Dictionary<string, string> parameters, string csharpCode)
        {
            _responseAccessor.AddDebugMessage(_debugMessageSenderName, "Execute C# Code (quick)", csharpCode);

            var globals = new Globals
            {
                Parameters = parameters,
                RequestAccessor = _requestAccessor,
                ResponseAccessor = _responseAccessor,
                ExecuteAgent = ExecuteAgent,
                GetCacheValue = _cacheAccessor.GetCacheValue,
                SetCacheValue = _cacheAccessor.SetCacheValue,
                Logger = _logger,
            };

            // Clean out #r directives
            var cleanedCode = Regex.Replace(csharpCode, @"#r\s+""nuget:[^""]+""", "");
            cleanedCode = Regex.Replace(cleanedCode, @"#r\s+""[^""]+""", "");

            // Create a stable cache key
            var scriptCacheKey = cleanedCode.GetHash().Replace("=", "").Replace("/", "");

            if (!_compiledScripts.TryGetValue(scriptCacheKey, out var compiledScript))
            {
                // Extract and resolve any NuGet directives
                var nugetDirectives = ExtractNuGetDirectives(csharpCode);
                var assemblyPathsJson = nugetDirectives.ToJson();
                var assemblyPathsCacheKey = assemblyPathsJson?.GetHash() ?? "default";

                _assemblyPaths.TryGetValue(assemblyPathsCacheKey, out var assemblyPaths);
                if (assemblyPaths == null)
                {
                    assemblyPaths = await ResolveNuGetPackages(nugetDirectives);
                    _assemblyPaths.TryAdd(assemblyPathsCacheKey, assemblyPaths);
                }

                // Build script options with references as metadata
                var scriptOptions = ScriptOptions.Default
                    .WithReferences(assemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));

                // Optionally include imports if needed:
                // scriptOptions = scriptOptions.WithImports("System", "System.Linq", ...);

                // Create the script
                compiledScript = CSharpScript.Create<string>(cleanedCode, scriptOptions, typeof(Globals));
                _compiledScripts.TryAdd(scriptCacheKey, compiledScript);
            }

            try
            {
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "C# Code Execution (quick)", "");
                var result = await compiledScript.RunAsync(globals);
                _responseAccessor.AddDebugMessage(_debugMessageSenderName, "C# Code Result (quick)", result.ReturnValue);
                return result.ReturnValue;
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(
                    _debugMessageSenderName,
                    "C# Code Error (quick)",
                    $"Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}"
                );
                throw;
            }
        }

        // Helper for chaining an agent call from within the code
        private string ExecuteAgent(string agentName, List<string>? parameters = null)
        {
            _plannerHelpers.CsharpCodeAgent = this;
            try
            {
                return _plannerHelpers.ExecuteAgent(agentName, parameters).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _responseAccessor.AddDebugMessage(
                    _debugMessageSenderName,
                    "C# Code ExecuteAgent Error",
                    $"Agent: {agentName}\r\n\r\n Exception: {e.Message}\r\n\r\nInner Exception: {e.InnerException?.Message}"
                );
                throw;
            }
        }

        // Extract #r "nuget:PackageName,Version" from code
        private List<(string packageName, string version)> ExtractNuGetDirectives(string code)
        {
            var regex = new Regex(@"#r\s+""nuget:\s*(?<package>[^,]+),\s*(?<version>[^""]+)""");
            var matches = regex.Matches(code);

            var packages = new List<(string, string)>();
            foreach (Match match in matches)
            {
                var packageName = match.Groups["package"].Value.Trim();
                var packageVersion = match.Groups["version"].Value.Trim();
                packages.Add((packageName, packageVersion));
            }
            return packages;
        }

        // Resolves the given set of packages (plus all dependencies) to local DLL files
        private async Task<List<string>> ResolveNuGetPackages(List<(string packageName, string version)> packages)
        {
            var packagePaths = new List<string>();
            var cache = new SourceCacheContext();
            var logger = NullLogger.Instance;
            var providers = Repository.Provider.GetCoreV3();

            // Create list of repositories based on config settings
            var repositories = new List<SourceRepository>();

            // Add custom feed if enabled
            if (_extendedConfig.UseCustomNugetFeed && !string.IsNullOrEmpty(_extendedConfig.CustomNugetFeedUrl))
            {
                var customSource = new PackageSource(_extendedConfig.CustomNugetFeedUrl);

                // Add API key credentials if provided
                if (!string.IsNullOrEmpty(_extendedConfig.CustomNugetFeedApiKey))
                {
                    customSource.Credentials = new PackageSourceCredential(
                        customSource.Name,
                        "user",
                        _extendedConfig.CustomNugetFeedApiKey,
                        isPasswordClearText: true,
                        validAuthenticationTypesText: null);
                }

                repositories.Add(new SourceRepository(customSource, providers));
            }

            // Add NuGet.org as fallback or if no custom feed is configured
            if (_extendedConfig.UseNugetOrgFallback || repositories.Count == 0)
            {
                var nugetSource = new PackageSource("https://api.nuget.org/v3/index.json");
                repositories.Add(new SourceRepository(nugetSource, providers));
            }

            var processedPackages = new HashSet<string>();
            foreach (var (packageName, version) in packages)
            {
                await ResolvePackageAndDependencies(
                    packageName,
                    version,
                    repositories,
                    cache,
                    logger,
                    packagePaths,
                    processedPackages
                );
            }
            return packagePaths;
        }


        private async Task<(FindPackageByIdResource? resource, NuGetVersion? selectedVersion)> GetPackageResourceAndVersion(
            string packageName, string version, List<SourceRepository> repositories, SourceCacheContext cache, ILogger logger) =>
            await GetPackageResourceAndVersion(packageName, new VersionRange(NuGetVersion.Parse(version)), repositories, cache, logger);

        private async Task<(FindPackageByIdResource? resource, NuGetVersion? selectedVersion)> GetPackageResourceAndVersion(
            string packageName,
            VersionRange versionRange,
            List<SourceRepository> repositories,
            SourceCacheContext cache,
            ILogger logger)
        {
            FindPackageByIdResource? resource = null;
            NuGetVersion? selectedVersion = null;

            foreach (var repository in repositories)
            {
                resource = await repository.GetResourceAsync<FindPackageByIdResource>();
                var versions = await resource.GetAllVersionsAsync(packageName, cache, logger, CancellationToken.None);
                selectedVersion = versions.FindBestMatch(versionRange, v => v);
                if (selectedVersion == null)
                {
                    if (!versions.Any())
                        continue;

                    // If the exact version wasn't found, use the latest
                    selectedVersion = versions.Last();
                    _responseAccessor.AddDebugMessage(
                        _debugMessageSenderName,
                        "C# Code Warning",
                        $"NuGet package '{packageName}' version '{versionRange.ToString()}' not found. Using latest: {selectedVersion}"
                    );
                }
                break;
            }

            return (resource, selectedVersion);
        }

        private async Task ResolvePackageAndDependencies(
            string packageName,
            string version,
            List<SourceRepository> repositories,
            SourceCacheContext cache,
            ILogger logger,
            List<string> packagePaths,
            HashSet<string> processedPackages)
        {
            var currentFramework = NuGetFramework.ParseFolder($"net{Environment.Version.Major}.{Environment.Version.Minor}");

            (FindPackageByIdResource? resource, NuGetVersion? selectedVersion) = await GetPackageResourceAndVersion(packageName, version, repositories, cache, logger);

            if (resource == null || selectedVersion == null)
                throw new Exception($"NuGet package '{packageName}' not found in any repository");


            var packageKey = $"{packageName}.{selectedVersion}";
            if (processedPackages.Contains(packageKey))
                return; // Already handled

            processedPackages.Add(packageKey);

            // Download .nupkg into a temp folder
            var tempPath = Path.Combine(Path.GetTempPath(), packageName, selectedVersion.ToNormalizedString());
            Directory.CreateDirectory(tempPath);
            var packagePath = Path.Combine(tempPath, $"{packageName}.nupkg");

            if (!File.Exists(packagePath))
            {
                using var fs = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resource.CopyNupkgToStreamAsync(
                    packageName,
                    selectedVersion,
                    fs,
                    cache,
                    logger,
                    CancellationToken.None
                );
            }

            var packageReader = new PackageArchiveReader(packagePath);

            // Extract the most appropriate libs
            var libItems = (await packageReader.GetLibItemsAsync(CancellationToken.None))
                .Where(x => x.TargetFramework.Framework != ".NETFramework")
                .OrderByDescending(x => x.TargetFramework.Framework)
                .ThenByDescending(x => x.TargetFramework.Version)
                .ToList();

            foreach (var item in libItems)
            {
                bool anythingLoaded = false;
                if (IsCompatibleFramework(item.TargetFramework, currentFramework))
                {
                    foreach (var lib in item.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        var libPath = ExtractFile(packageReader, lib, tempPath);
                        packagePaths.Add(libPath);
                        anythingLoaded = true;
                    }
                }
                if (anythingLoaded) break;
            }

            // Extract possible native binaries from runtimes
            var runtimeItems = await packageReader.GetItemsAsync("runtimes", CancellationToken.None);
            var nativeLibraries = new List<string>();

            foreach (var runtimeItem in runtimeItems)
            {
                if (!runtimeItem.TargetFramework.Equals(currentFramework) && !runtimeItem.TargetFramework.IsAny)
                    continue;

                foreach (var runtimeFile in runtimeItem.Items.Where(i =>
                     i.Contains("native/") &&
                     (i.Contains("linux-x64") || i.Contains("win-x64")) &&
                     (i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                      || i.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                      || i.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))))
                {
                    var extractedPath = ExtractFile(packageReader, runtimeFile, tempPath);
                    nativeLibraries.Add(extractedPath);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var runtimePath = Path.Combine(tempPath, "runtimes", "linux-x64", "native");

                if (!packageName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
                    !packageName.Contains(".native.System", StringComparison.OrdinalIgnoreCase) &&
                    !packageName.StartsWith("NETStandard.", StringComparison.OrdinalIgnoreCase))
                {
                    var existingPaths = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")?.Split(':') ?? Array.Empty<string>();
                    if (!existingPaths.Contains(runtimePath))
                    {
                        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", runtimePath + ":" + string.Join(":", existingPaths));
                    }
                }
            }

            foreach (var path in nativeLibraries)
            {
                try
                {
                    NativeLibrary.Load(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to load native library: {path} - {ex.Message}");
                }
            }

            // Get dependencies and recursively process them
            var dependencies = await packageReader.GetPackageDependenciesAsync(CancellationToken.None);
            foreach (var dependencyGroup in dependencies)
            {
                foreach (var dependency in dependencyGroup.Packages)
                {
                    var depPackageName = dependency.Id;
                    var depVersionRange = dependency.VersionRange;
                    (FindPackageByIdResource? _, NuGetVersion? depSelectedVersion) = await GetPackageResourceAndVersion(depPackageName, depVersionRange, repositories, cache, logger);
                    try
                    {
                        await ResolvePackageAndDependencies(
                            depPackageName,
                            depSelectedVersion.ToNormalizedString(),
                            repositories,
                            cache,
                            logger,
                            packagePaths,
                            processedPackages
                        );
                    }
                    catch (Exception e)
                    {
                        logger.LogError($"Package load error ({depPackageName}): {e.Message} {e.InnerException?.Message}");
                    }
                }
            }
        }

        private bool IsCompatibleFramework(NuGetFramework targetFramework, NuGetFramework currentFramework)
        {
            // Basic check for .NET Standard or .NETCoreApp with version <= current
            return (targetFramework.Framework == ".NETStandard" || targetFramework.Framework == ".NETCoreApp")
                && targetFramework.Version <= currentFramework.Version;
        }

        private string ExtractFile(PackageArchiveReader packageReader, string filePath, string tempPath)
        {
            var absolutePath = Path.Combine(tempPath, filePath);
            var dir = Path.GetDirectoryName(absolutePath);
            if (dir == null)
                throw new Exception($"Invalid path while extracting: {absolutePath}");

            Directory.CreateDirectory(dir);

            // Extract the file if not already
            if (!File.Exists(absolutePath))
            {
                using var stream = packageReader.GetStream(filePath);
                using var fileStream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fileStream);
            }

            // You may optionally copy these to a final location, but it's not always necessary
            // as you can reference them directly from 'absolutePath'. E.g.:
            // var finalDest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.GetFileName(absolutePath));
            // if (!File.Exists(finalDest)) File.Copy(absolutePath, finalDest, true);

            return absolutePath;
        }
        public class Globals
        {
            public Dictionary<string, string> Parameters { get; set; }
            public RequestAccessor RequestAccessor { get; set; }
            public ResponseAccessor ResponseAccessor { get; set; }
            public Func<string, List<string>?, string> ExecuteAgent { get; set; }
            public Func<string, string> GetCacheValue { get; set; }
            public Func<string, string, int, string> SetCacheValue { get; set; }
            public ILogger<CsharpCodeAgent> Logger { get; set; }
        }

        /// <summary>
        /// Compiles the given C# code to a DLL on disk using Roslyn, referencing the specified assemblies.
        /// </summary>
        public class DynamicCompiler
        {
            public string CompileCodeToDll(string code, string outputDllPath, IEnumerable<string> references, string scriptCacheKey)
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(code);

                // Gather metadata references
                var loadedReferences = new List<MetadataReference>
                {
                    // System.Private.CoreLib
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    // System.Console
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    // System.Linq
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                };

                // Load references from the current AppDomain
                var assembliesReferences = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .Select(x => x.Location)
                    .ToList();

                // Add custom references
                assembliesReferences.AddRange(references);
                var processedFileNames = new List<string>();
                foreach (var reference in assembliesReferences)
                {
                    var fileName = Path.GetFileName(reference).ToLower();
                    if (File.Exists(reference) && !processedFileNames.Contains(fileName))
                    {
                        try
                        {
                            loadedReferences.Add(MetadataReference.CreateFromFile(reference));
                            processedFileNames.Add(fileName);
                        }
                        catch
                        {
                            // Skip any references that can't be processed
                        }
                    }
                }

                var compilation = CSharpCompilation.Create(
                    assemblyName: Path.GetFileNameWithoutExtension(outputDllPath),
                    syntaxTrees: new[] { syntaxTree },
                    references: loadedReferences,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                using var dllStream = new FileStream(outputDllPath, FileMode.Create);
                var result = compilation.Emit(dllStream);
                if (!result.Success)
                {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    throw new Exception($"Compilation failed:\n{errors}");
                }
                return outputDllPath;
            }
        }

        /// <summary>
        /// A collectible AssemblyLoadContext that can load an assembly, execute it, and be unloaded to free memory.
        /// </summary>
        public class CustomAssemblyLoadContext : AssemblyLoadContext, IDisposable
        {
            public CustomAssemblyLoadContext() : base(isCollectible: true) { }
            protected override Assembly Load(AssemblyName assemblyName) => null;
            public void Dispose()
            {
                Unload();
            }
        }

        /// <summary>
        /// Loads and executes a method from a DLL in a collectible AssemblyLoadContext, then unloads.
        /// </summary>
        public class DynamicAssemblyExecutor
        {
            public object Execute(string dllPath, string typeName, string methodName, object[] parameters, List<string>? references)
            {
                using var alc = new CustomAssemblyLoadContext();
                try
                {
                    var loadedAssembly = alc.Assemblies.FirstOrDefault(a => a.Location == dllPath);
                    var assembly = loadedAssembly ?? alc.LoadFromAssemblyPath(dllPath);
                    if (references != null)
                    {
                        var processedFileNames = new List<string>();
                        foreach (var reference in references)
                        {
                            var fileName = Path.GetFileName(reference).ToLower();
                            if (File.Exists(reference) && alc.Assemblies.All(a => a.Location != reference) && !processedFileNames.Contains(fileName))
                            {
                                alc.LoadFromAssemblyPath(reference);
                                processedFileNames.Add(fileName);
                            }
                        }
                    }
                    var type = assembly.GetType(typeName);
                    if (type == null)
                        throw new Exception($"Type '{typeName}' not found in assembly {dllPath}.");

                    var method = type.GetMethod(methodName);
                    if (method == null)
                        throw new Exception($"Method '{methodName}' not found on type '{typeName}'.");

                    var instance = Activator.CreateInstance(type);
                    return method.Invoke(instance, parameters);
                }
                finally
                {
                    alc.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }

            public MethodInfo? GetMethodInfo(string dllPath, string typeName, string methodName)
            {
                using var alc = new CustomAssemblyLoadContext();
                try
                {
                    var assembly = alc.LoadFromAssemblyPath(dllPath);
                    var type = assembly.GetType(typeName);
                    if (type == null) return null;

                    var method = type.GetMethod(methodName);
                    return method;
                }
                finally
                {
                    alc.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
        }
    }

    public interface ICsharpCodeAgent
    {
        Task AddAgent(AgentModel agent, Kernel kernel, List<string> pluginsInstructions);
        Task<string> DoCallWrapper(AgentModel agent, Dictionary<string, string> parameters);
    }
}
