using Humans.Application.Helpers;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Web.Infrastructure;

public sealed record DashboardSeedResult(
    bool AlreadySeeded,
    int TeamsCreated,
    int UsersCreated,
    int ShiftsCreated,
    int SignupsCreated);

public sealed record DashboardResetResult(
    int EventsDeleted,
    int TeamsDeleted,
    int UsersDeleted);

/// <summary>
/// Seeds deterministic-ish demo data for the volunteer coordinator dashboard.
/// Gated to IsDevelopment() only by the calling controller. Not safe to run in QA/prod.
///
/// All writes go through the owning section services. The controller keeps destructive
/// reset behind full Admin authorization.
/// </summary>
public sealed class DevelopmentDashboardSeeder(
    IShiftManagementService shiftManagementService,
    IShiftSignupService shiftSignupService,
    ITeamService teamService,
    IUserEmailService userEmailService,
    IUserService userService,
    UserManager<User> userManager,
    IClock clock,
    ILogger<DevelopmentDashboardSeeder> logger)
{
    private static readonly Guid SeededEventId = Guid.Parse("2f38bf0c-46fd-4f7d-a05b-7ec9e26d8e6b");

    private const string SeededEventName = "Seeded Elsewhere 2026 (dev)";
    private const string DevTeamNameSuffix = " (dev)";
    private const string DevUserEmailSuffix = "@seed.local";
    private const string DevUserEmailPrefix = "dev-human-";

    // Match real, non-hidden parent departments from the production Teams list
    // so the dashboard seed reflects terminology the coordinators actually use.
    private static readonly string[] ParentTeamNames =
    [
        "Gate",
        "Infrastructure",
        "Participant Wellness",
        "Ice and Water",
        "Site Operations",
        "Production & Logistics",
        "Werkhaus/Barrio",
        "Power",
    ];

    // Subteams chosen to exercise the department-expand UI:
    //   - Participant Wellness has 3 subteams + "Direct" row (wide expand).
    //   - Ice and Water has 2 subteams.
    //   - Other parents have none (exercise the per-period row path).
    private static readonly (string Parent, string[] Subteams)[] Subteams =
    [
        ("Participant Wellness", ["Consent", "Welfare", "Ohana House"]),
        ("Ice and Water", ["Ice Ice Baby", "Icemakers"]),
    ];

    // A deterministic RNG so reruns on a clean DB produce the same-ish shape.
    private readonly Random _rng = new(42);

    public async Task<DashboardSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await shiftManagementService.GetByIdAsync(SeededEventId);
        if (existing is not null)
        {
            logger.LogInformation("Dashboard seed already applied (event '{EventName}' exists).", SeededEventName);
            return new DashboardSeedResult(AlreadySeeded: true, 0, 0, 0, 0);
        }

        var now = clock.GetCurrentInstant();
        var todayUtc = now.InUtc().Date;

        var seedEvent = await CreateSeedEventAsync(now, todayUtc);

        var teams = await CreateSeedTeamsAsync(cancellationToken);

        var subteamRates = BuildSubteamRates();
        var rotas = await CreateSeedRotasAsync(seedEvent, teams, subteamRates, now);

        var shifts = await CreateSeedShiftsAsync(rotas, now);

        var users = await CreateSeedUsersAsync(now, cancellationToken);

        await AssignSeedCoordinatorsAsync(teams.ParentTeams.Values, users, now, cancellationToken);

        // Per-user team assignments: ~80% stick to a single parent team, ~20% switch
        // primary→secondary at a given day offset. Produces realistic block-shaped rows
        // in the volunteer-tracking export instead of fully-scattered signups.
        var userAssignments = BuildUserAssignments(users, teams.ParentTeams.Values.ToList());

        // Rota → parent team Id. Parent rotas map to themselves; subteam rotas map to their parent.
        var rotaToParentTeamId = BuildRotaParentMap(rotas, teams, subteamRates);

        // Signups: rate-driven Confirmed vs Pending, plus a few Bailed / Refused, and some stale Pending.
        // Candidate filter: only users whose effective team for this shift's day matches the shift's parent.
        var signupsCreated = await CreateSeedSignupsAsync(shifts, users, userAssignments, rotaToParentTeamId);

        logger.LogInformation(
            "Dashboard seed complete: {Teams} teams, {Users} users, {Shifts} shifts, {Signups} signups.",
            teams.Count, users.Count, shifts.Count, signupsCreated);

        return new DashboardSeedResult(
            AlreadySeeded: false,
            TeamsCreated: teams.Count,
            UsersCreated: users.Count,
            ShiftsCreated: shifts.Count,
            SignupsCreated: signupsCreated);
    }

    private async Task<EventSettings> CreateSeedEventAsync(Instant now, LocalDate todayUtc)
    {
        // Deactivate any existing active event so ours becomes the one resolved by GetActiveAsync.
        var existingActive = await shiftManagementService.GetActiveAsync();
        if (existingActive is not null)
        {
            existingActive.IsActive = false;
            await shiftManagementService.UpdateAsync(existingActive);
        }

        var eventSettings = new EventSettings
        {
            Id = SeededEventId,
            EventName = SeededEventName,
            Year = todayUtc.Year,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = todayUtc.PlusDays(60),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            IsShiftBrowsingOpen = true,
            CreatedAt = now.Minus(Duration.FromDays(30)),
            UpdatedAt = now,
        };
        await shiftManagementService.CreateAsync(eventSettings);

        return eventSettings;
    }

    private async Task<SeededTeams> CreateSeedTeamsAsync(CancellationToken cancellationToken)
    {
        var parentTeams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var name in ParentTeamNames)
        {
            parentTeams[name] = await teamService.CreateTeamAsync(
                $"{name}{DevTeamNameSuffix}",
                description: null,
                requiresApproval: true,
                cancellationToken: cancellationToken);
        }

        var subteams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var (parentName, subNames) in Subteams)
        {
            foreach (var subName in subNames)
            {
                subteams[subName] = await teamService.CreateTeamAsync(
                    $"{subName}{DevTeamNameSuffix}",
                    description: null,
                    requiresApproval: true,
                    parentTeamId: parentTeams[parentName].Id,
                    cancellationToken: cancellationToken);
            }
        }

        return new SeededTeams(parentTeams, subteams);
    }

    private static Dictionary<string, double> BuildSubteamRates() => new(StringComparer.Ordinal)
    {
        ["Consent"] = 0.3,
        ["Welfare"] = 0.6,
        ["Ohana House"] = 0.85,
        ["Ice Ice Baby"] = 0.55,
        ["Icemakers"] = 0.9,
    };

    private async Task<List<SeededRota>> CreateSeedRotasAsync(
        EventSettings eventSettings,
        SeededTeams teams,
        IReadOnlyDictionary<string, double> subteamRates,
        Instant now)
    {
        var rotas = new List<SeededRota>();
        foreach (var config in BuildRotaConfigs(teams, subteamRates))
        {
            var rota = new Rota
            {
                Id = Guid.NewGuid(),
                TeamId = config.Team.Id,
                EventSettingsId = eventSettings.Id,
                Name = $"{config.Team.Name} - {config.Label}",
                Priority = ShiftPriority.Normal,
                Policy = SignupPolicy.Public,
                Period = config.Period,
                IsVisibleToVolunteers = true,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await shiftManagementService.CreateRotaAsync(rota);
            rotas.Add(new SeededRota(rota, config.ConfirmedRate));
        }

        return rotas;
    }

    private static IEnumerable<RotaConfig> BuildRotaConfigs(
        SeededTeams teams,
        IReadOnlyDictionary<string, double> subteamRates)
    {
        foreach (var parent in teams.ParentTeams.Values)
        {
            var isWellStaffed = parent.Name.StartsWith("Gate", StringComparison.Ordinal)
                             || parent.Name.StartsWith("Site Operations", StringComparison.Ordinal);
            yield return new RotaConfig(parent, RotaPeriod.Build, "Build", isWellStaffed ? 0.85 : 0.55);
            yield return new RotaConfig(parent, RotaPeriod.Event, "Event", isWellStaffed ? 0.9 : 0.6);
            yield return new RotaConfig(parent, RotaPeriod.Strike, "Strike", 0.2);
        }

        foreach (var (subName, rate) in subteamRates)
        {
            yield return new RotaConfig(teams.Subteams[subName], RotaPeriod.Event, "Event", rate);
        }
    }

    private async Task<List<SeededShift>> CreateSeedShiftsAsync(IReadOnlyList<SeededRota> rotas, Instant now)
    {
        var shifts = new List<SeededShift>();
        foreach (var seededRota in rotas)
        {
            shifts.AddRange(await CreateSeedShiftsForRotaAsync(seededRota, now));
        }

        return shifts;
    }

    private async Task<List<SeededShift>> CreateSeedShiftsForRotaAsync(SeededRota seededRota, Instant now)
    {
        var shifts = new List<SeededShift>();
        var isAllDay = seededRota.Rota.Period != RotaPeriod.Event;
        foreach (var dayOffset in PickSeedShiftDayOffsets(seededRota.Rota.Period, isAllDay))
        {
            var draft = CreateSeedShiftDraft(seededRota.Rota, dayOffset, isAllDay);
            var result = await shiftManagementService.CreateShiftAsync(draft.Input);
            if (!result.Succeeded || result.ShiftId is null)
                throw new InvalidOperationException(result.Message);

            shifts.Add(new SeededShift(
                new Shift
                {
                    Id = result.ShiftId.Value,
                    RotaId = seededRota.Rota.Id,
                    DayOffset = dayOffset,
                    StartTime = draft.StartTime,
                    Duration = draft.Duration,
                    MinVolunteers = draft.MinVolunteers,
                    MaxVolunteers = draft.MaxVolunteers,
                    IsAllDay = isAllDay,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                seededRota.ConfirmedRate));
        }

        return shifts;
    }

    private List<int> PickSeedShiftDayOffsets(RotaPeriod period, bool isAllDay)
    {
        var requested = _rng.Next(8, 13);
        var (offsetMin, offsetMaxExclusive) = period switch
        {
            RotaPeriod.Build => (-14, 0),
            RotaPeriod.Event => (0, 7),
            RotaPeriod.Strike => (7, 10),
            _ => (0, 1),
        };

        var allowedOffsets = Enumerable.Range(offsetMin, offsetMaxExclusive - offsetMin).ToList();
        return isAllDay
            ? allowedOffsets.OrderBy(_ => _rng.Next()).Take(Math.Min(requested, allowedOffsets.Count)).ToList()
            : Enumerable.Range(0, requested).Select(_ => allowedOffsets[_rng.Next(allowedOffsets.Count)]).ToList();
    }

    private SeedShiftDraft CreateSeedShiftDraft(Rota rota, int dayOffset, bool isAllDay)
    {
        var min = _rng.Next(2, 6);
        var max = min + _rng.Next(1, 4);
        var startTime = isAllDay
            ? LocalTime.Midnight
            : new LocalTime(_rng.Next(8, 20), 0);
        var duration = isAllDay
            ? Duration.FromHours(24)
            : Duration.FromHours(_rng.Next(2, 9));

        return new SeedShiftDraft(
            new CreateShiftInput(
                rota.Id,
                rota.TeamId,
                null,
                dayOffset,
                startTime,
                duration.TotalHours,
                min,
                max,
                AdminOnly: false,
                IsAllDay: isAllDay),
            startTime,
            duration,
            min,
            max);
    }

    private async Task<List<User>> CreateSeedUsersAsync(Instant now, CancellationToken cancellationToken)
    {
        const int TotalUsers = 120;
        var users = new List<User>();

        for (var i = 0; i < TotalUsers; i++)
        {
            var user = CreateSeedUser(now, i);
            var email = $"{DevUserEmailPrefix}{i:D3}{DevUserEmailSuffix}";
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
                throw CreateIdentityException($"Failed to create seeded dev user '{email}'", createResult);

            await userEmailService.AddVerifiedEmailAsync(user.Id, email, cancellationToken);
            users.Add(user);
        }

        return users;
    }

    private User CreateSeedUser(Instant now, int index)
    {
        var lastLoginDaysAgo = _rng.Next(0, 85);
#pragma warning disable HUM_USER_DISPLAYNAME // Development dashboard seeding writes the legacy Identity fallback column.
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Dev Human {index:D3}",
            CreatedAt = now.Minus(Duration.FromDays(_rng.Next(30, 400))),
            LastLoginAt = now.Minus(Duration.FromDays(lastLoginDaysAgo)).Minus(Duration.FromHours(_rng.Next(0, 23))),
        };
#pragma warning restore HUM_USER_DISPLAYNAME

        return user;
    }

    private async Task AssignSeedCoordinatorsAsync(
        IEnumerable<Team> parentTeams,
        IReadOnlyList<User> users,
        Instant now,
        CancellationToken cancellationToken)
    {
        foreach (var parent in parentTeams)
        {
            var coordLastLogin = parent.Name.StartsWith("Infrastructure", StringComparison.Ordinal)
                ? now.Minus(Duration.FromDays(9))
                : now.Minus(Duration.FromHours(_rng.Next(1, 48)));

            for (var i = 0; i < 2; i++)
            {
                await AssignSeedCoordinatorAsync(parent, users, coordLastLogin, now, cancellationToken);
            }
        }
    }

    private async Task AssignSeedCoordinatorAsync(
        Team parent,
        IReadOnlyList<User> users,
        Instant coordLastLogin,
        Instant now,
        CancellationToken cancellationToken)
    {
        var coord = users[_rng.Next(users.Count)];
        coord.LastLoginAt = coordLastLogin;
        var updateResult = await userManager.UpdateAsync(coord);
        if (!updateResult.Succeeded)
            throw CreateIdentityException($"Failed to update LastLoginAt for seeded coord '{coord.Id}'", updateResult);

        try
        {
            await teamService.AddSeededMemberAsync(
                parent.Id,
                coord.Id,
                TeamMemberRole.Coordinator,
                now.Minus(Duration.FromDays(60)),
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Duplicate seeded coordinator membership is harmless demo data noise.
        }
    }

    private Dictionary<Guid, UserAssignment> BuildUserAssignments(
        IReadOnlyList<User> users,
        IReadOnlyList<Team> assignableTeams)
    {
        var assignments = new Dictionary<Guid, UserAssignment>(users.Count);
        foreach (var user in users)
        {
            assignments[user.Id] = BuildUserAssignment(assignableTeams);
        }

        return assignments;
    }

    private UserAssignment BuildUserAssignment(IReadOnlyList<Team> assignableTeams)
    {
        var primary = assignableTeams[_rng.Next(assignableTeams.Count)];
        if (_rng.NextDouble() >= 0.20)
            return new UserAssignment(primary.Id, null, null);

        var secondary = PickSecondaryTeam(assignableTeams, primary.Id);
        return new UserAssignment(primary.Id, secondary.Id, _rng.Next(-7, 5));
    }

    private Team PickSecondaryTeam(IReadOnlyList<Team> assignableTeams, Guid primaryTeamId)
    {
        Team secondary;
        do
        {
            secondary = assignableTeams[_rng.Next(assignableTeams.Count)];
        }
        while (secondary.Id == primaryTeamId);

        return secondary;
    }

    private static Dictionary<Guid, Guid> BuildRotaParentMap(
        IReadOnlyList<SeededRota> rotas,
        SeededTeams teams,
        IReadOnlyDictionary<string, double> subteamRates)
    {
        var teamIdToParent = teams.ParentTeams.Values.ToDictionary(t => t.Id, t => t.Id);
        foreach (var (subName, _) in subteamRates)
        {
            var subteam = teams.Subteams[subName];
            teamIdToParent[subteam.Id] = subteam.ParentTeamId ?? subteam.Id;
        }

        return rotas.ToDictionary(
            seededRota => seededRota.Rota.Id,
            seededRota => ResolveParentTeamId(teamIdToParent, seededRota.Rota.TeamId));
    }

    private static Guid ResolveParentTeamId(IReadOnlyDictionary<Guid, Guid> teamIdToParent, Guid teamId)
        => teamIdToParent.TryGetValue(teamId, out var parentTeamId) ? parentTeamId : teamId;

    private async Task<int> CreateSeedSignupsAsync(
        IReadOnlyList<SeededShift> shifts,
        IReadOnlyList<User> users,
        IReadOnlyDictionary<Guid, UserAssignment> userAssignments,
        IReadOnlyDictionary<Guid, Guid> rotaToParentTeamId)
    {
        var signupsCreated = 0;
        var pickedSignups = new HashSet<(Guid ShiftId, Guid UserId)>();
        foreach (var seededShift in shifts)
        {
            var candidates = FindCandidatesForShift(
                seededShift.Shift,
                users,
                userAssignments,
                rotaToParentTeamId[seededShift.Shift.RotaId]);
            if (candidates.Count == 0)
                continue;

            signupsCreated += await CreateConfirmedSignupsAsync(seededShift, candidates, users[0].Id, pickedSignups);
            signupsCreated += await CreatePendingSignupsAsync(seededShift.Shift, candidates, pickedSignups);
        }

        signupsCreated += await CreateFinalizedDemoSignupsAsync(shifts, users, pickedSignups);
        return signupsCreated;
    }

    private static List<User> FindCandidatesForShift(
        Shift shift,
        IEnumerable<User> users,
        IReadOnlyDictionary<Guid, UserAssignment> userAssignments,
        Guid shiftTeamId)
    {
        return users
            .Where(user => EffectiveTeamForDay(userAssignments[user.Id], shift.DayOffset) == shiftTeamId)
            .ToList();
    }

    private static Guid EffectiveTeamForDay(UserAssignment assignment, int dayOffset)
        => assignment.SecondaryTeamId is Guid secondaryTeamId
        && assignment.SwitchDayOffset is int switchDay
        && dayOffset >= switchDay
            ? secondaryTeamId
            : assignment.PrimaryTeamId;

    private async Task<int> CreateConfirmedSignupsAsync(
        SeededShift seededShift,
        IReadOnlyList<User> candidates,
        Guid voluntellerId,
        HashSet<(Guid ShiftId, Guid UserId)> pickedSignups)
    {
        var signupsCreated = 0;
        var targetConfirmed = (int)Math.Round(seededShift.Shift.MaxVolunteers * seededShift.ConfirmedRate);
        for (var i = 0; i < targetConfirmed && i < seededShift.Shift.MaxVolunteers; i++)
        {
            var user = candidates[_rng.Next(candidates.Count)];
            if (!pickedSignups.Add((seededShift.Shift.Id, user.Id)))
                continue;

            var signup = await shiftSignupService.VoluntellAsync(user.Id, seededShift.Shift.Id, voluntellerId);
            if (signup.Success)
                signupsCreated++;
        }

        return signupsCreated;
    }

    private async Task<int> CreatePendingSignupsAsync(
        Shift shift,
        IReadOnlyList<User> candidates,
        HashSet<(Guid ShiftId, Guid UserId)> pickedSignups)
    {
        var signupsCreated = 0;
        for (var i = 0; i < 2; i++)
        {
            var user = candidates[_rng.Next(candidates.Count)];
            if (!pickedSignups.Add((shift.Id, user.Id)))
                continue;

            var signup = await shiftSignupService.SignUpAsync(user.Id, shift.Id);
            if (signup.Success)
                signupsCreated++;
        }

        return signupsCreated;
    }

    private async Task<int> CreateFinalizedDemoSignupsAsync(
        IReadOnlyList<SeededShift> shifts,
        IReadOnlyList<User> users,
        HashSet<(Guid ShiftId, Guid UserId)> pickedSignups)
    {
        var signupsCreated = 0;
        for (var i = 0; i < 8 && i < shifts.Count; i++)
        {
            if (await TryCreateFinalizedDemoSignupAsync(i, shifts, users, pickedSignups))
                signupsCreated++;
        }

        return signupsCreated;
    }

    private async Task<bool> TryCreateFinalizedDemoSignupAsync(
        int index,
        IReadOnlyList<SeededShift> shifts,
        IReadOnlyList<User> users,
        HashSet<(Guid ShiftId, Guid UserId)> pickedSignups)
    {
        var shift = shifts[_rng.Next(shifts.Count)].Shift;
        var user = users[_rng.Next(users.Count)];
        if (!pickedSignups.Add((shift.Id, user.Id)))
            return false;

        var signup = index % 2 == 0
            ? await shiftSignupService.VoluntellAsync(user.Id, shift.Id, users[0].Id)
            : await shiftSignupService.SignUpAsync(user.Id, shift.Id);
        if (!signup.Success || signup.Signup is null)
            return false;

        var final = index % 2 == 0
            ? await shiftSignupService.BailAsync(signup.Signup.Id, user.Id, "Seeded for demo")
            : await shiftSignupService.RefuseAsync(signup.Signup.Id, users[0].Id, "Seeded for demo");

        return final.Success;
    }

    private static InvalidOperationException CreateIdentityException(string message, IdentityResult result)
        => new($"{message}: {string.Join(", ", result.Errors.Select(e => e.Description))}");

    /// <summary>
    /// Deletes all data previously created by <see cref="SeedAsync"/>: the seeded
    /// event (with its rotas/shifts/signups), known seeded dev teams, and dev
    /// users (email pattern <c>dev-human-*@seed.local</c>). Safe to call on
    /// a DB where no seed has ever run.
    ///
    /// Deletion goes through the owning section services. The caller is responsible
    /// for full Admin authorization before invoking reset.
    /// </summary>
    public async Task<DashboardResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        var eventsDeleted = await shiftManagementService.DeleteEventAsync(SeededEventId, cancellationToken);

        // Dev users - match the seed marker on UserEmails.
        var devUserIds = await userEmailService.GetUserIdsByEmailPrefixAndSuffixAsync(
            DevUserEmailPrefix, DevUserEmailSuffix, cancellationToken);

        if (devUserIds.Count > 0)
        {
            await shiftSignupService.DeleteAllForUsersAsync(devUserIds, cancellationToken);
        }

        var teamsDeleted = 0;
        foreach (var slug in SeededTeamSlugsForDelete())
        {
            var team = await teamService.GetTeamEntityBySlugAsync(slug, cancellationToken);
            if (team is null)
                continue;

            if (await teamService.PermanentlyDeleteTeamAsync(team.Id, cancellationToken))
                teamsDeleted++;
        }

        var usersDeleted = devUserIds.Count == 0
            ? 0
            : await userService.DeleteUsersAsync(devUserIds, cancellationToken);

        logger.LogInformation(
            "Dashboard seed reset complete: {Events} events, {Teams} teams, {Users} users deleted.",
            eventsDeleted, teamsDeleted, usersDeleted);

        return new DashboardResetResult(eventsDeleted, teamsDeleted, usersDeleted);
    }

    /// <summary>
    /// Per-user team assignment for the dashboard seeder. Primary team is always present;
    /// when SecondaryTeamId is set, the user switches from primary→secondary at SwitchDayOffset
    /// (so shifts with DayOffset &gt;= switchDay belong to the secondary).
    /// </summary>
    private sealed record SeededTeams(
        Dictionary<string, Team> ParentTeams,
        Dictionary<string, Team> Subteams)
    {
        public int Count => ParentTeams.Count + Subteams.Count;
    }

    private sealed record RotaConfig(Team Team, RotaPeriod Period, string Label, double ConfirmedRate);

    private sealed record SeededRota(Rota Rota, double ConfirmedRate);

    private sealed record SeededShift(Shift Shift, double ConfirmedRate);

    private sealed record SeedShiftDraft(
        CreateShiftInput Input,
        LocalTime StartTime,
        Duration Duration,
        int MinVolunteers,
        int MaxVolunteers);

    private sealed record UserAssignment(Guid PrimaryTeamId, Guid? SecondaryTeamId, int? SwitchDayOffset);

    private static IEnumerable<string> SeededTeamSlugsForDelete()
    {
        foreach (var (_, subteams) in Subteams)
        {
            foreach (var subteam in subteams)
            {
                yield return SlugHelper.GenerateSlug($"{subteam}{DevTeamNameSuffix}");
            }
        }

        foreach (var parentTeam in ParentTeamNames)
        {
            yield return SlugHelper.GenerateSlug($"{parentTeam}{DevTeamNameSuffix}");
        }
    }
}
