namespace Humans.Workgroups.Services;

/// <summary>What a working-group notice is about; picks the subject and body copy.</summary>
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
/// Payload for a working-group register notice. <see cref="RecipientName"/> is null
/// when the recipient is a role inbox (e.g. the Board) rather than a named person.
/// </summary>
/// <param name="RecipientEmail">The address receiving the notice.</param>
/// <param name="RecipientName">The recipient’s name, or null for a role inbox.</param>
/// <param name="Kind">The event determining the notice’s subject and body.</param>
/// <param name="WorkgroupName">The working group’s display name.</param>
/// <param name="WorkgroupSlug">The working group’s route slug.</param>
/// <param name="Culture">The recipient’s culture, or null for the default.</param>
/// <param name="Detail">
/// The one variable line the notice carries: the written reasons for Refused and
/// Withdrawn, the disposition and its note for DispositionRecorded, the document
/// title for Delivered, the days of silence for DormancyInquiry. Null when the kind
/// needs none.
/// </param>
internal sealed record WorkgroupNoticeRequest(
    string RecipientEmail,
    string? RecipientName,
    WorkgroupNoticeKind Kind,
    string WorkgroupName,
    string WorkgroupSlug,
    string? Detail = null,
    string? Culture = null);
