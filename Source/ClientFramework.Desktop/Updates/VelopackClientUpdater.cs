using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;

namespace Heron.ClientFramework.Infrastructure;

public sealed record ClientUpdateOptions
{
    public required string FeedUrl { get; init; }
    public required string InstallerDownloadUrl { get; init; }
    public required string LatestVersionApiPath { get; init; }
    public Uri? ServerBaseAddress { get; init; }
    public Func<string>? GetCurrentVersion { get; init; }
    public Func<string, string>? FormatDisplayVersion { get; init; }
    public bool SkipCheck { get; init; }
    public HttpClient? HttpClient { get; init; }
}

public sealed class ClientUpdateCheckResult
{
    public string Version { get; set; } = "";
    public string LatestVersion => Version;
    public string DownloadUrl { get; set; } = "";
    public long? FileSize { get; set; }
    public string ReleaseNotes { get; set; } = "";
    public bool IsAutomaticUpdate { get; set; }
}

/// <summary>
///     Checks, downloads, and stages Velopack releases for installed hosts, with an HTTP <c>latest.json</c>
///     discovery fallback for uninstalled/legacy executables.
/// </summary>
public sealed class VelopackClientUpdater : IDisposable
{
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<VelopackClientUpdater>();
    private readonly ClientUpdateOptions _options;
    private readonly HttpClient _httpClient;
    private readonly UpdateManager? _manager;
    private readonly bool _ownsHttpClient;
    private UpdateInfo? _pendingUpdate;

