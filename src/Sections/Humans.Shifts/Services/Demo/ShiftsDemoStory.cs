using Humans.Base.Demo;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Shifts.Services.Demo;

/// <summary>
/// Rotas for the demo departments on the active event, each filled to its own rate so the dashboard shows a mix of
/// full, short and empty, with some pending, bailed and refused signups. Attaches to the active event and never
/// changes its settings: when shift browsing is closed, the report says so.
/// </summary>
internal sealed class ShiftsDemoStory(
    IShiftManagement shifts,
    IShiftSeeding shiftCreation,
    IShiftRotaRows rotaRows,
    IShiftSignupService signups,
    ISettingsService settings,
    IClock clock) : IDemoStory
{
    private const string RotaKeyPrefix = "shifts:rota:";

    private static readonly DemoStoryKey StoryKey = new("shifts");

    // Low, middle and high fills, so every colour of the dashboard's chips shows.
    private static readonly IReadOnlyDictionary<string, double> SubteamRates = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["teams:team:consent"] = 0.3,
        ["teams:team:welfare"] = 0.6,
        ["teams:team:ohana-house"] = 0.85,
        ["teams:team:ice-ice-baby"] = 0.55,
        ["teams:team:icemakers"] = 0.9,
    };

    private static readonly HashSet<string> WellStaffed = new(StringComparer.Ordinal)
    {
        "teams:team:gate", "teams:team:site-operations",
    };

    public DemoStoryKey Key => StoryKey;

    public IReadOnlyList<DemoStoryKey> After => [TeamsDemo.StoryKey];

    public async Task TellAsync(IDemoWorld world, CancellationToken ct)
    {
        var activeEvent = settings.GetActiveEventSettings();
        if (activeEvent is null)
        {
            world.Report("shifts: no active event, so no demo rotas. Create one in Settings, then top up.");
            return;
        }

        var teamIds = await world.MadeAsync(TeamsDemo.TeamKeyPrefix, ct);
        var crowd = await world.MadeAsync(DemoCast.HumanKeyPrefix, ct);
        var volunteers = Enumerable.Range(1, DemoCast.Size)
            .Where(DemoCast.IsActiveVolunteer)
            .Select(n => crowd.GetValueOrDefault(DemoCast.HumanKey(n)))
            .Where(id => id != Guid.Empty)
            .ToList();

        foreach (var (teamKey, period, rate) in Rotas())
        {
            if (!teamIds.TryGetValue(teamKey, out var teamId))
                continue;

            var key = $"{RotaKeyPrefix}{activeEvent.Id:N}:{teamKey}:{period}";
            await world.EnsureAsync(
                key,
                async (id, token) => await FindAsync(id, token) is not null,
                (planned, token) => FillAsync(planned, key, teamId, activeEvent, period, rate, volunteers, world.ActorUserId, token),
                ct);
        }

        var knobs = await shifts.GetKnobsAsync(activeEvent.Id, ct);
        if (knobs?.IsShiftBrowsingOpen != true)
            world.Report("shifts: shift browsing is closed for the active event, so volunteers can't see the demo rotas. Open it in the event's shift settings if you want them to.");
    }

    public async Task RetireAsync(IDemoWorld world, CancellationToken ct)
    {
        var crowd = (await world.MadeAsync(DemoCast.HumanKeyPrefix, ct)).Values.ToHashSet();
        foreach (var (key, rotaId) in await world.MadeAsync(RotaKeyPrefix, ct))
        {
            var rota = await FindAsync(rotaId, ct);
            if (rota is null)
            {
                await world.MarkRetiredAsync(key, ct);
                continue;
            }

            // Only the cast's own signups are bailed; a tester's confirmed signup keeps the rota.
            foreach (var signup in rota.Shifts.SelectMany(s => s.Signups)
                         .Where(s => s.Status == SignupStatus.Confirmed && crowd.Contains(s.UserId)))
            {
                await signups.BailAsync(signup.Id, signup.UserId, "Demo world reset");
            }

            var deleted = await shifts.DeleteRotaAsync(rotaId, world.ActorUserId);
            if (deleted.ErrorKey is null)
                await world.MarkRetiredAsync(key, ct);
            else
                await world.MarkLeftBehindAsync(key, "a tester has a confirmed signup on it", ct);
        }
    }

    private static IEnumerable<(string TeamKey, RotaPeriod Period, double Rate)> Rotas()
    {
        foreach (var team in TeamsDemo.Teams.Where(t => t.ParentKey is null))
        {
            var good = WellStaffed.Contains(team.Key);
            yield return (team.Key, RotaPeriod.Build, good ? 0.85 : 0.55);
            yield return (team.Key, RotaPeriod.Event, good ? 0.9 : 0.6);
            yield return (team.Key, RotaPeriod.Strike, 0.2);
        }

        foreach (var (teamKey, rate) in SubteamRates)
            yield return (teamKey, RotaPeriod.Event, rate);
    }

    private async Task<ShiftAdminRota?> FindAsync(Guid rotaId, CancellationToken ct) =>
        (await rotaRows.GetRotaRowsAsync([rotaId], ct)).SingleOrDefault();

    /// <summary>
    /// Creates the rota on the planned id, then adds shifts if it has none and signups to shifts that have none, so a
    /// run cut off part way is finished by the next one.
    /// </summary>
    private async Task<Guid> FillAsync(
        Guid rotaId, string key, Guid teamId, EventSettingsInfo activeEvent, RotaPeriod period, double rate,
        IReadOnlyList<Guid> volunteers, Guid actorUserId, CancellationToken ct)
    {
        var rng = new Random(StableSeed(key));
        var rota = await FindAsync(rotaId, ct);
        if (rota is null)
        {
            await shifts.CreateRotaAsync(new Rota
            {
                Id = rotaId,
                TeamId = teamId,
                EventSettingsId = activeEvent.Id,
                Name = $"{period} (demo)",
                Priority = ShiftPriority.Normal,
                // Approval so self-signups stay Pending: a Public rota confirms them all, leaving nothing to refuse.
                Policy = SignupPolicy.RequireApproval,
                Period = period,
                IsVisibleToVolunteers = true,
                CreatedAt = clock.GetCurrentInstant(),
            });
            rota = await FindAsync(rotaId, ct)
                ?? throw new InvalidOperationException($"Demo rota {key} vanished right after it was made.");
        }

        if (rota.Shifts.Count == 0)
        {
            await AddShiftsAsync(rotaId, teamId, period, activeEvent, rng);
            rota = await FindAsync(rotaId, ct) ?? rota;
        }

        // Each rota draws on its own slice of the crowd, the way people stick to one department.
        var pool = volunteers.Where((_, i) => i % 8 == StableSeed(key) % 8).ToList();
        if (pool.Count == 0)
            return rotaId;

        var bailOrRefuse = 0;
        foreach (var shift in rota.Shifts.Where(s => s.Signups.Count == 0))
        {
            var picked = pool.OrderBy(_ => rng.Next()).Take(shift.Shift.MaxVolunteers + 2).ToList();
            var confirmed = (int)Math.Round(shift.Shift.MaxVolunteers * rate);
            for (var i = 0; i < picked.Count; i++)
            {
                if (i < confirmed)
                {
                    await signups.VoluntellAsync(picked[i], shift.Shift.Id, actorUserId);
                    continue;
                }

                var signup = await signups.SignUpAsync(picked[i], shift.Shift.Id);
                if (signup.SignupId is not { } signupId || bailOrRefuse >= 2)
                    continue;

                // A couple per rota end bailed or refused, so the dashboard's filters have something to show.
                if (bailOrRefuse++ % 2 == 0)
                    await signups.BailAsync(signupId, picked[i], "Demo: plans changed");
                else
                    await signups.RefuseAsync(signupId, actorUserId, "Demo: shift already covered");
            }
        }

        return rotaId;
    }

    // All-day Build and Strike shifts get distinct days: two on one day show up as duplicate options in the range form.
    // Days follow the event's own windows, the same bounds CreateShiftAsync checks; an empty window gets no shifts.
    private async Task AddShiftsAsync(Guid rotaId, Guid teamId, RotaPeriod period, EventSettingsInfo activeEvent, Random rng)
    {
        var (first, last) = period switch
        {
            RotaPeriod.Build => (activeEvent.BuildStartOffset, -1),
            RotaPeriod.Strike => (activeEvent.EventEndOffset + 1, activeEvent.StrikeEndOffset),
            _ => (0, activeEvent.EventEndOffset),
        };
        if (last < first)
            return;

        var days = Enumerable.Range(first, last - first + 1).ToList();
        var isAllDay = period != RotaPeriod.Event;
        var count = rng.Next(8, 13);
        var offsets = isAllDay
            ? days.OrderBy(_ => rng.Next()).Take(Math.Min(count, days.Count)).ToList()
            : Enumerable.Range(0, count).Select(_ => days[rng.Next(days.Count)]).ToList();

        foreach (var dayOffset in offsets)
        {
            var min = rng.Next(2, 6);
            var result = await shiftCreation.CreateShiftAsync(new CreateShiftInput(
                rotaId, teamId, Description: null, dayOffset,
                isAllDay ? LocalTime.Midnight : new LocalTime(rng.Next(8, 20), 0),
                isAllDay ? 24 : rng.Next(2, 9),
                min, min + rng.Next(1, 4),
                AdminOnly: false, IsAllDay: isAllDay));
            if (!result.Succeeded)
                throw new InvalidOperationException($"Could not add a demo shift: {result.Message}");
        }
    }

    // string.GetHashCode changes per process; this doesn't, so a rerun draws the same shape.
    private static int StableSeed(string key) =>
        key.Aggregate(17, (hash, c) => unchecked(hash * 31 + c)) & int.MaxValue;
}
