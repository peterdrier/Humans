namespace Humans.Workgroups.Services;

internal enum WorkgroupNoticeKind
{
    Applied,
    Referred,
    Registered,
    Refused,
    Withdrawn,
    Ended,
    Reactivated,
    CoordinatorsChanged,
    DormancyInquiry,
    Delivered,
    DispositionRecorded
}

/// <summary>
/// <paramref name="RecipientName"/> is null for a role inbox such as the Board. <paramref name="Detail"/> is the one
/// variable line: reasons for Refused and Withdrawn, the disposition and note, the delivered document's title.
/// </summary>
internal sealed record WorkgroupNoticeRequest(
    Guid RecipientUserId,
    string RecipientEmail,
    string? RecipientName,
    WorkgroupNoticeKind Kind,
    string WorkgroupName,
    string WorkgroupSlug,
    string? Detail = null,
    string? Culture = null);
