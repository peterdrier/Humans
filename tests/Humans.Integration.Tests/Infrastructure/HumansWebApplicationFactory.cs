using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using Humans.Application.Interfaces.Email;

namespace Humans.Integration.Tests.Infrastructure;

public class HumansWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public IReadOnlyList<ServiceDescriptor> RegisteredServices { get; private set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Override connection string and provide required config keys.
            // Program.cs reads ConnectionStrings:DefaultConnection to build the
            // NpgsqlDataSource and DbContext, so overriding here is sufficient.
            config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["DevAuth:Enabled"] = "true",
                ["Authentication:Google:ClientId"] = "test-client-id",
                ["Authentication:Google:ClientSecret"] = "test-client-secret",
                ["Email:SmtpHost"] = "localhost",
                ["Email:FromAddress"] = "test@example.com",
                ["Email:BaseUrl"] = "https://localhost",
                ["GitHub:Owner"] = "",
                ["GitHub:Repository"] = "",
                ["GitHub:AccessToken"] = "",
                ["GoogleMaps:ApiKey"] = "test-api-key",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace email service with a no-op stub
            // so integration tests don't depend on Hangfire's job-storage
            // globals, which are intentionally disabled in Testing.
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailService));
            if (emailDescriptor != null)
                services.Remove(emailDescriptor);
            services.AddScoped(_ => Substitute.For<IEmailService>());

            // TestServer serves over http://localhost; production config sets
            // CookieSecurePolicy.Always which would strip the auth cookie on
            // insecure requests. Relax that for integration tests so the dev
            // login flow actually establishes a session.
            services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            RegisteredServices = services.ToList();

        });
    }

    // xUnit v3 IAsyncLifetime: InitializeAsync returns ValueTask.
    // DisposeAsync is inherited from IAsyncDisposable — the base
    // WebApplicationFactory<TEntryPoint> already provides a virtual
    // ValueTask DisposeAsync(), so override that to tear down the
    // Testcontainers Postgres container.
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Signs the given <see cref="HttpClient"/> in as a fully-onboarded persona.
    ///
    /// "Fully onboarded" here matches what <see cref="Program"/>'s onboarding
    /// pipeline treats as complete for route-level access:
    /// <list type="bullet">
    ///   <item>User + Profile exist (seeded by <c>/dev/login/{persona}</c>).</item>
    ///   <item>Profile is approved and the consent-check is Cleared (seeded by the
    ///     dev-login controller — the bare persona is already
    ///     <c>IsApproved = true</c>, <c>ConsentCheckStatus = Cleared</c>).</item>
    ///   <item>A <see cref="ConsentRecord"/> exists for every published
    ///     <see cref="DocumentVersion"/> whose <see cref="LegalDocument"/> is
    ///     active and required. Integration-test DBs are fresh and have no
    ///     legal documents by default, so this is usually a no-op; tests that
    ///     pre-seed legal docs are covered.</item>
    /// </list>
    /// </summary>
    /// <returns>The persona's user id.</returns>
    public Task<Guid> SignInAsFullyOnboardedAsync(HttpClient client, DevPersona persona) =>
        SignInAsFullyOnboardedAsync(client, persona.Slug);

    /// <inheritdoc cref="SignInAsFullyOnboardedAsync(HttpClient, DevPersona)"/>
    public async Task<Guid> SignInAsFullyOnboardedAsync(HttpClient client, string personaSlug)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(personaSlug);

        // Normalize slug to lowercase: DevLoginController matches personas
        // case-insensitively, but we query the seeded user below by the
        // case-sensitive Postgres varchar email column. Mirror the email
        // convention used by DevLoginController ($"dev-{slug}@localhost", all
        // lowercase) so "Volunteer"/"ADMIN"/etc. still resolve.
        var slug = personaSlug.ToLowerInvariant();

        // 1) Hit the dev-login endpoint. This seeds the User + Profile +
        //    RoleAssignments + TeamMembers for the persona (idempotent) and
        //    issues the Identity auth cookie on the 302 response. Cookies are
        //    captured by WebApplicationFactory's default CookieContainer even
        //    with AllowAutoRedirect=false.
        var loginResp = await client.GetAsync($"/dev/login/{slug}");
        if (loginResp.StatusCode is not (HttpStatusCode.Redirect
            or HttpStatusCode.Found
            or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"Dev login for persona '{slug}' failed: {(int)loginResp.StatusCode} {loginResp.StatusCode}");
        }

        // 2) Resolve the seeded user id by email convention used in DevLoginController.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();

        var email = $"dev-{slug}@localhost";
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email)
            ?? throw new InvalidOperationException(
                $"Persona '{slug}' was not found after dev login (email {email}).");

        // 3) Seed ConsentRecord for every current required document version the
        //    user hasn't already consented to. ConsentRecord is append-only
        //    (DB triggers block UPDATE/DELETE) — INSERT is the only mutation
        //    allowed here.
        await SeedMissingConsentsAsync(db, user.Id);

        return user.Id;
    }

    private static async Task SeedMissingConsentsAsync(HumansDbContext db, Guid userId)
    {
        var now = SystemClock.Instance.GetCurrentInstant();

        // Match LegalDocumentSyncService.GetRequiredVersionsAsync: one row per
        // required+active document, picking the latest already-effective version
        // (EffectiveFrom <= now). Seeding *every* version would give the user
        // consent to future-effective docs and hide re-consent regressions.
        var documentGroups = await db.DocumentVersions
            .AsNoTracking()
            .Where(v => v.LegalDocument.IsRequired
                     && v.LegalDocument.IsActive
                     && v.EffectiveFrom <= now)
            .Select(v => new { v.Id, v.LegalDocumentId, v.EffectiveFrom, Content = v.Content })
            .ToListAsync();

        if (documentGroups.Count == 0)
            return;

        var currentVersions = documentGroups
            .GroupBy(v => v.LegalDocumentId)
            .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First())
            .ToList();

        var alreadyConsentedIds = await db.ConsentRecords
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.DocumentVersionId)
            .ToListAsync();
        var alreadyConsented = alreadyConsentedIds.ToHashSet();

        foreach (var version in currentVersions)
        {
            if (alreadyConsented.Contains(version.Id))
                continue;

            // Mirror ConsentService.SubmitConsentAsync: hash the canonical ("es")
            // content so ContentHash matches what production would produce.
            var canonical = version.Content.GetValueOrDefault("es", string.Empty);
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();

            db.ConsentRecords.Add(new ConsentRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DocumentVersionId = version.Id,
                ConsentedAt = now,
                IpAddress = "127.0.0.1",
                UserAgent = "integration-test-fixture",
                ContentHash = contentHash,
                ExplicitConsent = true
            });
        }

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Named dev-login personas used by integration-test fixtures. The slug
/// matches the route segment consumed by <c>DevLoginController.SignIn</c>.
/// </summary>
public sealed record DevPersona(string Slug)
{
    /// <summary>Bare volunteer: approved, consent-check cleared, Volunteers team.</summary>
    public static readonly DevPersona Volunteer = new("volunteer");

    /// <summary>Admin persona (RoleNames.Admin). Volunteers team + Admin role.</summary>
    public static readonly DevPersona Admin = new("admin");

    /// <summary>Board member persona (RoleNames.Board). Volunteers + Board teams.</summary>
    public static readonly DevPersona Board = new("board");

    /// <summary>Coordinator persona with a seeded test department and sub-team.</summary>
    public static readonly DevPersona Coordinator = new("coordinator");
}
