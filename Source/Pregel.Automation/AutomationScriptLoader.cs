using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Heron.Pregel;
using Heron.Pregel.Scripting;
using Heron.Pregel.Tooling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Heron.Pregel.Automation;

/// <summary>Explicitly evaluates a user-selected script. Directory discovery never calls this loader.</summary>
internal static class AutomationScriptLoader
{
    // Permission and transient IO errors do not establish that the saved source was removed.
    internal static bool SourceMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // A graph entry and its adjacent Client template are one script, not two registrations.
    internal static string EntryKey(string path) => path.EndsWith(".Template.csx", StringComparison.OrdinalIgnoreCase)
        ? Path.GetFullPath(path)[..^".Template.csx".Length] + ".csx" : Path.GetFullPath(path);

    internal static string PreferredPath(string path)
    {
        var adapter = Path.ChangeExtension(EntryKey(path), ".Template.csx");
        return File.Exists(adapter) ? adapter : Path.GetFullPath(path);
    }

    public static async Task<AutomationScriptInstance> LoadAsync(string path, string id, string stateRoot,
        Assembly[] references, CancellationToken token)
    {
        path = PreferredPath(path);
        var logicalPath = path;
        using var sourceLease = AutomationRuntime.ScriptSource?.AcquireReadLease(logicalPath);
        var frozen = AutomationRuntime.FreezeScript(logicalPath, stateRoot, token);
        path = frozen.Path;
        var directory = Path.GetDirectoryName(path)!;
        var options = ScriptOptions.Default.WithFilePath(path)
            .AddReferences(typeof(IAutomationTemplate).Assembly,
                typeof(System.ComponentModel.DisplayNameAttribute).Assembly, typeof(Pregel).Assembly,
                typeof(PregelScript).Assembly, typeof(ProcessExecution).Assembly, typeof(ProcessStartInfo).Assembly,
                typeof(JsonElement).Assembly)
            .AddReferences(AutomationRuntime.ScriptReferences)
            .AddReferences(references)
            .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading",
                "System.Threading.Tasks", "System.Diagnostics", "Heron.Pregel", "Heron.Pregel.Tooling",
                "Heron.Pregel.Automation", "System.ComponentModel")
            .AddImports(AutomationRuntime.ScriptImports)
            .WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(directory))
            .WithSourceResolver(PregelScript.SourceResolver(directory));
        var bytes = await File.ReadAllBytesAsync(path, token);
        using var reader = new StreamReader(new MemoryStream(bytes));
        var source = PregelScript.TransformSource(await reader.ReadToEndAsync(token));
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var loader = new ScriptLoadContext(references);
        IAutomationTemplate? template = null;
        try
        {
            // Roslyn's interactive loader is non-collectible and cannot reference private plugin assemblies.
            // Compile the same submission, then load it in a collectible context using the provider instances.
            var compilation = CSharpScript.Create<object>(source, options, typeof(PregelScriptGlobals)).GetCompilation();
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image, cancellationToken: token);
            if (!emitted.Success)
                throw new CompilationErrorException(string.Join(Environment.NewLine,
                    emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)), emitted.Diagnostics);
            loader.RegisterPaths(compilation.References);
            image.Position = 0;
            var assembly = loader.LoadFromStream(image);
            var entry = compilation.GetEntryPoint(token) ?? throw new InvalidOperationException("脚本缺少入口。");
            var typeName = entry.ContainingNamespace.IsGlobalNamespace ? entry.ContainingType.MetadataName
                : entry.ContainingNamespace.ToDisplayString() + "." + entry.ContainingType.MetadataName;
            var factory = assembly.GetType(typeName, throwOnError: true)!
                .GetMethod(entry.MetadataName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .CreateDelegate<Func<object[], Task<object>>>();
            token.ThrowIfCancellationRequested();
            var returned = await factory([new PregelScriptGlobals(directory), null!]).ConfigureAwait(false);
            template = returned is IAutomationTemplate adapter ? adapter
                : returned is { } graph && DynamicPregelGraph.IsGraphType(graph.GetType())
                    ? AutomationGraphScript.Create(DynamicPregelGraph.Adapt(graph), path, id, stateRoot)
                : throw new InvalidOperationException("脚本须返回已编译的 PregelGraph<TInput, TOutput> 或 IAutomationTemplate；辅助文件请由入口脚本 #load。");
            if (template is IAutomationGraphRuntimeConsumer graphTemplate)
                graphTemplate.ConfigureGraphRuntime(id, path, stateRoot);
            var inputPath = Path.Combine(directory, "Input.json");
            if (template is IAutomationInputSchemaConsumer consumer && File.Exists(inputPath))
                consumer.ConfigureInput(AutomationScriptInput.Parse(await File.ReadAllTextAsync(inputPath, token).ConfigureAwait(false)));
            token.ThrowIfCancellationRequested();
            var contentRevision = frozen.Version?.ActualSha256
                ?? AutomationRuntime.ScriptContentRevision?.Invoke(logicalPath, hash, token) ?? hash;
            return new AutomationScriptInstance(new AutomationScriptTemplate(id, logicalPath, template, path, frozen.Version)
            { WorkflowContentRevision = contentRevision }, hash, loader);
        }
        catch
        {
            await AutomationScriptInstance.ReleaseAsync(template);
            loader.Dispose();
            throw;
        }
    }

    private sealed class ScriptLoadContext(IEnumerable<Assembly> providers)
        : AssemblyLoadContext("automation-script:" + Guid.NewGuid().ToString("N"), isCollectible: true), IDisposable
    {
        private readonly Dictionary<string, Assembly> _providers = providers.Distinct()
            .ToDictionary(assembly => assembly.GetName().Name!, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterPaths(IEnumerable<MetadataReference> references)
        {
            foreach (var reference in references.OfType<PortableExecutableReference>())
                if (reference.FilePath is { } path)
                    _paths.TryAdd(AssemblyName.GetAssemblyName(path).Name!, path);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (_providers.TryGetValue(name.Name!, out var provider))
            {
                if (!string.Equals(provider.FullName, name.FullName, StringComparison.OrdinalIgnoreCase))
                    throw new FileLoadException($"Script provider assembly mismatch: {name.FullName}");
                return provider;
            }
            // Shared contracts and framework assemblies retain their host identity.
            try { return Default.LoadFromAssemblyName(name); }
            catch (FileNotFoundException)
            {
                return _paths.TryGetValue(name.Name!, out var path) ? LoadFromAssemblyPath(path) : null;
            }
        }

        public void Dispose() => Unload();
    }
}

