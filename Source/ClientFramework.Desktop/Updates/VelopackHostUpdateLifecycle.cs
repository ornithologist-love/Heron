using Heron.ClientFramework.Abstractions;
using Velopack;
using Velopack.Locators;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Generic Velopack implementation of <see cref="IClientHostUpdateLifecycle" /> for rollback and install-root safety checks.</summary>
public sealed class VelopackHostUpdateLifecycle : IClientHostUpdateLifecycle
{
    private readonly Func<string> _resolveFeedUrl;
    private readonly Func<Action, Task> _exitForUpdateAsync;
    private readonly Func<CancellationToken, Task> _ensureInstallRootFreeAsync;

    public VelopackHostUpdateLifecycle(
        Func<string> resolveFeedUrl,
        Func<Action, Task> exitForUpdateAsync,
        Func<CancellationToken, Task>? ensureInstallRootFreeAsync = null)
    {
        _resolveFeedUrl = resolveFeedUrl ?? throw new ArgumentNullException(nameof(resolveFeedUrl));
        _exitForUpdateAsync = exitForUpdateAsync ?? throw new ArgumentNullException(nameof(exitForUpdateAsync));
        _ensureInstallRootFreeAsync = ensureInstallRootFreeAsync
            ?? (static cancellationToken => InstallRootHolders.EnsureFreeAsync(cancellationToken: cancellationToken));
    }

    public string PackagesDirectory => VelopackLocator.Current.PackagesDir
        ?? throw new InvalidOperationException("当前安装没有本地更新包目录。");

    public string CurrentPackageFileName
    {
        get
        {
            var locator = VelopackLocator.Current;
            return locator.GetLocalPackages().Single(p => p.Type == VelopackAssetType.Full
                && p.Version.Equals(locator.CurrentlyInstalledVersion)).FileName;
        }
    }

    public async Task ApplyRollbackAsync(string packageFileName, Action restoreProductState,
        Action restoreJournal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("宿主回退仅支持 Windows。");
        var locator = VelopackLocator.Current;
        var release = locator.GetLocalPackages().SingleOrDefault(p => p.FileName == packageFileName)
            ?? throw new FileNotFoundException("找不到已验证的本体回退包。", packageFileName);
        var manager = new UpdateManager(_resolveFeedUrl(), new UpdateOptions { AllowVersionDowngrade = true });
        await _ensureInstallRootFreeAsync(cancellationToken).ConfigureAwait(false);
        await _exitForUpdateAsync(() =>
        {
            restoreProductState();
            try { manager.WaitExitThenApplyUpdates(release, false, true, null); }
            catch { restoreJournal(); throw; }
        }).ConfigureAwait(false);
    }
}