    public VelopackClientUpdater(ClientUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        UpdateFeedUrl = options.FeedUrl;
        InstallerDownloadUrl = options.InstallerDownloadUrl;
        LatestVersionApiPath = options.LatestVersionApiPath;

        if (options.HttpClient != null)
        {
            _httpClient = options.HttpClient;
            _ownsHttpClient = false;
            IsInstalled = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                BaseAddress = options.ServerBaseAddress,
                Timeout = TimeSpan.FromSeconds(10),
            };
            _ownsHttpClient = true;
            _manager = new UpdateManager(options.FeedUrl);
            IsInstalled = _manager.IsInstalled;
        }
    }

    public bool IsInstalled { get; }
    public string UpdateFeedUrl { get; }
    public string InstallerDownloadUrl { get; }
    public string LatestVersionApiPath { get; }

    public string PendingFullPackagePath => _pendingUpdate == null
        ? throw new InvalidOperationException("没有待应用的本体更新包。")
        : Path.Combine(Velopack.Locators.VelopackLocator.Current.PackagesDir!, _pendingUpdate.TargetFullRelease.FileName);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public async Task<ClientUpdateCheckResult?> CheckForUpdateAsync(string? currentVersion = null)
    {
        if (_options.SkipCheck) return null;
        if (IsInstalled && _manager != null)
        {
            _pendingUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pendingUpdate == null)
            {
                Logger.Information("Update check: installed, no update available");
                return null;
            }

            var release = _pendingUpdate.TargetFullRelease;
            Logger.Information("Update check: installed, target={Target} size={Size}", release.Version, release.Size);
            var rawVersion = release.Version.ToString();
            return new ClientUpdateCheckResult
            {
                Version = _options.FormatDisplayVersion != null ? _options.FormatDisplayVersion(rawVersion) : rawVersion,
                FileSize = release.Size,
                ReleaseNotes = release.NotesMarkdown ?? "",
                IsAutomaticUpdate = true,
            };
        }

        currentVersion ??= _options.GetCurrentVersion?.Invoke() ?? "0.0.0";
        using var response = await _httpClient.GetAsync(
            LatestVersionApiPath + "?localVersion=" + Uri.EscapeDataString(currentVersion)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var latestInfo = JsonSerializer.Deserialize<LatestVersionInfo>(content);
        if (latestInfo == null) return null;

        if (!TryParseVersion(latestInfo.Version, out var latestVersion)
            || !TryParseVersion(currentVersion, out var localVersion)
            || latestVersion <= localVersion)
            return null;

        var platformInfo = latestInfo.GetPlatformInfo(GetCurrentPlatform());
        var downloadUrl = platformInfo?.EffectiveDownloadUrl ?? latestInfo.EffectiveDownloadUrl;
        return new ClientUpdateCheckResult
        {
            Version = latestInfo.Version,
            DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? InstallerDownloadUrl : ResolveUrl(downloadUrl),
            FileSize = platformInfo?.EffectiveFileSize ?? latestInfo.EffectiveFileSize,
            ReleaseNotes = latestInfo.EffectiveReleaseNotes ?? "",
            IsAutomaticUpdate = false,
        };
    }

    public async Task DownloadAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled || _manager == null)
            throw new InvalidOperationException("当前程序不是通过 Windows 安装程序安装的，无法执行应用内自动更新。");
        if (_pendingUpdate == null)
            throw new InvalidOperationException("没有待下载的更新，请先检查更新。");

        var targetVersion = _pendingUpdate.TargetFullRelease.Version.ToString();
        Logger.Information("Update download begin: target={Target}", targetVersion);
        try
        {
            await _manager.DownloadUpdatesAsync(_pendingUpdate, progress, cancellationToken).ConfigureAwait(false);
            Logger.Information("Update download end: target={Target}", targetVersion);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update download failed: target={Target}", targetVersion);
            throw;
        }
    }

    public void BeginApplyAfterExit()
    {
        if (!IsInstalled || _manager == null)
            throw new InvalidOperationException("当前程序不是通过 Windows 安装程序安装的，无法自动安装更新。");

        var targetVersion = _pendingUpdate?.TargetFullRelease.Version.ToString() ?? "unknown";
        var currentVersion = _options.GetCurrentVersion?.Invoke() ?? "";
        Logger.Information("Update apply issue: target={Target} currentVersion={CurrentVersion}",
            targetVersion, currentVersion);
        try
        {
            _manager.WaitExitThenApplyUpdates(
                _pendingUpdate?.TargetFullRelease ?? throw new InvalidOperationException("没有已确认的本体更新目标"),
                false,
                true,
                null);
            Logger.Information("Update apply issued: target={Target}", targetVersion);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update apply issue failed: target={Target}", targetVersion);
            throw;
        }
    }

    public static bool TryParseVersion(string version, out Version result)
    {
        var cleaned = version.Trim().TrimStart('v', 'V');
        var suffixIndex = cleaned.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0) cleaned = cleaned[..suffixIndex];
        return Version.TryParse(cleaned, out result!);
    }

    private string ResolveUrl(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return absolute.ToString();
        var relative = value.TrimStart('/');
        return _httpClient.BaseAddress == null
            ? value
            : new Uri(_httpClient.BaseAddress, relative).ToString();
    }

    private static string GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    private sealed class LatestVersionInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrlAlias { get; set; }
        [JsonPropertyName("release_notes")]
        public string? ReleaseNotes { get; set; }
        [JsonPropertyName("releaseNotes")]
        public string? ReleaseNotesAlias { get; set; }
        [JsonPropertyName("file_size")]
        public long? FileSize { get; set; }
        [JsonPropertyName("fileSize")]
        public long? FileSizeAlias { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonIgnore] public string? EffectiveDownloadUrl => FirstNonEmpty(DownloadUrl, DownloadUrlAlias);
        [JsonIgnore] public string? EffectiveReleaseNotes => FirstNonEmpty(ReleaseNotes, ReleaseNotesAlias);
        [JsonIgnore] public long? EffectiveFileSize => FileSize ?? FileSizeAlias;

        public LatestPlatformInfo? GetPlatformInfo(string platform)
        {
            if (ExtensionData == null || !ExtensionData.TryGetValue(platform, out var element) || element.ValueKind != JsonValueKind.Object)
                return null;
            return JsonSerializer.Deserialize<LatestPlatformInfo>(element.GetRawText());
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }
    }

    private sealed class LatestPlatformInfo
    {
        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrlAlias { get; set; }
        [JsonPropertyName("file_size")]
        public long? FileSize { get; set; }
        [JsonPropertyName("fileSize")]
        public long? FileSizeAlias { get; set; }

        [JsonIgnore] public string? EffectiveDownloadUrl => !string.IsNullOrWhiteSpace(DownloadUrl) ? DownloadUrl : DownloadUrlAlias;
        [JsonIgnore] public long? EffectiveFileSize => FileSize ?? FileSizeAlias;
    }
}
