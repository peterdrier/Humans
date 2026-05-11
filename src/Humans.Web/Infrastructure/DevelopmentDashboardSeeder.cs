using Humans.Application.Helpers;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
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
public sealed class DevelopmentDashboardSeeder
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

    private readonly IShiftManagementService _shiftManagementService;
    private readonly IShiftSignupService _shiftSignupService;
    private readonly ITeamService _teamService;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDashboardSeeder> _logger;

    public DevelopmentDashboardSeeder(
        IShiftManagementService shiftManagementService,
        IShiftSignupService shiftSignupService,
        ITeamService teamService,
        IUserEmailService userEmailService,
        IUserService userService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<DevelopmentDashboardSeeder> logger)
    {
        _shiftManagementService = shiftManagementService;
        _shiftSignupService = shiftSignupService;
        _teamService = teamService;
        _userEmailService = userEmailService;
        _userService = userService;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DashboardSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await _shiftManagementService.GetByIdAsync(SeededEventId);
        if (existing is not null)
        {
            _logger.LogInformation("Dashboard seed already applied (event '{EventName}' exists).", SeededEventName);
            return new DashboardSeedResult(AlreadySeeded: true, 0, 0, 0, 0);
        }

        var now = _clock.GetCurrentInstant();
        var todayUtc = now.InUtc().Date;

        // Deactivate any existing active event so ours becomes the one resolved by GetActiveAsync.
        var existingActive = await _shiftManagementService.GetActiveAsync();
        if (existingActive is not null)
        {
            existingActive.IsActive = false;
            await _shiftManagementService.UpdateAsync(existingActive);
        }

        var es = new EventSettings
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
            // Enable volunteer browsing so /Shifts/ and /Teams/{slug}/Shifts render
            // the seeded rotas side-by-side with /Shifts/Dashboard for QA comparisons.
            IsShiftBrowsingOpen = true,
            CreatedAt = now.Minus(Duration.FromDays(30)),
            UpdatedAt = now,
        };
        await _shiftManagementService.CreateAsync(es);

        // Teams: create parents, then subteams. Goes through ITeamService so slug
        // generation, validation, and cache seeding match production.
        var teamsCreated = 0;
        var parentTeams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var name in ParentTeamNames)
        {
            var created = await _teamService.CreateTeamAsync(
                $"{name}{DevTeamNameSuffix}",
                description: null,
                requiresApproval: true,
                cancellationToken: cancellationToken);
            parentTeams[name] = created;
            teamsCreated++;
        }

        var subteams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var (parentName, subNames) in Subteams)
        {
            foreach (var subName in subNames)
            {
                var created = await _teamService.CreateTeamAsync(
                    $"{subName}{DevTeamNameSuffix}",
                    description: null,
                    requiresApproval: true,
                    parentTeamId: parentTeams[parentName].Id,
                    cancellationToken: cancellationToken);
                subteams[subName] = created;
                teamsCreated++;
            }
        }

        // Rotas: one per period per parent team, plus Event-period rotas on each subteam.
        // Subteam fill rates are tuned to produce a mix of low/mid/high % chips in the expand view.
        var subteamRates = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            // Participant Wellness: one low (drives the red chip), one mid, one high.
            ["Consent"] = 0.3,
            ["Welfare"] = 0.6,
            ["Ohana House"] = 0.85,
            // Ice and Water: one mid, one high.
            ["Ice Ice Baby"] = 0.55,
            ["Icemakers"] = 0.9,
        };

        var rotaConfigs = new List<(Team Team, RotaPeriod Period, string Label, double ConfirmedRate)>();
        foreach (var parent in parentTeams.Values)
        {
            var isWellStaffed = parent.Name.StartsWith("Gate", StringComparison.Ordinal)
                             || parent.Name.StartsWith("Site Operations", StringComparison.Ordinal);
            rotaConfigs.Add((parent, RotaPeriod.Build, "Build", isWellStaffed ? 0.85 : 0.55));
            rotaConfigs.Add((parent, RotaPeriod.Event, "Event", isWellStaffed ? 0.9 : 0.6));
            rotaConfigs.Add((parent, RotaPeriod.Strike, "Strike", 0.2));
        }
        foreach (var (subName, rate) in subteamRates)
        {
            rotaConfigs.Add((subteams[subName], RotaPeriod.Event, "Event", rate));
        }

        var allRotas = new List<(Rota Rota, double ConfirmedRate)>();
        foreach (var (team, period, label, confirmedRate) in rotaConfigs)
        {
            var rota = new Rota
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                EventSettingsId = es.Id,
                Name = $"{team.Name} - {label}",
                Priority = ShiftPriority.Normal,
                // Public so the volunteer-facing /Shifts/ page lists them for any
                // logged-in user - otherwise the browse view hides approval-only
                // rotas and QA can't compare across the three surfaces.
                Policy = SignupPolicy.Public,
                Period = period,
                IsVisibleToVolunteers = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _shiftManagementService.CreateRotaAsync(rota);
            allRotas.Add((rota, confirmedRate));
        }

        // Shifts: 8-12 per rota with varied min/max/duration and day offsets by period.
        // All-day rotas (Build/Strike) get distinct offsets - duplicate same-date all-day
        // shifts surface as duplicate dropdown options in the range-signup form and
        // confuse the confirmation modal's overlap math.
        var shifts = new List<(Shift Shift, double ConfirmedRate)>();
        foreach (var (rota, rate) in allRotas)
        {
            var requested = _rng.Next(8, 13);
            var isAllDay = rota.Period != RotaPeriod.Event;
            var (offsetMin, offsetMaxExclusive) = rota.Period switch
            {
                RotaPeriod.Build => (-14, 0),
                RotaPeriod.Event => (0, 7),
                RotaPeriod.Strike => (7, 10),
                _ => (0, 1),
            };
            var allowedOffsets = Enumerable.Range(offsetMin, offsetMaxExclusive - offsetMin).ToList();
            var dayOffsets = isAllDay
                ? allowedOffsets.OrderBy(_ => _rng.Next()).Take(Math.Min(requested, allowedOffsets.Count)).ToList()
                : Enumerable.Range(0, requested).Select(_ => allowedOffsets[_rng.Next(allowedOffsets.Count)]).ToList();
            foreach (var dayOffset in dayOffsets)
            {
                var min = _rng.Next(2, 6);
                var max = min + _rng.Next(1, 4);
                var shift = new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = rota.Id,
                    DayOffset = dayOffset,
                    // All-day rows: StartTime/Duration are don't-care; GetAbsoluteStart/End
                    // compute bounds from Shift.AllDayWindowStart/End. Store midnight/24h sentinel.
                    StartTime = isAllDay
                        ? LocalTime.Midnight
                        : new LocalTime(_rng.Next(8, 20), 0),
                    Duration = isAllDay
                        ? Duration.FromHours(24)
                        : Duration.FromHours(_rng.Next(2, 9)),
                    MinVolunteers = min,
                    MaxVolunteers = max,
                    IsAllDay = isAllDay,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await _shiftManagementService.CreateShiftAsync(shift);
                shifts.Add((shift, rate));
            }
        }

        // Users: keep the cohort small (~120) - large enough to show activity, small enough to keep the dev seed fast.
        // Users are an Identity-framework concern; we create them via UserManager, the
        // standard pattern for framework-owned types. Passwords are intentionally omitted
        // - these accounts are never logged into; they exist purely as dashboard data.
        var totalUsers = 120;

        var users = new List<User>();
        for (var i = 0; i < totalUsers; i++)
        {
            var display = $"Dev Human {i:D3}";
            var email = $"{DevUserEmailPrefix}{i:D3}{DevUserEmailSuffix}";
            var createdAt = now.Minus(Duration.FromDays(_rng.Next(30, 400)));
            var lastLoginDaysAgo = _rng.Next(0, 85);
            // Id MUST be set explicitly: UserManager's username uniqueness validator
            // reads user.UserName before EF assigns the PK, and the override returns
            // Id.ToString(). Without an explicit Id, every loop iteration resolves
            // to UserName = Guid.Empty.ToString() and fails on the second insert.
            var userId = Guid.NewGuid();
            var user = new User
            {
                Id = userId,
                DisplayName = display,
                CreatedAt = createdAt,
                LastLoginAt = now.Minus(Duration.FromDays(lastLoginDaysAgo)).Minus(Duration.FromHours(_rng.Next(0, 23))),
            };

            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Failed to create seeded dev user '{email}': {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
            }

            await _userEmailService.AddVerifiedEmailAsync(user.Id, email, cancellationToken);

            users.Add(user);
        }

        // Coordinators: 2 per parent team, routed via ITeamService.AddSeededMemberAsync so
        // we never touch TeamMembers directly. Infrastructure coords logged in 9 days ago
        // (stale) to drive the red chip on the dashboard.
        foreach (var parent in parentTeams.Values)
        {
            var isInfrastructure = parent.Name.StartsWith("Infrastructure", StringComparison.Ordinal);
            var coordLastLogin = isInfrastructure
                ? now.Minus(Duration.FromDays(9))
                : now.Minus(Duration.FromHours(_rng.Next(1, 48)));

            for (var i = 0; i < 2; i++)
            {
                var coord = users[_rng.Next(users.Count)];
                coord.LastLoginAt = coordLastLogin;
                var updateResult = await _userManager.UpdateAsync(coord);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to update LastLoginAt for seeded coord '{coord.Email}': {string.Join(", ", updateResult.Errors.Select(e => e.Description))}");
                }

                try
                {
                    await _teamService.AddSeededMemberAsync(
                        parent.Id, coord.Id, TeamMemberRole.Coordinator, now.Minus(Duration.FromDays(60)),
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // The RNG may pick the same user as a coord for multiple teams or twice for
                    // the same team; ignore duplicate membership (it's just demo data).
                }
            }
        }

        // Signups: rate-driven Confirmed vs Pending, plus a few Bailed / Refused, and some stale Pending.
        var signupsCreated = 0;
        var pickedSignups = new HashSet<(Guid ShiftId, Guid UserId)>();
        foreach (var (shift, rate) in shifts)
        {
            var targetConfirmed = (int)Math.Round(shift.MaxVolunteers * rate);
            for (var i = 0; i < targetConfirmed && i < shift.MaxVolunteers; i++)
            {
                var user = users[_rng.Next(users.Count)];
                var key = (shift.Id, user.Id);
                if (!pickedSignups.Add(key)) continue;
                var signup = await _shiftSignupService.VoluntellAsync(user.Id, shift.Id, users[0].Id);
                if (signup.Success)
                    signupsCreated++;
            }

            // A couple of pending per shift to create visible pending load.
            for (var i = 0; i < 2; i++)
            {
                var user = users[_rng.Next(users.Count)];
                var key = (shift.Id, user.Id);
                if (!pickedSignups.Add(key)) continue;
                var signup = await _shiftSignupService.SignUpAsync(user.Id, shift.Id);
                if (signup.Success)
                    signupsCreated++;
            }
        }

        // A few Bailed / Refused to exercise filters.
        for (var i = 0; i < 8 && i < shifts.Count; i++)
        {
            var (shift, _) = shifts[_rng.Next(shifts.Count)];
            var user = users[_rng.Next(users.Count)];
            var key = (shift.Id, user.Id);
            if (!pickedSignups.Add(key)) continue;
            var signup = i % 2 == 0
                ? await _shiftSignupService.VoluntellAsync(user.Id, shift.Id, users[0].Id)
                : await _shiftSignupService.SignUpAsync(user.Id, shift.Id);
            if (!signup.Success || signup.Signup is null)
                continue;

            var final = i % 2 == 0
                ? await _shiftSignupService.BailAsync(signup.Signup.Id, user.Id, "Seeded for demo")
                : await _shiftSignupService.RefuseAsync(signup.Signup.Id, users[0].Id, "Seeded for demo");
            if (final.Success)
                signupsCreated++;
        }

        _logger.LogInformation(
            "Dashboard seed complete: {Teams} teams, {Users} users, {Shifts} shifts, {Signups} signups.",
            teamsCreated, users.Count, shifts.Count, signupsCreated);

        return new DashboardSeedResult(
            AlreadySeeded: false,
            TeamsCreated: teamsCreated,
            UsersCreated: users.Count,
            ShiftsCreated: shifts.Count,
            SignupsCreated: signupsCreated);
    }

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
        var eventsDeleted = await _shiftManagementService.DeleteEventAsync(SeededEventId, cancellationToken);

        // Dev users - match the seed marker on UserEmails.
        var devUserIds = await _userEmailService.GetUserIdsByEmailPrefixAndSuffixAsync(
            DevUserEmailPrefix, DevUserEmailSuffix, cancellationToken);

        if (devUserIds.Count > 0)
        {
            await _shiftSignupService.DeleteAllForUsersAsync(devUserIds, cancellationToken);
        }

        var teamsDeleted = 0;
        foreach (var slug in SeededTeamSlugsForDelete())
        {
            var team = await _teamService.GetTeamBySlugAsync(slug, cancellationToken);
            if (team is null)
                continue;

            if (await _teamService.PermanentlyDeleteTeamAsync(team.Id, cancellationToken))
                teamsDeleted++;
        }

        var usersDeleted = devUserIds.Count == 0
            ? 0
            : await _userService.DeleteUsersAsync(devUserIds, cancellationToken);

        _logger.LogInformation(
            "Dashboard seed reset complete: {Events} events, {Teams} teams, {Users} users deleted.",
            eventsDeleted, teamsDeleted, usersDeleted);

        return new DashboardResetResult(eventsDeleted, teamsDeleted, usersDeleted);
    }

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
