namespace Heron.Core.Utils;

/// <summary>
///     Helpers for turning user-provided names into safe single path segments.
/// </summary>
public static class PathSafety
{
    private static readonly HashSet<char> InvalidFileNameChars = Path.GetInvalidFileNameChars().ToHashSet();

    public static string SanitizePathSegment(string? value, string fallback = "unnamed", int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value.Trim().Select(c =>
            char.IsControl(c) || c is '/' or '\\' or ':' || InvalidFileNameChars.Contains(c)
                ? '_'
                : c).ToArray();

        var sanitized = new string(chars);
        while (sanitized.Contains("..", StringComparison.Ordinal))
            sanitized = sanitized.Replace("..", "_", StringComparison.Ordinal);

        sanitized = sanitized.Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized))
            return fallback;

        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    public static string SanitizeFileName(string? value, string fallback = "upload.bin", int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalizedSeparators = value.Replace('\\', '/');
        var fileName = Path.GetFileName(normalizedSeparators);
        return SanitizePathSegment(fileName, fallback, maxLength);
    }
}
