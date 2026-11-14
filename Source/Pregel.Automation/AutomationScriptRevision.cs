using System.IO.Compression;
using Heron.Pregel.Tooling;

namespace Heron.Pregel.Automation;

/// <summary>Content identity of a published script suite, independent of its ZIP representation.</summary>
public static class AutomationScriptRevision
{
    public static bool IsValidId(string? id) => ScriptSourceContent.IsValidId(id);
    public static bool IsContentHash(string? revision) => ScriptSourceContent.IsContentHash(revision);
    public static bool IsValid(string? revision) => ScriptSourceContent.IsValid(revision);

    // Numeric archives retain their historical URL. Content revisions may have
    // several ZIP representations (compression/runtime differences), each immutable.
    public static string ArchivePath(string id, string revision, string sha256) =>
        ScriptSourceContent.ArchivePath(id, revision, sha256);

    public static string Compute(ZipArchive archive, string id, string entry)
        => ScriptSourceContent.Compute(archive, id, entry);
}
