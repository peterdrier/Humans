using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

public sealed record ShiftSignupBuckets(
    List<MySignupItem> Upcoming,
    List<MySignupItem> Pending,
    List<MySignupItem> Past);

public static class ShiftSignupBucketer
{
    public static IReadOnlyList<Guid> GetTeamIds(IReadOnlyList<ShiftSignup> signups) =>
        signups
            .Where(s => s.Shift?.Rota is not null)
            .Select(s => s.Shift.Rota.TeamId)
            .Distinct()
            .ToList();

    public static ShiftSignupBuckets Build(
        IReadOnlyList<ShiftSignup> signups,
        EventSettings? eventSettings,
        IReadOnlyDictionary<Guid, string> teamNames,
        Instant now,
        bool includeOtherStatusesInPast = true,
        Action<ShiftSignup>? onMissingSignupData = null)
    {
        var upcoming = new List<MySignupItem>();
        var pending = new List<MySignupItem>();
        var past = new List<MySignupItem>();

        if (eventSettings is null)
            return new ShiftSignupBuckets(upcoming, pending, past);

        foreach (var signup in signups)
        {
            if (signup.Shift?.Rota is null)
            {
                onMissingSignupData?.Invoke(signup);
                continue;
            }

            var item = new MySignupItem
            {
                Signup = signup,
                DepartmentName = teamNames.GetValueOrDefault(signup.Shift.Rota.TeamId, "Unknown"),
                AbsoluteStart = signup.Shift.GetAbsoluteStart(eventSettings),
                AbsoluteEnd = signup.Shift.GetAbsoluteEnd(eventSettings)
            };

            switch (signup.Status)
            {
                case SignupStatus.Confirmed when item.AbsoluteEnd > now:
                    upcoming.Add(item);
                    break;
                case SignupStatus.Pending:
                    pending.Add(item);
                    break;
                default:
                    if (includeOtherStatusesInPast || signup.Status is SignupStatus.Confirmed or SignupStatus.NoShow or SignupStatus.Bailed)
                        past.Add(item);
                    break;
            }
        }

        return new ShiftSignupBuckets(
            upcoming.OrderBy(s => s.AbsoluteStart).ToList(),
            pending.OrderBy(s => s.AbsoluteStart).ToList(),
            past.OrderByDescending(s => s.AbsoluteStart).ToList());
    }
}
