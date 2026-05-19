using NodaTime;

namespace Humans.Web.Models.Mailer;

/// <summary>
/// Per-audience debug snapshot — five tables comparing Humans-side audience
/// membership against the live MailerLite group state. Issue #773.
///
/// Sources:
/// 1. Expected — <c>IMailerAudience.ComputeMemberUserIdsAsync</c> resolved to
///    notification-target emails. Read from cached UserInfo.
/// 2. Currently in ML — subscribers in <c>ListSubscribersAsync</c> whose
///    GroupIds contain the audience's target group.
/// 3/4. To add / To remove — set diff by normalized email.
/// 5. Non-primary subscribed — diagnostic pairing for the Frank-pattern.
/// </summary>
public sealed record MailerAudienceDebugViewModel(
    string SelectedKey,
    string SelectedGroupName,
    string SelectedMailerLiteGroupName,
    IReadOnlyList<MailerAudienceListItem> AvailableAudiences,
    bool GroupExists,
    string? MlError,
    DebugSection<DebugExpectedRow> Expected,
    DebugSection<DebugMlRow> CurrentlyInMl,
    DebugSection<DebugExpectedRow> ToAdd,
    DebugSection<DebugMlRow> ToRemove,
    DebugSection<DebugNonPrimaryRow> NonPrimary,
    DebugTableOptions Options);

/// <summary>Dropdown entry — every DI-registered <c>IMailerAudience</c>.</summary>
public sealed record MailerAudienceListItem(string Key, string GroupName);

/// <summary>Paged + sorted slice of one section's rows. <see cref="Total"/> is the unfiltered row count.</summary>
public sealed record DebugSection<TRow>(
    IReadOnlyList<TRow> Rows,
    int Total,
    DebugTableState State);

/// <summary>Per-table state — section discriminator (used in querystring), page, page size, sort column + direction.</summary>
public sealed record DebugTableState(
    string Section,
    int Page,
    int PageSize,
    DebugSortColumn Sort,
    bool Descending);

/// <summary>Defaults + allowed page sizes for the view.</summary>
public sealed record DebugTableOptions(
    IReadOnlyList<int> PageSizes,
    int DefaultPageSize);

public enum DebugSortColumn
{
    Name,
    Email,
    /// <summary>"Subscribed-at" in MailerLite — §2 only. For other sections, behaves as Name.</summary>
    InMlSince,
}

/// <summary>§1, §3 — humans that belong in the audience.</summary>
public sealed record DebugExpectedRow(
    Guid UserId,
    string Name,
    string Email);

/// <summary>§2, §4 — subscribers currently in the ML group. UserId is null when the email doesn't resolve to a known human.</summary>
public sealed record DebugMlRow(
    string SubscriberId,
    Guid? UserId,
    string Name,
    string Email,
    Instant? InMlSince);

/// <summary>§5 — subscriber in ML under a non-primary verified email, paired with the user's current primary.</summary>
public sealed record DebugNonPrimaryRow(
    Guid UserId,
    string Name,
    string SubscribedEmail,
    string PrimaryEmail);

/// <summary>
/// Delegate used by the Debug view + partials to build a Debug URL with one
/// section's state mutated and every other section's state carried verbatim.
/// </summary>
public delegate string DebugUrlBuilder(
    DebugTableState state, int? page, int? size, DebugSortColumn? sort, bool? desc);

/// <summary>Header-cell render input — passed into <c>_DebugSortHeader.cshtml</c>.</summary>
public sealed record DebugSortHeader(
    DebugTableState State,
    DebugSortColumn Column,
    string Label,
    DebugUrlBuilder Url);

/// <summary>Footer-pager render input — passed into <c>_DebugPager.cshtml</c>.</summary>
public sealed record DebugPagerModel(
    DebugTableState State,
    int Total,
    DebugTableOptions Options,
    DebugUrlBuilder Url);
