using System.Diagnostics;
using System.Reflection;
using System.Text;
using Heron.Pregel.Tooling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Heron.Pregel.Scripting;

/// <summary>Loads trusted CSX source into a compiled graph. Does not execute graph nodes.</summary>
public static class PregelScript
{
    public static Task<DynamicPregelGraph> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [], [], cancellationToken);

    /// <summary>
    /// Rewrites script text before compiling, so an embedding product can keep older scripts working after an
    /// API rename. It applies to entry scripts and <c>#load</c> files, and never to what revisions are hashed from.
    /// </summary>
    public static Func<string, string>? SourceTransform { get; set; }

    public static string TransformSource(string source) => SourceTransform?.Invoke(source) ?? source;

    public static SourceReferenceResolver SourceResolver(string directory) =>
        new TransformingSourceResolver(ScriptSourceResolver.Default.WithBaseDirectory(directory));

    public static async Task<DynamicPregelGraph> LoadAsync(string path, IEnumerable<Assembly> references,
        IEnumerable<string>? imports = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var source = TransformSource(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false));
        var options = ScriptOptions.Default
            .WithFilePath(fullPath)
            .AddReferences(typeof(Pregel).Assembly, typeof(ProcessExecution).Assembly,
                typeof(PregelScriptGlobals).Assembly, typeof(ProcessStartInfo).Assembly,
                typeof(System.ComponentModel.DisplayNameAttribute).Assembly)
            .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic",
                "System.Threading", "System.Threading.Tasks", "System.Diagnostics",
                "Heron.Pregel", "Heron.Pregel.Tooling", "System.ComponentModel")
            .WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(directory))
            .WithSourceResolver(SourceResolver(directory));
        options = options.AddReferences(references);
        if (imports != null) options = options.AddImports(imports);
        var state = await CSharpScript.RunAsync<object>(source, options,
            new PregelScriptGlobals(directory), typeof(PregelScriptGlobals), cancellationToken)
            .ConfigureAwait(false);
        var graph = state.ReturnValue;
        if (graph != null && DynamicPregelGraph.IsGraphType(graph.GetType()))
            return DynamicPregelGraph.Adapt(graph);
        throw new ArgumentException($"{fullPath}: CSX 必须返回 Compile() 生成的 PregelGraph<TInput, TOutput>。");
    }
}

internal sealed class TransformingSourceResolver(SourceReferenceResolver inner) : SourceReferenceResolver
{
    public override string? NormalizePath(string path, string? baseFilePath) => inner.NormalizePath(path, baseFilePath);

    public override string? ResolveReference(string path, string? baseFilePath) => inner.ResolveReference(path, baseFilePath);

    public override Stream OpenRead(string resolvedPath)
    {
        using var reader = new StreamReader(inner.OpenRead(resolvedPath));
        return new MemoryStream(Encoding.UTF8.GetBytes(PregelScript.TransformSource(reader.ReadToEnd())));
    }

    public override bool Equals(object? other) => other is TransformingSourceResolver resolver && inner.Equals(resolver.Inner);

    public override int GetHashCode() => inner.GetHashCode();

    private SourceReferenceResolver Inner => inner;
}

/// <summary>Conveniences available to CSX graph factories and their named node methods.</summary>
public sealed class PregelScriptGlobals(string scriptDirectory)
{
    public string ScriptDirectory { get; } = scriptDirectory;

    /// <summary>Runs an executable without shell interpolation, relative to the CSX directory.</summary>
    public Task RunAsync(PregelNodeContext context, string executable, string[] arguments,
        CancellationToken cancellationToken = default, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory is null ? ScriptDirectory : Path.GetFullPath(workingDirectory, ScriptDirectory),
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return ProcessExecution.RunCheckedAsync(start, context, cancellationToken);
    }
}
