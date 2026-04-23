using AwesomeAssertions;
using Humans.Application.DTOs.Calendar;
using Humans.Application.Interfaces.Calendar;
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
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, "Test Team A");
        var userId = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        var start = Instant.FromUtc(2026, 5, 1, 17, 0);
        var end = Instant.FromUtc(2026, 5, 1, 18, 0);

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
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
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
            to: Instant.FromUtc(2026, 7, 1, 0, 0),
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
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

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

    [Fact]
    public async Task Soft_deleted_events_do_not_appear_in_window()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

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

    [Fact]
    public async Task Recurring_weekly_event_stays_at_local_time_across_Madrid_DST()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        // Tuesday 24 March 2026 19:00 Madrid (CET, UTC+1) = 18:00 UTC.
        // After Madrid DST flip on 29 Mar 2026, 19:00 Madrid (CEST, UTC+2) = 17:00 UTC.
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var firstLocal = new LocalDateTime(2026, 3, 24, 19, 0);
        var firstUtc = firstLocal.InZoneLeniently(zone).ToInstant();

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Tuesday call", null, null, null, team.Id,
            StartUtc: firstUtc,
            EndUtc: firstUtc.Plus(Duration.FromHours(1)),
            IsAllDay: false,
            RecurrenceRule: "FREQ=WEEKLY;BYDAY=TU;COUNT=4",
            RecurrenceTimezone: "Europe/Madrid"), uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            from: Instant.FromUtc(2026, 3, 1, 0, 0),
            to: Instant.FromUtc(2026, 5, 1, 0, 0),
            teamId: team.Id);

        occ.Should().HaveCount(4);
        foreach (var o in occ)
        {
            var localStart = o.OccurrenceStartUtc.InZone(zone).LocalDateTime;
            localStart.Hour.Should().Be(19);
            localStart.Minute.Should().Be(0);
        }
    }

    [Fact]
    public async Task Recurring_bounded_event_is_skipped_when_until_before_window()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Old", null, null, null, team.Id,
            StartUtc: Instant.FromUtc(2024, 1, 7, 18, 0),
            EndUtc: Instant.FromUtc(2024, 1, 7, 19, 0),
            IsAllDay: false,
            RecurrenceRule: "FREQ=WEEKLY;UNTIL=20240201T000000Z",
            RecurrenceTimezone: "Europe/Madrid"), uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 1, 1, 0, 0),
            Instant.FromUtc(2026, 2, 1, 0, 0),
            teamId: team.Id);

        occ.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_exception_removes_that_occurrence()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

        var first = new LocalDateTime(2026, 5, 5, 19, 0).InZoneLeniently(zone).ToInstant();

        var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Weekly", null, null, null, team.Id,
            first, first.Plus(Duration.FromHours(1)),
            false, "FREQ=WEEKLY;BYDAY=TU;COUNT=4", "Europe/Madrid"), uid);

        // Cancel the 3rd occurrence (2026-05-19 19:00 Madrid).
        var cancel = new LocalDateTime(2026, 5, 19, 19, 0).InZoneLeniently(zone).ToInstant();
        await svc.CancelOccurrenceAsync(ev.Id, cancel, uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 5, 1, 0, 0),
            Instant.FromUtc(2026, 6, 1, 0, 0),
            teamId: team.Id);

        occ.Should().HaveCount(3);
        occ.Select(o => o.OccurrenceStartUtc).Should().NotContain(cancel);
    }

    [Fact]
    public async Task Override_changes_title_and_moves_occurrence()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

        var first = new LocalDateTime(2026, 5, 5, 19, 0).InZoneLeniently(zone).ToInstant();

        var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Weekly", null, null, null, team.Id,
            first, first.Plus(Duration.FromHours(1)),
            false, "FREQ=WEEKLY;BYDAY=TU;COUNT=4", "Europe/Madrid"), uid);

        // Move the 2nd occurrence from 19:00 to 20:00.
        var original = new LocalDateTime(2026, 5, 12, 19, 0).InZoneLeniently(zone).ToInstant();
        var moved = new LocalDateTime(2026, 5, 12, 20, 0).InZoneLeniently(zone).ToInstant();

        await svc.OverrideOccurrenceAsync(ev.Id, original, new OverrideOccurrenceDto(
            OverrideStartUtc: moved,
            OverrideEndUtc: moved.Plus(Duration.FromHours(1)),
            OverrideTitle: "Special week",
            OverrideDescription: null,
            OverrideLocation: null,
            OverrideLocationUrl: null), uid);

        var occ = await svc.GetOccurrencesInWindowAsync(
            Instant.FromUtc(2026, 5, 1, 0, 0),
            Instant.FromUtc(2026, 6, 1, 0, 0),
            teamId: team.Id);

        occ.Should().HaveCount(4);
        var special = occ.Single(o => string.Equals(o.Title, "Special week", StringComparison.Ordinal));
        special.OccurrenceStartUtc.Should().Be(moved);
        special.OriginalOccurrenceStartUtc.Should().Be(original);
    }

    [Fact]
    public async Task UpdateEvent_changes_fields_and_preserves_id()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
            "Original", null, null, null, team.Id,
            Instant.FromUtc(2026, 7, 1, 17, 0),
            Instant.FromUtc(2026, 7, 1, 18, 0), false, null, null), uid);

        await svc.UpdateEventAsync(ev.Id, new UpdateCalendarEventDto(
            "Updated", "new desc", "Hall", null, team.Id,
            Instant.FromUtc(2026, 7, 2, 17, 0),
            Instant.FromUtc(2026, 7, 2, 18, 0), false, null, null), uid);

        var fetched = await svc.GetEventByIdAsync(ev.Id);
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Updated");
        fetched.Description.Should().Be("new desc");
        fetched.StartUtc.Should().Be(Instant.FromUtc(2026, 7, 2, 17, 0));
    }

    [Fact]
    public async Task DeleteEvent_soft_deletes_and_hides_from_queries()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var team = await SeedTeamAsync(db, $"T-{Guid.NewGuid():N}");
        var uid = await SeedUserAsync(scope, $"calsvc-{Guid.NewGuid():N}@test.local");

        var ev = await svc.CreateEventAsync(new CreateCalendarEventDto(
            "ToDelete", null, null, null, team.Id,
            Instant.FromUtc(2026, 8, 1, 10, 0),
            Instant.FromUtc(2026, 8, 1, 11, 0), false, null, null), uid);

        await svc.DeleteEventAsync(ev.Id, uid);

        (await svc.GetEventByIdAsync(ev.Id)).Should().BeNull();
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
