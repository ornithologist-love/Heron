using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heron.Pregel.Tooling;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationArtifactsViewModel : ViewModelBase, IDisposable
{
    private const int TextPreviewLimit = 128 * 1024;
    private static readonly JsonSerializerOptions JsonPreview = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private string? _artifactPath;
    private IReadOnlyList<ArtifactRecord> _registered = [];
    private string _registeredJson = "";
    private CancellationTokenSource? _inventoryRead;
    private CancellationTokenSource? _previewRead;
    private bool _disposed;

    public ObservableCollection<AutomationArtifactItem> Files { get; } = [];
    public string FileCount => $"{Files.Count} 个文件";
    public bool HasMultipleFiles => Files.Count > 1;
    public bool HasPreviewStatus => Message.Length > 0 || PreviewNotice.Length > 0 || IsPreviewLoading;

    [ObservableProperty]
    private bool _hasArchive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _directory = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewStatus))] private string _message = "";
    [ObservableProperty] private AutomationArtifactItem? _selectedFile;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _showLineNumbers;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewStatus))] private string _previewNotice = "";
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMarkdownPreview), nameof(ShowTextPreview))] private bool _isMarkdown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowTextPreview))] private bool _isText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowMarkdownPreview), nameof(ShowTextPreview), nameof(MarkdownButtonText))]
    private bool _renderMarkdown = true;
    [ObservableProperty] private bool _isImage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewStatus))] private bool _isPreviewLoading;

    public bool ShowMarkdownPreview => IsMarkdown && RenderMarkdown;
    public bool ShowTextPreview => IsText || (IsMarkdown && !RenderMarkdown);
    public string MarkdownButtonText => RenderMarkdown ? "原文" : "渲染";

    [RelayCommand]
    private void ToggleMarkdown() => RenderMarkdown = !RenderMarkdown;

    public void SetArtifact(string? path)
        => SetArtifacts([], path);

    public void SetRun(AutomationRunRecord? run)
        => SetArtifacts(run?.Artifacts ?? [], run?.ArtifactPath);

    private void SetArtifacts(IReadOnlyList<ArtifactRecord> records, string? path)
    {
        var registeredJson = JsonSerializer.Serialize(records);
        if (_disposed || string.Equals(path, _artifactPath, StringComparison.Ordinal) && registeredJson == _registeredJson) return;
        _artifactPath = path;
        _registered = records;
        _registeredJson = registeredJson;
        HasArchive = records.Count > 0 || !string.IsNullOrWhiteSpace(path);
        _ = ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _inventoryRead?.Cancel();
        using var read = new CancellationTokenSource();
        _inventoryRead = read;
        var previous = SelectedFile?.Path;
        SelectedFile = null;
        Files.Clear();
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasMultipleFiles));
        Directory = "";
        Message = "";
        IsLoading = HasArchive;
        try
        {
            if (!HasArchive || _disposed)
            {
                if (!_disposed) Message = "本次运行尚未登记实际制品。";
                return;
            }
            var path = _artifactPath;
            var registered = _registered;
            var inventory = registered.Count > 0
                ? (Directory: "", Files: (IReadOnlyList<AutomationArtifactItem>)registered.Select(item =>
                    new AutomationArtifactItem(item.ArchivedPath ?? item.SourcePath, item.Name, item.Bytes ?? 0)
                    { Kind = item.Kind, Sha256 = item.Sha256, MachineName = item.MachineName, Deliveries = item.Deliveries,
                        HasKnownSize = item.Bytes.HasValue }).ToArray())
                : await Task.Run(() => ReadInventory(path!, read.Token), read.Token);
            read.Token.ThrowIfCancellationRequested();
            Directory = inventory.Directory;
            foreach (var item in inventory.Files) Files.Add(item);
            OnPropertyChanged(nameof(FileCount));
            OnPropertyChanged(nameof(HasMultipleFiles));
            SelectedFile = Files.FirstOrDefault(item => string.Equals(item.Path, previous ?? path, StringComparison.OrdinalIgnoreCase))
                ?? Files.FirstOrDefault(item => item.Extension is ".md" or ".markdown") ?? Files.FirstOrDefault();
            if (Files.Count == 0) Message = "这条记录没有登记实际制品；运行目录本身不作为制品。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!read.IsCancellationRequested) Message = "无法读取制品：" + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_inventoryRead, read)) { _inventoryRead = null; IsLoading = false; }
        }
    }

    partial void OnSelectedFileChanged(AutomationArtifactItem? value)
    {
        if (value is { IsLocal: true }) Directory = Path.GetDirectoryName(value.Path) ?? "";
        else if (value != null) Directory = "";
        _ = LoadPreviewAsync(value);
    }

    private async Task LoadPreviewAsync(AutomationArtifactItem? file)
    {
        _previewRead?.Cancel();
        using var read = new CancellationTokenSource();
        _previewRead = read;
        ClearPreview();
        IsPreviewLoading = file != null;
        try
        {
            if (file == null || _disposed) return;
            if (!file.IsLocal) { PreviewNotice = "制品位于 " + file.MachineName + "，本地预览不可用。"; return; }
            if (!File.Exists(file.Path)) { PreviewNotice = "文件已移动或删除，无法预览。"; return; }
            if (file.Extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
            {
                if (new FileInfo(file.Path).Length > 32 * 1024 * 1024)
                { PreviewNotice = "图片较大，请打开文件查看。"; return; }
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(file.Path);
                    return Bitmap.DecodeToWidth(stream, 1600);
                }, read.Token);
                if (read.IsCancellationRequested) { bitmap.Dispose(); return; }
                PreviewImage = bitmap;
                IsImage = true;
            }
            else if (file.IsText)
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                var buffer = new char[TextPreviewLimit + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    var length = await reader.ReadAsync(buffer.AsMemory(count), read.Token);
                    if (length == 0) break;
                    count += length;
                }
                read.Token.ThrowIfCancellationRequested();
                if (Array.IndexOf(buffer, '\0', 0, count) >= 0)
                { PreviewNotice = "文件包含二进制内容，请打开文件查看。"; return; }
                var text = new string(buffer, 0, Math.Min(count, TextPreviewLimit));
                if (file.Extension == ".json" && count <= TextPreviewLimit)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(text);
                        text = JsonSerializer.Serialize(json.RootElement, JsonPreview);
                    }
                    catch (JsonException) { /* Incomplete or invalid JSON remains available as source text. */ }
                }
                PreviewText = text;
                IsMarkdown = file.Extension is ".md" or ".markdown";
                IsText = !IsMarkdown;
                if (count > TextPreviewLimit) PreviewNotice = "仅预览前 128K 字符，打开文件可查看完整内容。";
                else if (count == 0) PreviewNotice = "文件为空。";
            }
            else PreviewNotice = "此格式暂不支持预览，可用系统应用打开文件。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!read.IsCancellationRequested) PreviewNotice = "无法预览：" + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_previewRead, read)) { _previewRead = null; IsPreviewLoading = false; }
        }
    }

    private void ClearPreview()
    {
        // A deferred Image binding may still hold the previous bitmap. Avalonia's
        // ref-count finalizer releases it after those references are gone.
        PreviewImage = null;
        PreviewText = "";
        PreviewNotice = "";
        IsMarkdown = IsText = IsImage = false;
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedFile is not { IsLocal: true } file) return;
        try
        {
            if (!File.Exists(file.Path)) throw new FileNotFoundException("文件已移动或删除");
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception error) { PreviewNotice = "无法打开文件：" + error.Message; }
    }

    [RelayCommand]
    private void OpenDirectory()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) throw new DirectoryNotFoundException("归档目录已移动或删除");
            Process.Start(new ProcessStartInfo(Directory) { UseShellExecute = true });
        }
        catch (Exception error) { Message = "无法打开目录：" + error.Message; }
    }

    private static (string Directory, IReadOnlyList<AutomationArtifactItem> Files) ReadInventory(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var directory = System.IO.Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("归档清单为空");
            if (manifest.Schema != 1 || manifest.Artifacts == null) throw new InvalidDataException("归档清单格式不支持");
            var root = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var files = new List<AutomationArtifactItem>();
            foreach (var item in manifest.Artifacts)
            {
                token.ThrowIfCancellationRequested();
                var saved = Path.GetFullPath(Path.Combine(directory, item.Name));
                if (Path.IsPathRooted(item.Name) || !saved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("归档清单中的文件路径无效");
                files.Add(new(saved, item.Name, item.Bytes)
                {
                    Kind = item.Kind, Sha256 = item.Sha256,
                    Deliveries = [new("本地归档", "succeeded", saved)]
                });
            }
            return (directory, files);
        }
        // Older runs can point directly to a single result without an archive manifest.
        if (!System.IO.Directory.Exists(path))
        {
            var file = new FileInfo(path);
            return (directory, file.Exists ? [new(path, file.Name, file.Length)] : []);
        }
        return (directory, []);
    }

    public void Dispose()
    {
        _disposed = true;
        _inventoryRead?.Cancel();
        _previewRead?.Cancel();
        ClearPreview();
    }
}

