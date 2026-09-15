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
    public const string WorkgroupsSettings = "WorkgroupsSettings";
}
