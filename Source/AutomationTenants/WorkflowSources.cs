namespace Heron.AutomationTenants.Contracts;

public sealed record WorkflowSourcePackage(string Id, string Revision, string Archive, string Sha256,
    string Entry = "", string Title = "", string Description = "", string? TaskId = null, string? TenantId = null);
public sealed record WorkflowSourceManifest(WorkflowSourcePackage Shared, WorkflowSourcePackage[] Scripts);
public sealed record WorkflowOfficialBaseline(string CatalogHash);
public sealed record WorkflowOfficialPublishRequest(string CatalogHash, WorkflowSourceManifest Manifest);
public sealed record WorkflowOfficialPublishResult(string? ReleaseId, int Published, string SharedRevision, bool SharedChanged);
public sealed record WorkflowSourceContext(string TaskId, string SourceId, string Title, string Revision,
    string SharedRevision, string Entry, string SharedContentRevision = "");
public sealed record WorkflowUploadRequest(string TenantId, string TaskId, string SourceId, string Title,
    string Entry, string BaseRevision, string BaseSharedRevision, string Revision, string SharedRevision,
    string MachineName, bool MachineUser = false, string DefinitionId = "", string? ValidationRunId = null,
    bool NewTask = false, string UploadId = "", bool ForceShared = false);
public sealed record WorkflowUploadIdentity(string TenantId, string MachineName, string UserId, string UserKind,
    string SharedPublisher = "");
public sealed record WorkflowSourceTarget(string Id, string Name);
public sealed record WorkflowSourceFile(string Path, long Bytes, string Sha256);
public sealed record WorkflowReviewDecision(string Revision, string Comment = "", bool ConfirmSharedUpdate = false,
    string ReviewVersion = "", bool PublishOfficial = false);
public sealed record WorkflowSharedConflict(string Path, string Reason);
public sealed record WorkflowSharedReview(WorkflowSourcePackage Baseline, WorkflowSourcePackage Package,
    WorkflowSourceFile[] Files, WorkflowSourceChange[] Changes, WorkflowSharedConflict[] Conflicts,
    DateTimeOffset RebasedAt, WorkflowSourcePackage? MergeBase = null, WorkflowSourcePackage? MergeSource = null,
    string? ResolvedBy = null);
public sealed record WorkflowSharedFileResolution(string Path, string Choice, string? Content = null);
public sealed record WorkflowSharedResolution(string ReviewVersion, WorkflowSharedFileResolution[] Files);
public sealed record WorkflowSourceChange(string Path, string Change, long? Bytes, bool Shared);
public sealed record WorkflowSourceDiff(string Path, string? Before, string? After, bool Truncated, string? UnavailableReason = null,
    string? ConflictBase = null);
public sealed record WorkflowSubmission
{
    public const string DuplicateContentMessage = "相同内容已在待审核队列，已跳过重复提交。";
    public string Id { get; init; } = "";
    public WorkflowUploadRequest Request { get; init; } = null!;
    public WorkflowUploadIdentity Uploader { get; init; } = null!;
    public string State { get; init; } = "reviewing";
    public DateTimeOffset SubmittedAt { get; init; }
    public string? Reviewer { get; init; }
    public DateTimeOffset? ReviewStartedAt { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string ReviewComment { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public string SharedArchiveSha256 { get; init; } = "";
    public WorkflowSourceFile[] Files { get; init; } = [];
    public WorkflowSourceFile[] SharedFiles { get; init; } = [];
    public bool SharedChanged { get; init; }
    public bool SharedReviewConfirmed { get; init; }
    public bool PublishedAsOfficial { get; init; }
    public string? RelatedSubmissionId { get; init; }
    public WorkflowSourcePackage? BasePackage { get; init; }
    public WorkflowSourcePackage? BaseShared { get; init; }
    public WorkflowSourceChange[] Changes { get; init; } = [];
    public WorkflowSharedReview? SharedReview { get; init; }
    public string ReviewVersion { get; init; } = "";
}
