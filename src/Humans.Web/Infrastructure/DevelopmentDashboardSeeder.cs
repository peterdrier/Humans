using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
/// Cross-domain writes (Users, Teams, TeamMembers) go through the owning services:
/// <see cref="UserManager{TUser}"/> for Users and <see cref="ITeamService"/> for
/// Teams/TeamMembers. Shifts-owned tables (EventSettings, Rotas, Shifts, ShiftSignups)
/// are written directly via <see cref="HumansDbContext"/> because this seeder IS the
/// Shifts-test-data setup — the same ownership the <see cref="ShiftManagementService"/>
/// holds in production.
/// </summary>
public sealed class DevelopmentDashboardSeeder
{
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

    private readonly HumansDbContext _dbContext;
    private readonly ITeamService _teamService;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDashboardSeeder> _logger;

    public DevelopmentDashboardSeeder(
        HumansDbContext dbContext,
        ITeamService teamService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<DevelopmentDashboardSeeder> logger)
    {
        _dbContext = dbContext;
        _teamService = teamService;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DashboardSeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await _dbContext.EventSettings
            .FirstOrDefaultAsync(e => e.EventName == SeededEventName, cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation("Dashboard seed already applied (event '{EventName}' exists).", SeededEventName);
            return new DashboardSeedResult(AlreadySeeded: true, 0, 0, 0, 0);
        }

        var now = _clock.GetCurrentInstant();
        var todayUtc = now.InUtc().Date;

        // Deactivate any existing active event so ours becomes the one resolved by GetActiveAsync.
        // EventSettings is Shifts-owned — the service's direct-write path.
        var existingActive = await _dbContext.EventSettings
            .Where(e => e.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var e in existingActive)
        {
            e.IsActive = false;
            e.UpdatedAt = now;
        }

        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = SeededEventName,
            Year = todayUtc.Year,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = todayUtc.PlusDays(60),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = now.Minus(Duration.FromDays(30)),
            UpdatedAt = now,
        };
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync(cancellationToken);

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
                Policy = SignupPolicy.RequireApproval,
                Period = period,
                IsVisibleToVolunteers = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _dbContext.Rotas.Add(rota);
            allRotas.Add((rota, confirmedRate));
        }

        // Shifts: 8–12 per rota with varied min/max/duration and day offsets by period.
        var shifts = new List<(Shift Shift, double ConfirmedRate)>();
        foreach (var (rota, rate) in allRotas)
        {
            var count = _rng.Next(8, 13);
            for (var i = 0; i < count; i++)
            {
                var dayOffset = rota.Period switch
                {
                    RotaPeriod.Build => _rng.Next(-14, 0),
                    RotaPeriod.Event => _rng.Next(0, 7),
                    RotaPeriod.Strike => _rng.Next(7, 10),
                    _ => 0,
                };
                var min = _rng.Next(2, 6);
                var max = min + _rng.Next(1, 4);
                var isAllDay = rota.Period != RotaPeriod.Event;
                var shift = new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = rota.Id,
                    DayOffset = dayOffset,
                    StartTime = isAllDay ? new LocalTime(0, 0) : new LocalTime(_rng.Next(8, 20), 0),
                    Duration = isAllDay
                        ? Duration.FromHours(24)
                        : Duration.FromHours(_rng.Next(2, 9)),
                    MinVolunteers = min,
                    MaxVolunteers = max,
                    IsAllDay = isAllDay,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _dbContext.Shifts.Add(shift);
                shifts.Add((shift, rate));
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Users: keep the cohort small (~120) — large enough to show activity, small enough to keep the dev seed fast.
        // Users are an Identity-framework concern; we create them via UserManager, the
        // standard pattern for framework-owned types. Passwords are intentionally omitted
        // — these accounts are never logged into; they exist purely as dashboard data.
        var totalUsers = 120;

        var users = new List<User>();
        for (var i = 0; i < totalUsers; i++)
        {
            var display = $"Dev Human {i:D3}";
            var email = $"{DevUserEmailPrefix}{i:D3}{DevUserEmailSuffix}";
            var createdAt = now.Minus(Duration.FromDays(_rng.Next(30, 400)));
            var lastLoginDaysAgo = _rng.Next(0, 85);
            var user = new User
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
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
                _dbContext.ShiftSignups.Add(new ShiftSignup
                {
                    Id = Guid.NewGuid(),
                    ShiftId = shift.Id,
                    UserId = user.Id,
                    Status = SignupStatus.Confirmed,
                    CreatedAt = now.Minus(Duration.FromDays(_rng.Next(0, 85))),
                    UpdatedAt = now,
                });
                signupsCreated++;
            }

            // A couple of pending per shift to create visible pending load.
            for (var i = 0; i < 2; i++)
            {
                var user = users[_rng.Next(users.Count)];
                var key = (shift.Id, user.Id);
                if (!pickedSignups.Add(key)) continue;
                // Some of these are stale (>3 days old).
                var createdDaysAgo = _rng.Next(0, 8);
                _dbContext.ShiftSignups.Add(new ShiftSignup
                {
                    Id = Guid.NewGuid(),
                    ShiftId = shift.Id,
                    UserId = user.Id,
                    Status = SignupStatus.Pending,
                    CreatedAt = now.Minus(Duration.FromDays(createdDaysAgo)),
                    UpdatedAt = now,
                });
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
            _dbContext.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                ShiftId = shift.Id,
                UserId = user.Id,
                Status = i % 2 == 0 ? SignupStatus.Bailed : SignupStatus.Refused,
                CreatedAt = now.Minus(Duration.FromDays(_rng.Next(1, 10))),
                UpdatedAt = now,
                ReviewedAt = now,
                ReviewedByUserId = users[0].Id,
                StatusReason = "Seeded for demo",
            });
            signupsCreated++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

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
    /// event (with its rotas/shifts/signups), dev teams (name suffix "<c> (dev)</c>"),
    /// and dev users (email pattern <c>dev-human-*@seed.local</c>). Safe to call on
    /// a DB where no seed has ever run.
    ///
    /// Users go through <see cref="UserManager{TUser}"/>; Teams go through
    /// <see cref="ITeamService.HardDeleteSeededTeamsAsync"/>. Shifts-owned tables
    /// are emptied directly — this seeder IS the owning test fixture.
    /// </summary>
    public async Task<DashboardResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        // Order matters: clear Shifts-owned dependents that reference Users/Teams/Events
        // first so downstream deletions don't trip FK constraints.
        var seededEventIds = await _dbContext.EventSettings
            .Where(e => e.EventName == SeededEventName)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        // Dev users — looked up via UserManager's query surface (framework-owned).
        var devUserIds = await _userManager.Users
            .Where(u => u.Email != null
                        && u.Email.EndsWith(DevUserEmailSuffix)
                        && u.Email.StartsWith(DevUserEmailPrefix))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        // Rotas created by the seeder live under the seeded event IDs; remove their
        // shifts + signups first. All three tables are Shifts-owned, so direct
        // ExecuteDeleteAsync is fine here.
        if (seededEventIds.Count > 0)
        {
            var rotaIds = await _dbContext.Rotas
                .Where(r => seededEventIds.Contains(r.EventSettingsId))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);

            if (rotaIds.Count > 0)
            {
                var shiftIds = await _dbContext.Shifts
                    .Where(s => rotaIds.Contains(s.RotaId))
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken);

                if (shiftIds.Count > 0)
                {
                    await _dbContext.ShiftSignups
                        .Where(s => shiftIds.Contains(s.ShiftId))
                        .ExecuteDeleteAsync(cancellationToken);

                    await _dbContext.Shifts
                        .Where(s => shiftIds.Contains(s.Id))
                        .ExecuteDeleteAsync(cancellationToken);
                }

                await _dbContext.Rotas
                    .Where(r => rotaIds.Contains(r.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        // Also clear any remaining signups that reference dev users — they might belong to
        // non-seeded rotas (e.g., after a partially replayed seed). Shifts-owned, direct OK.
        if (devUserIds.Count > 0)
        {
            await _dbContext.ShiftSignups
                .Where(s => devUserIds.Contains(s.UserId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Teams (and their TeamMembers + TeamJoinRequests): routed via ITeamService.
        var teamsDeleted = await _teamService.HardDeleteSeededTeamsAsync(DevTeamNameSuffix, cancellationToken);

        // Users: routed via UserManager.
        var usersDeleted = 0;
        if (devUserIds.Count > 0)
        {
            // Load each user and delete — UserManager takes entity references, not IDs.
            // At ~120 users this is fine; it's dev-only teardown.
            foreach (var userId in devUserIds)
            {
                var user = await _userManager.FindByIdAsync(userId.ToString());
                if (user is null) continue;
                var deleteResult = await _userManager.DeleteAsync(user);
                if (!deleteResult.Succeeded)
                {
                    _logger.LogWarning(
                        "Failed to delete seeded dev user {UserId} during reset: {Errors}",
                        userId, string.Join(", ", deleteResult.Errors.Select(e => e.Description)));
                    continue;
                }
                usersDeleted++;
            }
        }

        // Events last: Shifts-owned, direct OK.
        var eventsDeleted = 0;
        if (seededEventIds.Count > 0)
        {
            eventsDeleted = await _dbContext.EventSettings
                .Where(e => seededEventIds.Contains(e.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Dashboard seed reset complete: {Events} events, {Teams} teams, {Users} users deleted.",
            eventsDeleted, teamsDeleted, usersDeleted);

        return new DashboardResetResult(eventsDeleted, teamsDeleted, usersDeleted);
    }
}
