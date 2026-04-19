using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Web.Infrastructure;

public sealed record DashboardSeedResult(
    bool AlreadySeeded,
    int TeamsCreated,
    int UsersCreated,
    int ShiftsCreated,
    int SignupsCreated,
    int TicketOrdersCreated);

/// <summary>
/// Seeds deterministic-ish demo data for the volunteer coordinator dashboard.
/// Gated to IsDevelopment() only by the calling controller. Not safe to run in QA/prod.
/// </summary>
public sealed class DevelopmentDashboardSeeder
{
    private const string SeededEventName = "Seeded Nowhere 2026 (dev)";

    private static readonly string[] ParentTeamNames =
        ["Gate", "Infrastructure", "Kitchen", "Medics", "Rangers", "DPW"];

    private static readonly string[] InfrastructureSubteamNames = ["Power", "Plumbing"];

    // A deterministic RNG so reruns on a clean DB produce the same-ish shape.
    private readonly Random _rng = new(42);

    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentDashboardSeeder> _logger;

    public DevelopmentDashboardSeeder(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<DevelopmentDashboardSeeder> logger)
    {
        _dbContext = dbContext;
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
            return new DashboardSeedResult(AlreadySeeded: true, 0, 0, 0, 0, 0);
        }

        var now = _clock.GetCurrentInstant();
        var todayUtc = now.InUtc().Date;

        // Deactivate any existing active event so ours becomes the one resolved by GetActiveAsync.
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

        var teamsCreated = 0;
        var parentTeams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var name in ParentTeamNames)
        {
            var t = new Team
            {
                Id = Guid.NewGuid(),
                Name = $"{name} (dev)",
                Slug = $"dev-{name.ToLowerInvariant()}",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            parentTeams[name] = t;
            _dbContext.Teams.Add(t);
            teamsCreated++;
        }

        var subteams = new Dictionary<string, Team>(StringComparer.Ordinal);
        foreach (var name in InfrastructureSubteamNames)
        {
            var t = new Team
            {
                Id = Guid.NewGuid(),
                Name = $"{name} (dev)",
                Slug = $"dev-{name.ToLowerInvariant()}",
                IsActive = true,
                ParentTeamId = parentTeams["Infrastructure"].Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            subteams[name] = t;
            _dbContext.Teams.Add(t);
            teamsCreated++;
        }

        // Rotas: one per period per parent team, plus Event-period rotas on each Infrastructure subteam.
        var rotaConfigs = new List<(Team Team, RotaPeriod Period, string Label, double ConfirmedRate)>();
        foreach (var parent in parentTeams.Values)
        {
            var isWellStaffed = parent.Name.StartsWith("Gate", StringComparison.Ordinal)
                             || parent.Name.StartsWith("Kitchen", StringComparison.Ordinal);
            rotaConfigs.Add((parent, RotaPeriod.Build, "Build", isWellStaffed ? 0.85 : 0.55));
            rotaConfigs.Add((parent, RotaPeriod.Event, "Event", isWellStaffed ? 0.9 : 0.6));
            rotaConfigs.Add((parent, RotaPeriod.Strike, "Strike", 0.2));
        }
        rotaConfigs.Add((subteams["Power"], RotaPeriod.Event, "Event", 0.85));
        rotaConfigs.Add((subteams["Plumbing"], RotaPeriod.Event, "Event", 0.4));

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

        // Users: keep the cohort small (~120) — large enough to show activity, small enough to keep the dev seed fast.
        var totalUsers = 120;
        var ticketHolderCount = 90;

        var users = new List<User>();
        var ticketOrdersCreated = 0;
        for (var i = 0; i < totalUsers; i++)
        {
            var display = $"Dev Human {i:D3}";
            var email = $"dev-human-{i:D3}@seed.local";
            var createdAt = now.Minus(Duration.FromDays(_rng.Next(30, 400)));
            var lastLoginDaysAgo = _rng.Next(0, 30);
            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                DisplayName = display,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = createdAt,
                LastLoginAt = now.Minus(Duration.FromDays(lastLoginDaysAgo)).Minus(Duration.FromHours(_rng.Next(0, 23))),
            };
            users.Add(user);
            _dbContext.Users.Add(user);

            if (i < ticketHolderCount)
            {
                _dbContext.TicketOrders.Add(new TicketOrder
                {
                    Id = Guid.NewGuid(),
                    VendorOrderId = $"DEV-{i:D4}",
                    BuyerName = display,
                    BuyerEmail = email,
                    MatchedUserId = user.Id,
                    TotalAmount = 80m,
                    Currency = "EUR",
                    PaymentStatus = TicketPaymentStatus.Paid,
                    VendorEventId = "dev-vendor-event",
                    PurchasedAt = now.Minus(Duration.FromDays(_rng.Next(0, 45))),
                    SyncedAt = now,
                });
                ticketOrdersCreated++;
            }
        }

        // Coordinators: 2 per parent team. Infrastructure coords logged in 9 days ago (stale).
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
                _dbContext.TeamMembers.Add(new TeamMember
                {
                    Id = Guid.NewGuid(),
                    TeamId = parent.Id,
                    UserId = coord.Id,
                    Role = TeamMemberRole.Coordinator,
                    JoinedAt = now.Minus(Duration.FromDays(60)),
                });
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
                    CreatedAt = now.Minus(Duration.FromDays(_rng.Next(0, 20))),
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
            "Dashboard seed complete: {Teams} teams, {Users} users, {Shifts} shifts, {Signups} signups, {Tickets} ticket orders.",
            teamsCreated, users.Count, shifts.Count, signupsCreated, ticketOrdersCreated);

        return new DashboardSeedResult(
            AlreadySeeded: false,
            TeamsCreated: teamsCreated,
            UsersCreated: users.Count,
            ShiftsCreated: shifts.Count,
            SignupsCreated: signupsCreated,
            TicketOrdersCreated: ticketOrdersCreated);
    }
}
