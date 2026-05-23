using Humans.Application;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Users;
using NodaTime;

namespace Humans.Web.Models.Mailer;

/// <summary>
/// Builds the unpaged debug snapshot for a single audience by combining:
/// the audience's computed user-id set, the cached <see cref="UserInfo"/>
/// snapshot, and the live MailerLite subscriber/group state.
/// </summary>
/// <remarks>
/// All Humans-side reads route through cached interfaces — the audience compute
/// uses <c>IShiftView</c> + <c>ITicketQueryService</c> (decorated by their
/// caching layers), and name/email rendering reads <see cref="UserInfo"/>
/// from <c>IUserServiceRead.GetUserInfosAsync</c>. The MailerLite read is
/// intentional (we're diffing against the remote we don't own).
/// </remarks>
internal static class MailerAudienceDebugSnapshotBuilder
{
    // Mirrors MailerAudienceSyncService.UnsubscribedStatuses so the debug page
    // previews what Sync will actually do. If you change one, change both.
    private static readonly HashSet<string> SuppressedSubscriberStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unsubscribed", "bounced", "junk" };

    public static async Task<DebugSnapshot> BuildAsync(
        IMailerAudience audience,
        IMailerLiteService ml,
        IUserServiceRead users,
        ILogger logger,
        CancellationToken ct)
    {
        var expectedIds = await audience.ComputeMemberUserIdsAsync(ct);

        // Pull subscribers + groups once; any failure surfaces as MlError and
        // the page renders Humans-side data only.
        List<MailerLiteSubscriber>? subscribers = null;
        MailerLiteGroup? group = null;
        bool groupExists = false;
        string? mlError = null;

        try
        {
            var groups = await ml.ListGroupsAsync(ct);
            group = groups.FirstOrDefault(g =>
                string.Equals(g.Name, audience.MailerLiteGroupName, StringComparison.Ordinal));
            groupExists = group is not null;

            var fetched = new List<MailerLiteSubscriber>();
            await foreach (var s in ml.ListSubscribersAsync(ct).WithCancellation(ct))
                fetched.Add(s);
            // Only assign on successful completion — a partial fetch must not
            // back §2/§3/§4 computation.
            subscribers = fetched;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "MailerLite read failed for audience {AudienceKey}", audience.Key);
            mlError = $"MailerLite read failed: {ex.StatusCode?.ToString() ?? "no response"}.";
            subscribers = null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "MailerLite read timed out for audience {AudienceKey}", audience.Key);
            mlError = "MailerLite read timed out.";
            subscribers = null;
        }

        // Cached UserInfo — covers expected users plus every user we need to
        // resolve from a subscriber email in §2/§5.
        var allUserInfos = await users.GetAllUserInfosAsync(ct);
        var byUserId = allUserInfos.ToDictionary(u => u.Id);

        // verified email (case-insensitive) → first user found. UserInfo cache
        // ordering is stable per record construction, so picking First is
        // deterministic per snapshot.
        var emailToUser = new Dictionary<string, UserInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in allUserInfos)
        {
            foreach (var e in u.UserEmails)
            {
                if (!e.IsVerified) continue;
                emailToUser.TryAdd(e.Email, u);
            }
        }

        // §1 Expected — resolve a notification-target email from cached UserInfo.
        var expected = new List<DebugExpectedRow>(expectedIds.Count);
        foreach (var uid in expectedIds)
        {
            if (!byUserId.TryGetValue(uid, out var u)) continue;
            var email = NotificationTargetEmail(u);
            if (email is null) continue;
            expected.Add(new DebugExpectedRow(uid, u.BurnerName, email));
        }

        // §2 Currently in ML — subscribers whose GroupIds include our group.
        // Mirror MailerAudienceSyncService's status filter: unsubscribed /
        // bounced / junk are skipped by Sync (line 127 there), so they must
        // not appear in the diff preview either or §3/§4 counts will lie
        // about what Apply will do.
        var currentlyInMl = new List<DebugMlRow>();
        if (subscribers is not null && group is not null)
        {
            foreach (var s in subscribers)
            {
                if (!s.GroupIds.Contains(group.Id, StringComparer.Ordinal)) continue;
                if (SuppressedSubscriberStatuses.Contains(s.Status)) continue;
                UserInfo? matchedUser = null;
                emailToUser.TryGetValue(s.Email, out matchedUser);
                var name = matchedUser?.BurnerName ?? "—";
                currentlyInMl.Add(new DebugMlRow(
                    SubscriberId: s.Id,
                    UserId: matchedUser?.Id,
                    Name: name,
                    Email: s.Email,
                    InMlSince: s.SubscribedAt));
            }
        }

        // §3/§4 set-diff by normalized email.
        var expectedByEmail = expected
            .GroupBy(r => Normalize(r.Email), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var mlByEmail = currentlyInMl
            .GroupBy(r => Normalize(r.Email), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var toAdd = expected
            .Where(r => !mlByEmail.ContainsKey(Normalize(r.Email)))
            .ToList();
        var toRemove = currentlyInMl
            .Where(r => !expectedByEmail.ContainsKey(Normalize(r.Email)))
            .ToList();

        // §5 Non-primary — subscriber matches a verified UserEmail but the
        // matched user's primary is a different email.
        var nonPrimary = new List<DebugNonPrimaryRow>();
        foreach (var row in currentlyInMl)
        {
            if (row.UserId is not Guid uid) continue;
            if (!byUserId.TryGetValue(uid, out var u)) continue;
            var primary = u.PrimaryEmail;
            if (primary is null) continue;
            if (string.Equals(primary, row.Email, StringComparison.OrdinalIgnoreCase)) continue;
            nonPrimary.Add(new DebugNonPrimaryRow(
                UserId: uid,
                Name: u.BurnerName,
                SubscribedEmail: row.Email,
                PrimaryEmail: primary));
        }

        return new DebugSnapshot(
            Expected: expected,
            CurrentlyInMl: currentlyInMl,
            ToAdd: toAdd,
            ToRemove: toRemove,
            NonPrimary: nonPrimary,
            GroupExists: groupExists,
            MlError: mlError);
    }

    public static DebugSection<DebugExpectedRow> PageExpected(
        IReadOnlyList<DebugExpectedRow> rows, DebugTableState state, DebugTableOptions opts)
    {
        var sorted = (state.Sort switch
        {
            DebugSortColumn.Email => rows.OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Email, StringComparer.OrdinalIgnoreCase),
        }).ToList();
        if (state.Descending) sorted.Reverse();
        return new DebugSection<DebugExpectedRow>(SlicePage(sorted, state, opts), sorted.Count, Normalize(state, opts));
    }

    public static DebugSection<DebugMlRow> PageMl(
        IReadOnlyList<DebugMlRow> rows, DebugTableState state, DebugTableOptions opts)
    {
        var sorted = (state.Sort switch
        {
            DebugSortColumn.Email => rows.OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            DebugSortColumn.InMlSince => rows.OrderBy(r => r.InMlSince ?? Instant.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Email, StringComparer.OrdinalIgnoreCase),
        }).ToList();
        if (state.Descending) sorted.Reverse();
        return new DebugSection<DebugMlRow>(SlicePage(sorted, state, opts), sorted.Count, Normalize(state, opts));
    }

    public static DebugSection<DebugNonPrimaryRow> PageNonPrimary(
        IReadOnlyList<DebugNonPrimaryRow> rows, DebugTableState state, DebugTableOptions opts)
    {
        var sorted = (state.Sort switch
        {
            DebugSortColumn.Email => rows.OrderBy(r => r.SubscribedEmail, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SubscribedEmail, StringComparer.OrdinalIgnoreCase),
        }).ToList();
        if (state.Descending) sorted.Reverse();
        return new DebugSection<DebugNonPrimaryRow>(SlicePage(sorted, state, opts), sorted.Count, Normalize(state, opts));
    }

    private static IReadOnlyList<TRow> SlicePage<TRow>(IReadOnlyList<TRow> rows, DebugTableState state, DebugTableOptions opts)
    {
        var size = opts.PageSizes.Contains(state.PageSize) ? state.PageSize : opts.DefaultPageSize;
        var page = state.Page < 1 ? 1 : state.Page;
        var skip = (page - 1) * size;
        if (skip >= rows.Count && rows.Count > 0)
            skip = ((rows.Count - 1) / size) * size;
        if (skip < 0) skip = 0;
        return rows.Skip(skip).Take(size).ToList();
    }

    private static DebugTableState Normalize(DebugTableState state, DebugTableOptions opts)
    {
        var size = opts.PageSizes.Contains(state.PageSize) ? state.PageSize : opts.DefaultPageSize;
        var page = state.Page < 1 ? 1 : state.Page;
        return state with { Page = page, PageSize = size };
    }

    private static string? NotificationTargetEmail(UserInfo u)
    {
        var primary = u.UserEmails
            .Where(e => e.IsVerified && e.IsPrimary)
            .Select(e => e.Email)
            .FirstOrDefault();
        if (primary is not null) return primary;
        // Fallback to identity column — matches IUserEmailService.GetNotificationTargetEmailsAsync semantics.
        return u.IdentityEmailColumn;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public sealed record DebugSnapshot(
        IReadOnlyList<DebugExpectedRow> Expected,
        IReadOnlyList<DebugMlRow> CurrentlyInMl,
        IReadOnlyList<DebugExpectedRow> ToAdd,
        IReadOnlyList<DebugMlRow> ToRemove,
        IReadOnlyList<DebugNonPrimaryRow> NonPrimary,
        bool GroupExists,
        string? MlError);
}
