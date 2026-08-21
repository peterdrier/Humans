using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.Debug.Services;

/// <summary>
/// 4-dimensional set partition of the UserInfo cache for the admin dashboard's Venn + UpSet
/// diagrams. Each user is placed into exactly one of 16 disjoint buckets indexed by a
/// bitmask over four boolean dimensions:
/// <list type="bullet">
///   <item><description>bit 0 — <c>Profile</c>: <c>UserInfo.HasRequiredNameFields</c></description></item>
///   <item><description>bit 1 — <c>Ticket</c>: <c>UserInfo.HasTicketForYear(activeYear)</c></description></item>
///   <item><description>bit 2 — <c>Shift</c>: active signup (Pending/Confirmed) in the active event</description></item>
///   <item><description>bit 3 — <c>Marketing</c>: explicit marketing opt-in (<c>MarketingOptedOut == false</c>)</description></item>
/// </list>
/// The Venn diagram uses only the first three bits (marginalized over Marketing); the UpSet
/// plot uses all four. Moved out of the deleted admin-dashboard aggregator at
/// nobodies-collective/Humans#1091.
/// </summary>
internal static class UserSetMembershipCalculator
{
    public static async Task<UserSetMembership> BuildAsync(
        IReadOnlyCollection<UserInfo> snapshot,
        IBurnSettingsService burnSettings,
        IShiftView shiftView,
        CancellationToken ct)
    {
        var activeEvent = await burnSettings.GetActiveAsync();
        var activeYear = activeEvent?.Year ?? 0;
        var shiftViews = await shiftView.GetUsersAsync(snapshot.Select(u => u.Id), ct);

        var counts = new Dictionary<int, int>(capacity: 16);
        foreach (var u in snapshot)
        {
            var mask = 0;
            if (u.HasRequiredNameFields) mask |= UserSetMembership.ProfileBit;
            if (activeYear > 0 && u.HasTicketForYear(activeYear)) mask |= UserSetMembership.TicketBit;
            if (shiftViews[u.Id].HasShift) mask |= UserSetMembership.ShiftBit;
            // Explicit opt-in only: tri-state MarketingOptedOut == false (not null, not true).
            if (u.MarketingOptedOut == false) mask |= UserSetMembership.MarketingBit;

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
