using System.Net;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Camps.Data;
using Humans.Camps.Domain;
using Humans.Camps.Services;
using Humans.Events.Contracts;
using Humans.Events.Data;
using Humans.Events.Domain;
using Humans.Events.Services;
using Humans.Integration.Tests.Infrastructure;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The four non-human buckets of the global <c>/Search</c> page now render their owning
/// section's own view component — <c>&lt;vc:teams-search-result&gt;</c>,
/// <c>&lt;vc:camps-search-result&gt;</c>, <c>&lt;vc:shifts-search-result&gt;</c>,
/// <c>&lt;vc:events-search-result&gt;</c> — invoked with a key and nothing else
/// (nobodies-collective/Humans#1062).
/// </summary>
/// <remarks>
/// <para>
/// Each element binds only through one <c>@addTagHelper</c> line in Search's
/// <c>Views/_ViewImports.cshtml</c>. A missing line is silent: the row ships as literal
/// <c>&lt;vc:…&gt;</c> markup on a green build and a 200. So this seeds one real row per
/// bucket behind a single unique token and asserts on markers only the component writes —
/// the name it fetched itself, and the link it built. A bare
/// <c>NotContain("&lt;vc:")</c> would pass vacuously on an empty page, so it is the last
/// assertion here, not the only one.
/// </para>
/// <para>
/// Negative probe (run by hand, 2026-08-20): deleting any one of the four
/// <c>@addTagHelper</c> lines turns this test red on that bucket's marker.
/// </para>
/// </remarks>
public class GlobalSearchSectionRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Every_bucket_renders_through_its_own_sections_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var token = $"Zqx{Guid.NewGuid():N}"[..12];
        var seeded = await SeedOneRowPerBucketAsync(token, ct);

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var response = await Client.GetAsync($"/Search?q={token}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain($"{token} Team", because: "Teams' component fetched the name off the slug");
        html.Should().Contain($"href=\"/Teams/{seeded.TeamSlug}\"", because: "Teams' component built the link");

        html.Should().Contain($"{token} Camp", because: "Camps' component resolved the public-year season name");
        html.Should().Contain($"href=\"/Camps/{seeded.CampSlug}\"", because: "Camps' component built the link");

        html.Should().Contain($"{token} Rota", because: "Shifts' component fetched the rota by id");
        html.Should().Contain($"departmentId={seeded.TeamId}", because: "Shifts' component built the department link");

        html.Should().Contain($"{token} Event", because: "Events' component fetched the event by id");
        html.Should().Contain("/Events/Browse?q=", because: "Events' component built the Browse link");

        html.Should().NotContain("<vc:", because: "an unbound element renders as literal markup on a green 200");
        html.Should().NotContain("-view-component", because: "a ReSharper-rewritten vc tag is inert too");
    }

    private sealed record SeededRows(string TeamSlug, Guid TeamId, string CampSlug);

    private async Task<SeededRows> SeedOneRowPerBucketAsync(string token, CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var now = SystemClock.Instance.GetCurrentInstant();

        var teamsDb = sp.GetRequiredService<TeamsDbContext>();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"{token} Team",
            Slug = token.ToLowerInvariant(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        teamsDb.Teams.Add(team);
        await teamsDb.SaveChangesAsync(ct);

        var campsDb = sp.GetRequiredService<CampsDbContext>();
        var publicYear = (await campsDb.CampSettings.AsNoTracking().FirstAsync(ct)).PublicYear;
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = $"{token.ToLowerInvariant()}-camp",
            ContactEmail = "search@example.org",
            ContactPhone = string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        campsDb.Set<Camp>().Add(camp);
        campsDb.Set<CampSeason>().Add(new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = publicYear,
            Name = $"{token} Camp",
            Status = CampSeasonStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await campsDb.SaveChangesAsync(ct);

        var shiftsDb = sp.GetRequiredService<ShiftsDbContext>();
        var eventSettings = await shiftsDb.EventSettings.FirstOrDefaultAsync(e => e.IsActive, ct);
        if (eventSettings is null)
        {
            eventSettings = new EventSettings
            {
                Id = Guid.NewGuid(),
                EventName = $"{token} Burn",
                Year = publicYear,
                TimeZoneId = "Europe/Madrid",
                GateOpeningDate = new LocalDate(publicYear, 7, 1),
                BuildStartOffset = -10,
                EventEndOffset = 6,
                StrikeEndOffset = 8,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            shiftsDb.EventSettings.Add(eventSettings);
        }
        shiftsDb.Rotas.Add(new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettings.Id,
            TeamId = team.Id,
            Name = $"{token} Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            IsVisibleToVolunteers = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await shiftsDb.SaveChangesAsync(ct);

        await using var eventsDb = await sp
            .GetRequiredService<IDbContextFactory<EventGuideDbContext>>()
            .CreateDbContextAsync(ct);
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = $"{token} Category",
            Slug = $"{token.ToLowerInvariant()}-category",
            DisplayOrder = 99,
            IsActive = true,
        };
        eventsDb.Set<EventCategory>().Add(category);
        eventsDb.Set<Event>().Add(new Event
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            SubmitterUserId = Guid.NewGuid(),
            CategoryId = category.Id,
            Title = $"{token} Event",
            Description = "Seeded for the global-search render test.",
            StartAt = now,
            DurationMinutes = 60,
            Status = EventStatus.Approved,
            SubmittedAt = now,
            LastUpdatedAt = now,
        });
        await eventsDb.SaveChangesAsync(ct);

        // Teams, Camps and Events all serve search from a singleton snapshot warmed at
        // startup, so rows written straight to their contexts are invisible until it reloads.
        // Events has no reload hook of its own — a category save is the section's own signal
        // that every approved-event projection is stale, so drive that.
        sp.GetRequiredService<CachingTeamService>().InvalidateActiveTeamsCache();
        await sp.GetRequiredService<CachingCampService>().InvalidateAllAsync(ct);
        await sp.GetRequiredService<IEventService>().UpdateCategoryAsync(category, ct);

        return new SeededRows(team.Slug, team.Id, camp.Slug);
    }
}
