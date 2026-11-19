using System.Globalization;
using System.Resources;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Helpers;

namespace Humans.Shifts.Models;

internal sealed record ShiftSignupBuckets(
    List<MySignupItem> Upcoming,
    List<MySignupItem> Pending,
    List<MySignupItem> Past);

internal static class ShiftSignupBucketer
{
    private static readonly ResourceManager Resources = new(typeof(ShiftsResource));

    internal static IReadOnlyList<Guid> GetTeamIds(IReadOnlyList<ShiftUserSignup> signups) =>
        signups
            .Select(s => s.Rota.TeamId)
            .Distinct()
            .ToList();

    internal static ShiftSignupBuckets Build(
        IReadOnlyList<ShiftUserSignup> signups,
        BurnSettingsInfo? eventSettings,
        IReadOnlyDictionary<Guid, string> teamNames,
        Instant now)
    {
        var upcoming = new List<MySignupItem>();
        var pending = new List<MySignupItem>();
        var past = new List<MySignupItem>();

        if (eventSettings is null)
            return new ShiftSignupBuckets(upcoming, pending, past);

        foreach (var signup in signups)
        {
            var item = new MySignupItem
            {
                Signup = signup,
                DepartmentName = teamNames.GetValueOrDefault(signup.Rota.TeamId, Resources.GetString("Shifts_Signup_UnknownTeam", CultureInfo.CurrentUICulture)!),
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