public sealed record AutomationArtifactItem(string Path, string Name, long Bytes)
{
    public string Kind { get; init; } = "file";
    public bool HasKnownSize { get; init; } = true;
    public string? Sha256 { get; init; }
    public string MachineName { get; init; } = Environment.MachineName;
    public bool IsLocal => MachineName.Length == 0 || MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<ArtifactDelivery> Deliveries { get; init; } = [];
    public IReadOnlyList<AutomationArtifactDeliveryRow> DeliveryRows => Deliveries.Select(item =>
        new AutomationArtifactDeliveryRow(item.Destination, item.State, item.Location ?? "", item.Error ?? "")).ToArray();
    public string Extension => System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public string Size => !HasKnownSize ? "大小未登记" : Bytes < 1024 ? $"{Bytes} B" : Bytes < 1024 * 1024 ? $"{Bytes / 1024d:0.#} KB" : $"{Bytes / (1024d * 1024):0.#} MB";
    public string Format => Extension.Length > 1 ? Extension[1..].ToUpperInvariant() : "文件";
    public string KindLabel => Kind switch { "report" => "报告", "log" => "日志", "stat" => "统计数据", _ => "文件" };
    public string Metadata => $"{KindLabel} · {Format} · {Size} · {MachineName}";
    public bool IsText => Extension is ".md" or ".markdown" or ".txt" or ".log" or ".json" or ".jsonl"
        or ".yaml" or ".yml" or ".xml" or ".csv" or ".ini" or ".toml" or ".cs" or ".csx" or ".ps1" or ".sh" or ".html" or ".htm";
}

public sealed record AutomationArtifactDeliveryRow(string Destination, string State, string Location, string Error)
{
    public string Status => State switch { "uploading" => "上传中", "succeeded" => "已完成", "failed" => "失败", "canceled" => "已取消", _ => State };
    public bool HasLocation => Location.Length > 0;
    public bool HasError => Error.Length > 0;
}
