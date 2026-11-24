using System.Security.Cryptography;
using System.Text;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Alternative mutex and activation event names held alongside the primary identity for upgrade compatibility.</summary>
public sealed record SingleInstanceAlias(string MutexName, string ActivationEventName);

/// <summary>Process-wide single-instance lock and activation identity.</summary>
public sealed record SingleInstanceOptions(
    string InstanceId,
    string MutexName,
    string ActivationEventName,
    string ExecutablePath,
    string? PidFilePath,
    string? MainWindowTitle,
    bool IsDebugScoped,
    IReadOnlyList<SingleInstanceAlias>? LegacyAliases = null)
{
    public IReadOnlyList<SingleInstanceAlias> LegacyAliases { get; init; } = LegacyAliases ?? [];

    public static SingleInstanceOptions Create(
        string mutexPrefix,
        string executablePath,
        string dataRoot,
        bool isolateDebug,
        string? pidFileName = null,
        string? mainWindowTitle = null,
        IReadOnlyList<SingleInstanceAlias>? legacyAliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedPath = NormalizeExecutablePath(executablePath);
        if (!isolateDebug)
        {
            return new SingleInstanceOptions(
                "release",
                $"{mutexPrefix}_SingleInstance",
                $"{mutexPrefix}_ActivateInstance",
                normalizedPath,
                pidFileName != null ? Path.Combine(dataRoot, pidFileName) : null,
                mainWindowTitle,
                false,
                legacyAliases);
        }

        var hashInput = OperatingSystem.IsWindows() ? normalizedPath.ToUpperInvariant() : normalizedPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()[..16];
        var instanceId = $"debug-{hash}";
        return new SingleInstanceOptions(
            instanceId,
            $"{mutexPrefix}_Debug_{hash}",
            $"{mutexPrefix}_Debug_{hash}_Activate",
            normalizedPath,
            pidFileName != null ? Path.Combine(dataRoot, "debug-instances", instanceId, pidFileName) : null,
            mainWindowTitle,
            true,
            legacyAliases);
    }

    public static string NormalizeExecutablePath(string executablePath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(executablePath));

    public bool MatchesExecutablePath(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(ExecutablePath, NormalizeExecutablePath(candidatePath), comparison);
    }
}
