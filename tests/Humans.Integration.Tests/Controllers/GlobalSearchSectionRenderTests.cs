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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The four non-human buckets of the global <c>/Search</c> page now render their owning
/// section's own view component — <c>&lt;vc:teams-search-result&gt;</c>,
/// <c>&lt;vc:camps-search-result&gt;</c>, <c>&lt;vc:shifts-search-result&gt;</c>,
/// <c>&lt;vc:events-search-result&gt;</c> — invoked with the entity's Guid and nothing else
/// (nobodies-collective/Humans#1062).
/// </summary>
/// <remarks>
/// <para>
/// Each element binds only through one <c>@addTagHelper</c> line in Search's
/// <c>Views/_ViewImports.cshtml</c>. A missing line is silent: the row ships as literal
/// <c>&lt;vc:…&gt;</c> markup on a green build and a 200. So this seeds one real row per
/// bucket behind a single unique token and asserts on markers only the component writes —
/// the name it fetched itself, and the link it built. That a tag <i>can</i> bind at all is
/// checked statically and page-wide by <c>ViewComponentTagHelperBindingTests</c>
/// (nobodies-collective/Humans#1434); what is left for a rendered page is whether the row
/// the component produced is the right one.
/// </para>
/// <para>
/// Negative probe (2026-08-20): deleting any one of the four <c>@addTagHelper</c> lines
/// turns this test red on that bucket's marker, and the static check red on the call site.
/// </para>
/// <para>
/// The second test measures the other half of the acceptance bar: one component
/// instance per row must not mean one query per row.
/// </para>
/// <para>
/// The third covers the other call site — <c>/WidgetGallery</c>, which catalogs all four
/// and needs its own <c>@addTagHelper</c> lines in <c>Humans.Debug</c>.
/// </para>
/// </remarks>
public class GlobalSearchSectionRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 180000)]
    public async Task Every_bucket_renders_through_its_own_sections_view_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var token = $"Zqx{Guid.NewGuid():N}"[..12];
        var seeded = await SeedOneRowPerBucketAsync(token, 0, Factory.Services, ct);

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var response = await Client.GetAsync($"/Search?q={token}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain($"{token} Team", because: "Teams' component fetched the name off the id");
        html.Should().Contain($"href=\"/Teams/{seeded.TeamSlug}\"",
            because: "Teams' component fetched the slug too — the orchestrator only passed an id");

        html.Should().Contain($"{token} Camp", because: "Camps' component resolved the public-year season name");
        html.Should().Contain($"href=\"/Camps/{seeded.CampSlug}\"", because: "Camps' component built the link");

        html.Should().Contain($"{token} Rota", because: "Shifts' component fetched the rota by id");
        html.Should().Contain($"departmentId={seeded.TeamId}", because: "Shifts' component built the department link");

        html.Should().Contain($"{token} Event", because: "Events' component fetched the event by id");
        html.Should().Contain("/Events/Browse?q=", because: "Events' component built the Browse link");
    }

    /// <summary>
    /// The widget gallery catalogs all four rows, so it needs its own <c>@addTagHelper</c>
    /// lines — Teams and Events had none before these components existed.
    /// </summary>
    /// <remarks>
    /// The gallery keys each card off the first real row it can find, not off a fixture, so
    /// this cannot assert on a token for the two the environment already has rows of. Instead
    /// it pins both halves: the fallback line is absent (a key resolved, so the component was
    /// invoked at all) and no literal tag survives (it bound). The rota and the event are the
    /// only ones a fresh database holds, so those two carry the token assertion outright.
    /// </remarks>
    [HumansFact(Timeout = 180000)]
    public async Task The_widget_gallery_binds_all_four_rows()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var token = $"Zqx{Guid.NewGuid():N}"[..12];
        await SeedOneRowPerBucketAsync(token, 0, Factory.Services, ct);

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var response = await Client.GetAsync("/WidgetGallery", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        foreach (var fallback in new[] { "No team in", "No camp in", "No rota in", "No approved event" })
        {
            html.Should().NotContain(fallback,
                because: $"the seeding gives every card a key, so \"{fallback}…\" means the component never ran");
        }

        html.Should().Contain($"{token} Rota", because: "the seeded rota is the only one, so Shifts' card must show it");
        html.Should().Contain($"{token} Event", because: "the seeded event is the only one, so Events' card must show it");
    }

    /// <summary>
    /// One component instance per result row is only cheap if every one of them reads a
    /// cache the section already holds, so measure it rather than assert it in a comment:
    /// tripling the rows in each bucket must not move the query count.
    /// </summary>
    [HumansFact(Timeout = 180000)]
    public async Task Tripling_the_rows_costs_no_extra_query()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var token = $"Zqx{Guid.NewGuid():N}"[..12];
        await SeedOneRowPerBucketAsync(token, 0, Factory.Services, ct);

        var counter = new DbCommandCounter();
        // Replace the factory outright rather than adding a provider: Program.cs calls
        // UseSerilog, which swaps ILoggerFactory wholesale and drops added providers.
        // Scoped to this host, so a parallel test class cannot bleed into the count.
        await using var counted = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<ILoggerFactory>(new LoggerFactory([counter]))));
        var client = counted.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // Dev login straight against this host: the factory helper resolves the seeded id
        // through the *other* host's caches, which never saw it. A fresh clone has no
        // required legal documents, so the consent seeding it also does is a no-op here.
        await client.GetAsync($"/dev/login/{DevPersona.Admin.Slug}", ct);

        // A counter that never moves would make the comparison below pass vacuously.
        await client.GetAsync($"/Search?q={token}", ct);
        counter.Count.Should().BeGreaterThan(0, because: "the cold pass has to reach the database");

        var oneRowPerBucket = await MeasureSearchAsync(counter, client, token, ct);

        await SeedOneRowPerBucketAsync(token, 1, counted.Services, ct);
        await SeedOneRowPerBucketAsync(token, 2, counted.Services, ct);
        var threeRowsPerBucket = await MeasureSearchAsync(counter, client, token, ct);

        // Measured 2026-08-20, before and after the Guid re-key: two commands for the whole
        // page either way — per-request auth reads, nothing from the result rows.
        threeRowsPerBucket.Should().Be(oneRowPerBucket,
            because: "every row is served from its section's cache, so row count must not reach the database");
    }

    private static async Task<int> MeasureSearchAsync(
        DbCommandCounter counter, HttpClient client, string token, CancellationToken ct)
    {
        // Warm first — the per-rota view cache fills one row at a time, so the cold
        // pass is not the steady state. Then measure the request after it.
        await client.GetAsync($"/Search?q={token}", ct);
        counter.Reset();
        var response = await client.GetAsync($"/Search?q={token}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return counter.Count;
    }

    /// <summary>Counts executed commands across every section's context, off EF's own log.</summary>
    private sealed class DbCommandCounter : ILoggerProvider, ILogger
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public ILogger CreateLogger(string categoryName) =>
            string.Equals(categoryName, DbLoggerCategory.Database.Command.Name, StringComparison.Ordinal)
                ? this
                : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == RelationalEventId.CommandExecuted.Id)
                Interlocked.Increment(ref _count);
        }

        public void Dispose() { }
    }

    private sealed record SeededRows(string TeamSlug, Guid TeamId, string CampSlug);

    /// <summary>Seeds one matchable row per search bucket and returns the refs the assertions need.</summary>
    /// <param name="index">Distinguishes repeat seedings under one token; row 0 keeps the bare names the bind test asserts on.</param>
    private static async Task<SeededRows> SeedOneRowPerBucketAsync(
        string token, int index, IServiceProvider rootServices, CancellationToken ct)
    {
        var suffix = index == 0 ? string.Empty : $" {index}";
        var slugSuffix = index == 0 ? string.Empty : $"-{index}";
        await using var scope = rootServices.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var now = SystemClock.Instance.GetCurrentInstant();

        var teamsDb = sp.GetRequiredService<TeamsDbContext>();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"{token} Team{suffix}",
            Slug = $"{token.ToLowerInvariant()}{slugSuffix}",
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
            Slug = $"{token.ToLowerInvariant()}-camp{slugSuffix}",
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
            Name = $"{token} Camp{suffix}",
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
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettings.Id,
            TeamId = team.Id,
            Name = $"{token} Rota{suffix}",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            IsVisibleToVolunteers = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shiftsDb.Rotas.Add(rota);
        // One slot under it: a rota with no shifts is invisible to every rota-listing read
        // the app has, so a shiftless fixture would make the gallery card untestable.
        shiftsDb.Shifts.Add(new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 0,
            StartTime = new LocalTime(10, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 4,
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
            Name = $"{token} Category{suffix}",
            Slug = $"{token.ToLowerInvariant()}-category{slugSuffix}",
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
            Title = $"{token} Event{suffix}",
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
