using NodaTime;

namespace Humans.MailerLite.Models;

internal sealed record MailerLiteAudienceDebugViewModel(
    string SelectedKey,
    string SelectedGroupName,
    string SelectedMailerLiteGroupName,
    IReadOnlyList<MailerLiteAudienceListItem> AvailableAudiences,
    bool GroupExists,
    string? MlError,
    DebugSection<DebugExpectedRow> Expected,
    DebugSection<DebugMlRow> CurrentlyInMl,
    DebugSection<DebugExpectedRow> ToAdd,
    DebugSection<DebugMlRow> ToRemove,
    DebugSection<DebugNonPrimaryRow> NonPrimary,
    DebugTableOptions Options);

internal sealed record MailerLiteAudienceListItem(string Key, string GroupName);

/// <summary><see cref="Total"/> is the unfiltered row count.</summary>
internal sealed record DebugSection<TRow>(
    IReadOnlyList<TRow> Rows,
    int Total,
    DebugTableState State);

internal sealed record DebugTableState(
    string Section,
    int Page,
    int PageSize,
    DebugSortColumn Sort,
    bool Descending);

internal sealed record DebugTableOptions(
    IReadOnlyList<int> PageSizes,
    int DefaultPageSize);

internal enum DebugSortColumn
{
    Name,
    Email,
    /// <summary>§2 only; the other tables sort by name.</summary>
    InMlSince,
}

internal sealed record DebugExpectedRow(
    Guid UserId,
    string Name,
    string Email);

/// <summary>UserId is null when the email doesn't resolve to a known human.</summary>
internal sealed record DebugMlRow(
    string SubscriberId,
    Guid? UserId,
    string Name,
    string Email,
    Instant? InMlSince);

/// <summary>A subscriber under a non-primary verified email, paired with the user's current primary.</summary>
internal sealed record DebugNonPrimaryRow(
    Guid UserId,
    string Name,
    string SubscribedEmail,
    string PrimaryEmail);

/// <summary>Builds a Debug URL with one table's state changed and every other table's carried over.</summary>
internal delegate string DebugUrlBuilder(
    DebugTableState state, int? page, int? size, DebugSortColumn? sort, bool? desc);

internal sealed record DebugSortHeader(
    DebugTableState State,
    DebugSortColumn Column,
    string Label,
    DebugUrlBuilder Url);

internal sealed record DebugPagerModel(
    DebugTableState State,
    int Total,
    DebugTableOptions Options,
    DebugUrlBuilder Url);
