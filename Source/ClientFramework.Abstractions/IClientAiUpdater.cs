namespace Heron.ClientFramework.Abstractions;

public sealed record ClientAiUpdateProgress(string Stage, long Completed = 0, long Total = 0, string Detail = "");

public sealed record ClientAiUpdatePlan(
    string Mode = "host",
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    string? ReleaseNotes = null,
    long? PackageSize = null,
    string? Error = null,
    bool HasUpdate = false,
    bool CanPrepareAll = false,
    bool CanUpdateHostOnly = false,
    bool IsInstalled = false);

/// <summary>The host downloads the selected release; the AI plugin safely activates it.</summary>
public interface IClientAiUpdater
{
    Task ApplyPreparedAsync(string version, CancellationToken cancellationToken);

    ClientAiUpdatePlan UpdatePlan => new();
    string PreviewHint => "AI 功能由独立产品插件提供。";

    Task<ClientAiUpdatePlan> CheckUpdatePlanAsync(string? targetHostVersion, CancellationToken cancellationToken) =>
        Task.FromResult(UpdatePlan);

    Task<string> PrepareAiOnlyAsync(IProgress<ClientAiUpdateProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AI 包更新由当前插件版本处理。");

    Task PrepareAiForHostAsync(string targetHostVersion, string hostPackagePath,
        IProgress<ClientAiUpdateProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AI 联合更新由当前插件版本处理。");

    void RefreshAiState() { }

    void PreserveCurrentHostPackage() { }

    Task InstallPreviewAsync(string archivePath, CancellationToken cancellationToken) =>
        throw new NotSupportedException("AI 预览包安装由当前插件版本处理。");
}