/// <summary>Path-owned identity prevents a script from replacing a built-in template.</summary>
public sealed class AutomationScriptTemplate(string id, string path, IAutomationTemplate inner,
    string? frozenPath = null, AutomationExecutionVersion? executionVersion = null) : IAutomationReviewTemplate, IAutomationReexecutionTemplate, IAutomationGraphTemplate, IAutomationSubgraphResultReader
{
    internal string? WorkflowContentRevision { get; init; }
    public async Task<AutomationRunResult?> ReadCompletedResultAsync(AutomationRunRecord record, CancellationToken token)
    {
        using var sourceLease = AcquireSourceLease();
        return inner is IAutomationSubgraphResultReader reader
            ? await reader.ReadCompletedResultAsync(record, token).ConfigureAwait(false) : null;
    }
    public CompiledPregelGraph CompiledGraph => inner is IAutomationGraphTemplate graph ? graph.CompiledGraph
        : throw new InvalidOperationException("此模板不提供持久化 Pregel 图");
    public IAutomationTemplate BindSubgraph(PregelSubgraphExecutionRequest request)
    {
        using var sourceLease = AcquireSourceLease();
        return inner is IAutomationGraphTemplate graph
            ? new AutomationScriptTemplate(id, path, graph.BindSubgraph(request), frozenPath, executionVersion)
                { WorkflowContentRevision = WorkflowContentRevision }
            : throw new InvalidOperationException("此模板不支持完整子图执行");
    }
    public bool SupportsReexecution => inner is IAutomationReexecutionTemplate { SupportsReexecution: true };
    public Task<PregelReexecutionPreview> PreviewReexecutionAsync(string sourceRunId, PregelReexecutionRequest request,
        CancellationToken token) => inner is IAutomationReexecutionTemplate reexecution
        ? reexecution.PreviewReexecutionAsync(sourceRunId, request, token)
        : throw new InvalidOperationException("此模板不支持从检查点派生运行");
    internal const string SourceChangedMessage = "脚本源已更新，请重新加载脚本及 Shared";
    private readonly string _sourceStamp = AutomationRuntime.ScriptSource?.InstalledStamp(path) ?? "";

    internal bool SourceMissing => AutomationScriptLoader.SourceMissing(path);
    public AutomationExecutionVersion? ExecutionVersion => executionVersion;
    public bool SourceChanged => executionVersion != null
        ? executionVersion.BundleSha256 != AutomationRuntime.ScriptVersion?.Invoke(path)?.BundleSha256
        : _sourceStamp != (AutomationRuntime.ScriptSource?.InstalledStamp(path) ?? "");

    public string Id => id;
    public string DefinitionId => inner.Id;
    public string Title => inner.Title;
    public string Description => inner.Description + " · " + Path.GetFileName(path);
    public string Icon => inner.Icon;
    public IReadOnlyList<AutomationField> Fields => inner.Fields;
    public Task<IReadOnlyList<AutomationChoice>?> ListFieldChoicesAsync(AutomationTask task, string fieldKey, CancellationToken token) =>
        inner.ListFieldChoicesAsync(task, fieldKey, token);
    public string? Validate(AutomationTask task) => SourceChanged
        ? SourceChangedMessage
        : File.Exists(path) ? inner.Validate(task) : "脚本文件已不存在，请恢复文件或选择新的脚本";
    public IEnumerable<AutomationRuleViolation> CheckRules(AutomationTask task, IReadOnlyList<AutomationTask> siblings) => inner.CheckRules(task, siblings);
    public IReadOnlyList<string> ResourceKeys(AutomationTask task) => inner.ResourceKeys(task);
    public IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task) => inner.PlanSteps(task);
    public string? RenderParameters(AutomationTask task) => inner.RenderParameters(task);
    public async Task<AutomationRunResult> RunAsync(AutomationRunContext context, CancellationToken token)
    {
        using var sourceLease = AcquireSourceLease();
        using var aiScope = AutomationRuntime.EnterScriptScope(context.Task.Id, Path.GetDirectoryName(Path.GetFullPath(frozenPath ?? path))!);
        return await inner.RunAsync(context, token);
    }
    public bool SupportsChatTransfer => inner is IAutomationReviewTemplate { SupportsChatTransfer: true };
    public Task<AutomationChatSession?> GetChatSessionAsync(AutomationReview review, CancellationToken token) =>
        inner is IAutomationReviewTemplate reviewer ? reviewer.GetChatSessionAsync(review, token) : Task.FromResult(review.ChatSession);
    public async Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review, AutomationReviewDecision decision,
        CancellationToken token)
    {
        using var sourceLease = AcquireSourceLease();
        using var aiScope = AutomationRuntime.EnterScriptScope(context.Task.Id, Path.GetDirectoryName(Path.GetFullPath(frozenPath ?? path))!);
        return inner is IAutomationReviewTemplate reviewer ? await reviewer.ReviewAsync(context, review, decision, token)
            : throw new InvalidOperationException("此脚本不支持审核恢复");
    }
    private IDisposable? AcquireSourceLease()
    {
        var lease = AutomationRuntime.ScriptSource?.AcquireReadLease(path);
        // Admission validated the live copy; an admitted run continues on its immutable snapshot.
        if (executionVersion != null && frozenPath != null)
        {
            AutomationRuntime.ScriptSnapshotUsed?.Invoke(frozenPath);
            return lease;
        }
        if (!SourceChanged) return lease;
        lease?.Dispose();
        throw new InvalidOperationException(SourceChangedMessage);
    }
    public void OnTaskDeleted(AutomationTask task) => inner.OnTaskDeleted(task);

    public async ValueTask DisposeAsync()
    {
        if (inner is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (inner is IDisposable disposable) disposable.Dispose();
    }
}
