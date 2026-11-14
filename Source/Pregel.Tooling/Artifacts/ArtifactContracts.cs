namespace Heron.Pregel.Tooling;

public sealed record ArtifactSource(string Path, string Name, string Kind = "file", bool IsDirectory = false, bool Required = true);
public sealed record ArtifactArchiveRequest(string Directory, string Title, ArtifactSource[] Sources);
public sealed record ArchivedArtifact(string Name, string Kind, string Source, long Bytes, string Sha256);
public sealed record ArtifactManifest(int Schema, string Title, DateTimeOffset ArchivedAt, ArchivedArtifact[] Artifacts);

/// <summary>One produced file, with independent locations for archival and delivery.</summary>
public sealed record ArtifactRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "file";
    public string MachineName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string? ArchivedPath { get; init; }
    public long? Bytes { get; init; }
    public string? Sha256 { get; init; }
    public IReadOnlyList<ArtifactDelivery> Deliveries { get; init; } = [];

    public ArtifactRecord Merge(ArtifactRecord incoming) => incoming with
    {
        Name = ArchivedPath != null && incoming.ArchivedPath == null ? Name : incoming.Name,
        Kind = incoming.Kind == "file" ? Kind : incoming.Kind,
        ArchivedPath = incoming.ArchivedPath ?? ArchivedPath,
        Bytes = ArchivedPath != null && incoming.ArchivedPath == null ? Bytes : incoming.Bytes ?? Bytes,
        Sha256 = ArchivedPath != null && incoming.ArchivedPath == null ? Sha256 : incoming.Sha256 ?? Sha256,
        Deliveries = Deliveries.Concat(incoming.Deliveries).GroupBy(item => item.Destination, StringComparer.Ordinal)
            .Select(group => group.Last()).ToArray()
    };
}

public sealed record ArtifactDelivery(string Destination, string State, string? Location = null, string? Error = null);
