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
