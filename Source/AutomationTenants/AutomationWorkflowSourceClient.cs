using Heron.Pregel.Automation;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heron.Pregel.Tooling;
using Heron.AutomationTenants.Contracts;

namespace Heron.AutomationTenants;

public sealed record WorkflowPreparedUpload(WorkflowUploadRequest Request, byte[] Source, byte[] Shared, int FileCount)
{
    public string WorkflowContentRevision { get; init; } = "";
}

/// <summary>Explicit source submission and the authenticated official release entry point.</summary>
public sealed class AutomationWorkflowSourceClient(string server, Action<HttpRequestHeaders>? authenticate = null) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = Create(server, authenticate);
    private readonly Lazy<HttpClient> _upload = new(() => Create(server, authenticate, TimeSpan.FromMinutes(15)));
    private static HttpClient Create(string server, Action<HttpRequestHeaders>? authenticate, TimeSpan? timeout = null)
    {
        var uri = new Uri(server.TrimEnd('/') + "/");
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("服务器地址必须是 HTTP(S) 服务基址");
        var client = new HttpClient { BaseAddress = uri, Timeout = timeout ?? TimeSpan.FromMinutes(3) };
        authenticate?.Invoke(client.DefaultRequestHeaders);
        return client;
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken token)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(token);
                string? error = null;
                try { using var document = JsonDocument.Parse(text); if (document.RootElement.TryGetProperty("error", out var value)) error = value.GetString(); }
                catch (JsonException) { }
                throw new HttpRequestException(error ?? "流程上传服务返回 HTTP " + (int)response.StatusCode, null, response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, token) ?? throw new IOException("流程上传结果为空");
        }
    }
    public async Task<WorkflowSourceContext> ContextAsync(string taskId, CancellationToken token = default) =>
        await Read<WorkflowSourceContext>(await _http.GetAsync("api/workflow-sources/tasks/" + Uri.EscapeDataString(taskId), token), token);
    public async Task<WorkflowSourceTarget[]> TargetsAsync(string tenant, bool machineUser, CancellationToken token = default) =>
        await Read<WorkflowSourceTarget[]>(await _http.GetAsync("api/workflow-sources/targets?tenant_id=" + Uri.EscapeDataString(tenant)
            + "&machine_name=" + Uri.EscapeDataString(Environment.MachineName) + "&machine_user=" + machineUser.ToString().ToLowerInvariant(), token), token);

    public static string DefaultSharedDirectory(string sourceDirectory)
    {
        var adjacent = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "Shared"));
        return Directory.Exists(adjacent) ? adjacent : RemoteAutomationEnvironment.Current.SharedRoot();
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("脚本上传不跟随目录链接");
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) is ".source.json" or ".catalog.json" or ".workflow-source.json" or ".baseline.json") continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("脚本上传不跟随文件链接");
            yield return file;
        }
        foreach (var folder in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            if (!Path.GetFileName(folder).StartsWith('.') && Path.GetFileName(folder) is not ("bin" or "obj" or "node_modules" or "__pycache__"))
                foreach (var file in SourceFiles(folder)) yield return file;
    }

    public static string? CaptureContentRevision(string path, string loadedEntryHash, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)!;
        var entry = Path.GetFileName(path);
        var entryMatches = false;
        IEnumerable<(string Path, string Sha256)> HashFiles(string root, string prefix)
        {
            var count = 0; long total = 0;
            foreach (var file in SourceFiles(root))
            {
                token.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(file);
                total += stream.Length;
                ScriptSourceContent.ValidateFileSize(file, stream.Length, total, ++count);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                if (file == path) entryMatches = hash == loadedEntryHash;
                yield return (prefix + Path.GetRelativePath(root, file).Replace('\\', '/'), hash);
            }
        }
        try
        {
            var source = ScriptSourceContent.ComputeManifest(entry, HashFiles(directory, ""));
            if (!entryMatches) return null;
            var shared = ScriptSourceContent.ComputeManifest("", HashFiles(DefaultSharedDirectory(directory), "Shared/"));
            return source + ":" + shared;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public static byte[] Pack(string directory, string id)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory) || !ScriptSourceContent.IsValidId(id)) throw new ArgumentException("请选择有效的流程目录");
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            var count = 0; long total = 0;
            foreach (var file in SourceFiles(directory))
            {
                using var input = File.OpenRead(file);
                total += input.Length;
                ScriptSourceContent.ValidateFileSize(file, input.Length, total, ++count);
                var entry = archive.CreateEntry(id + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open(); input.CopyTo(output);
            }
        }
        ScriptSourceContent.ValidateArchiveSize(id, bytes.Length);
        return bytes.ToArray();
    }
    public async Task<WorkflowPreparedUpload> PrepareAsync(string tenant, string taskId, string title, string definitionId,
        string directory, string entryFile, string sharedDirectory, bool newTask, bool machineUser, string? validationRun,
        CancellationToken token = default)
    {
        var context = await ContextAsync(taskId, token);
        var source = await Task.Run(() => Pack(directory, context.SourceId), token);
        var shared = await Task.Run(() => Pack(sharedDirectory, "Shared"), token);
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(Path.Combine(directory, entryFile))).Replace('\\', '/');
        using var sourceZip = new ZipArchive(new MemoryStream(source)); using var sharedZip = new ZipArchive(new MemoryStream(shared));
        var entry = context.SourceId + "/" + relative;
        var request = new WorkflowUploadRequest(tenant, taskId, context.SourceId, title, entry, context.Revision,
            context.SharedRevision, ScriptSourceContent.Compute(sourceZip, context.SourceId, entry), ScriptSourceContent.Compute(sharedZip, "Shared", ""),
            Environment.MachineName, machineUser, definitionId, validationRun, newTask, Guid.NewGuid().ToString("N"));
        if (string.IsNullOrWhiteSpace(context.SharedContentRevision))
            throw new InvalidOperationException("服务器尚未支持自动核对 Shared，请更新服务器后重试。");
        if (request.SharedRevision != context.SharedContentRevision)
            throw new InvalidOperationException("普通上传不支持 Shared 变更，请使用云端当前 Shared 后重新上传。");
        var content = ScriptSourceContent.ComputeManifest(relative, sourceZip.Entries.Select(file =>
        {
            using var stream = file.Open();
            return (file.FullName[(context.SourceId.Length + 1)..], Convert.ToHexString(SHA256.HashData(stream)));
        }));
        return new(request, source, shared, sourceZip.Entries.Count + sharedZip.Entries.Count)
        { WorkflowContentRevision = content + ":" + request.SharedRevision };
    }
    public async Task<WorkflowSubmission> SubmitAsync(WorkflowPreparedUpload prepared, CancellationToken token = default)
    {
        ScriptSourceContent.ValidateArchiveSize(prepared.Request.SourceId, prepared.Source.LongLength);
        ScriptSourceContent.ValidateArchiveSize("Shared", prepared.Shared.LongLength);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(prepared.Request, Json), Encoding.UTF8, "application/json"), "metadata");
        form.Add(new ByteArrayContent(prepared.Source), "source", "source.zip");
        form.Add(new ByteArrayContent(prepared.Shared), "shared", "shared.zip");
        return await Read<WorkflowSubmission>(await _upload.Value.PostAsync("api/workflow-sources/submissions", form, token), token);
    }

    public async Task<WorkflowOfficialPublishResult> PublishOfficialAsync(WorkflowSourceManifest manifest, string directory,
        CancellationToken token = default)
    {
        const string endpoint = "api/workflow-sources/official-release";
        using var form = new MultipartFormDataContent();
        var request = new WorkflowOfficialPublishRequest("", manifest);
        form.Add(new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json"), "metadata");
        foreach (var package in manifest.Scripts.Prepend(manifest.Shared))
            form.Add(new StreamContent(File.OpenRead(Path.Combine(directory, package.Archive))), package.Id, "scripts.zip");
        return await Read<WorkflowOfficialPublishResult>(await _upload.Value.PostAsync(endpoint, form, token), token);
    }

    public void Dispose() { _http.Dispose(); if (_upload.IsValueCreated) _upload.Value.Dispose(); }
}
