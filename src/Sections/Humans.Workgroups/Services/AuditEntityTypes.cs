namespace Humans.Workgroups.Services;

/// <summary>Persisted strings: never derive them from type names.</summary>
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
