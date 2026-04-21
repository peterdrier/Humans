using AwesomeAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Services;

public class CalendarServiceTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;
    public CalendarServiceTests(HumansWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateEventAsync_persists_and_GetEventById_returns_it()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db  = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team   = await SeedTeamAsync(db, "Test Team A");
        var userId = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        var start = Instant.FromUtc(2026, 5, 1, 17, 0);
        var end   = Instant.FromUtc(2026, 5, 1, 18, 0);

        var created = await svc.CreateEventAsync(
            new CreateCalendarEventDto(
                Title: "Community call",
                Description: "Monthly sync",
                Location: "Zoom",
                LocationUrl: "https://meet.google.com/abc",
                OwningTeamId: team.Id,
                StartUtc: start,
                EndUtc: end,
                IsAllDay: false,
                RecurrenceRule: null,
                RecurrenceTimezone: null),
            createdByUserId: userId);

        created.Id.Should().NotBe(Guid.Empty);

        var fetched = await svc.GetEventByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Community call");
        fetched.OwningTeamId.Should().Be(team.Id);
        fetched.StartUtc.Should().Be(start);
        fetched.EndUtc.Should().Be(end);
    }

    [Fact]
    public async Task GetOccurrencesInWindow_returns_single_event_when_overlapping()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db  = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team   = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var userId = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Inside", null, null, null, team.Id,
            Instant.FromUtc(2026, 6, 15, 17, 0),
            Instant.FromUtc(2026, 6, 15, 18, 0),
            false, null, null), userId);

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Outside", null, null, null, team.Id,
            Instant.FromUtc(2027, 1, 1, 0, 0),
            Instant.FromUtc(2027, 1, 1, 1, 0),
            false, null, null), userId);

        var occ = await svc.GetOccurrencesInWindowAsync(
            from: Instant.FromUtc(2026, 6, 1, 0, 0),
            to:   Instant.FromUtc(2026, 7, 1, 0, 0),
            teamId: team.Id);

        occ.Should().ContainSingle(o => o.Title == "Inside");
        occ.Should().NotContain(o => o.Title == "Outside");
        occ.Single(o => string.Equals(o.Title, "Inside", StringComparison.Ordinal)).IsRecurring.Should().BeFalse();
    }

    [Fact]
    public async Task GetOccurrencesInWindow_filters_by_team()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db  = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var a = await SeedTeamAsync(db, $"A-{Guid.NewGuid():N}");
        var b = await SeedTeamAsync(db, $"B-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "A-evt", null, null, null, a.Id,
            Instant.FromUtc(2026, 6, 15, 17, 0),
            Instant.FromUtc(2026, 6, 15, 18, 0), false, null, null), uid);

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "B-evt", null, null, null, b.Id,
            Instant.FromUtc(2026, 6, 15, 19, 0),
            Instant.FromUtc(2026, 6, 15, 20, 0), false, null, null), uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            Instant.FromUtc(2026, 7, 1, 0, 0),
            teamId: a.Id);

        occ.Should().ContainSingle(o => o.Title == "A-evt");
        occ.Should().NotContain(o => o.Title == "B-evt");
    }

    [Fact(Skip = "Needs DeleteEventAsync (Task 12) — un-skip then.")]
    public async Task Soft_deleted_events_do_not_appear_in_window()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db  = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid  = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
            "DoomedEvent", null, null, null, team.Id,
            Instant.FromUtc(2026, 6, 15, 17, 0),
            Instant.FromUtc(2026, 6, 15, 18, 0), false, null, null), uid);

        await svc.DeleteEventAsync(ev.Id, uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            Instant.FromUtc(2026, 7, 1, 0, 0),
            teamId: team.Id);

        occ.Should().BeEmpty();
    }

    private static async Task<Team> SeedTeamAsync(HumansDbContext db, string name)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = $"test-{Guid.NewGuid():N}".Substring(0, 12),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    private static async Task<Guid> SeedUserAsync(IServiceScope scope, string email)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Human",
            Email = email,
            UserName = email,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
        var result = await um.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("Failed to seed user: " +
                string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }
}
