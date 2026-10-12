namespace Humans.Workgroups.Services;

/// <summary>
/// Entity-type strings this section writes into the audit log. Pinned as constants
/// because the value is persisted (memory/code/type-name-as-persisted-string.md).
/// </summary>
internal static class AuditEntityTypes
{
    public const string Workgroup = "Workgroup";
    public const string WorkgroupDocument = "WorkgroupDocument";
    public const string WorkgroupComment = "WorkgroupComment";
    public const string WorkgroupLogEntry = "WorkgroupLogEntry";
    public const string WorkgroupMeeting = "WorkgroupMeeting";
    public const string WorkgroupsSettings = "WorkgroupsSettings";

    /// <summary>Related-entity type on membership decisions, so the subject's GDPR export finds the row.</summary>
    public const string User = "User";
}
