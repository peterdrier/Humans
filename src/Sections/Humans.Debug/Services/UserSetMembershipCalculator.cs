using Humans.Email.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.Debug.Services;

/// <summary>
/// 4-dimensional set partition of the UserInfo cache for the admin dashboard's Venn + UpSet
/// diagrams. Each user is placed into exactly one of 16 disjoint buckets indexed by a
/// bitmask over four boolean dimensions:
/// <list type="bullet">
///   <item><description>bit 0 — <c>Profile</c>: <c>UserInfo.HasRequiredNameFields</c></description></item>
///   <item><description>bit 1 — <c>Ticket</c>: Ticketed/Attended participation for the active year (<c>ITicketService</c>)</description></item>
///   <item><description>bit 2 — <c>Shift</c>: active signup (Pending/Confirmed) in the active event</description></item>
///   <item><description>bit 3 — <c>Marketing</c>: explicit marketing opt-in (a Marketing preference with <c>OptedOut</c> false, <c>ICommunicationPreferenceService</c>)</description></item>
/// </list>
/// The Venn diagram uses only the first three bits (marginalized over Marketing); the UpSet
/// plot uses all four.
/// </summary>
internal static class UserSetMembershipCalculator
{
    public static async Task<UserSetMembership> BuildAsync(
        IReadOnlyCollection<UserInfo> snapshot,
        ISettingsService settingsService,
        IShiftView shiftView,
        ITicketService tickets,
        ICommunicationPreferenceService preferences,
        CancellationToken ct)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        var activeYear = activeEvent?.Year ?? 0;
        var ticketHolders = activeYear > 0
            ? (await tickets.GetAllParticipationsAsync(ct))
                .Where(p => p.Year == activeYear && p.HasTicket).Select(p => p.UserId).ToHashSet()
            : [];
        var shiftViews = await shiftView.GetUsersAsync(snapshot.Select(u => u.Id), ct);
        var marketing = await preferences.GetPreferencesForCategoryAsync(MessageCategory.Marketing, ct);

        var counts = new Dictionary<int, int>(capacity: 16);
        foreach (var u in snapshot)
        {
            var mask = 0;
            if (u.HasRequiredNameFields) mask |= UserSetMembership.ProfileBit;
            if (ticketHolders.Contains(u.Id)) mask |= UserSetMembership.TicketBit;
            if (shiftViews[u.Id].HasShift) mask |= UserSetMembership.ShiftBit;
            // Explicit opt-in only: a recorded Marketing preference that is not opted out.
            if (marketing.GetValueOrDefault(u.Id) is { OptedOut: false }) mask |= UserSetMembership.MarketingBit;

            counts[mask] = counts.GetValueOrDefault(mask) + 1;
        }

        return new UserSetMembership(counts);
    }
}

internal sealed record UserSetMembership(IReadOnlyDictionary<int, int> CountsByMask)
{
    public const int ProfileBit = 1;
    public const int TicketBit = 2;
    public const int ShiftBit = 4;
    public const int MarketingBit = 8;

    public int TotalUsers => CountsByMask.Values.Sum();

    public int ProfilesCount => Marginal(ProfileBit);
    public int TicketsCount => Marginal(TicketBit);
    public int ShiftsCount => Marginal(ShiftBit);
    public int MarketingOptInsCount => Marginal(MarketingBit);

    public int ProfileTicketIntersection => Intersection(ProfileBit | TicketBit);
    public int ProfileShiftIntersection => Intersection(ProfileBit | ShiftBit);
    public int TicketShiftIntersection => Intersection(TicketBit | ShiftBit);
    public int ProfileTicketShiftIntersection => Intersection(ProfileBit | TicketBit | ShiftBit);

    private int Marginal(int bit) =>
        CountsByMask.Where(kv => (kv.Key & bit) != 0).Sum(kv => kv.Value);

    private int Intersection(int mask) =>
        CountsByMask.Where(kv => (kv.Key & mask) == mask).Sum(kv => kv.Value);
}
