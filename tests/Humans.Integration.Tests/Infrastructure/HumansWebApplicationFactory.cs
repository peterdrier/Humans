using System.Net;
using Humans.Base.Constants;
using Humans.Consent.Contracts;
using Humans.Consent.Domain;
using Humans.Consent.Data;
using Humans.Consent.Services;
using Humans.Onboarding.Services;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Email.Contracts;
using Humans.MailerLite.Services;
using Humans.Users.Contracts;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Humans.Users.Data;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// An app host bound to one freshly cloned database, built per test: no other test's rows, and no Singleton cache
/// still holding them.
/// </summary>
public class HumansWebApplicationFactory(string connectionString)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestStripeWebhookSecret = "whsec_test_humans_integration_secret_do_not_use_in_prod";

    public IStripeService StripeServiceStub { get; } = Substitute.For<IStripeService>();

    /// <summary>Program.cs skips Hangfire in Testing; assert enqueues against ReceivedCalls().</summary>
    public IBackgroundJobClient BackgroundJobClientStub { get; } = Substitute.For<IBackgroundJobClient>();

    /// <summary>Hand-written rather than substituted because the interface yields an IAsyncEnumerable.</summary>
    internal StubMailerLiteService MailerLiteServiceStub { get; } = new();

    /// <summary>Isolated from parallel factories.</summary>
    internal Humans.Base.Logging.InMemoryLogSink LogSink { get; } = new();

    public IReadOnlyList<ServiceDescriptor> RegisteredServices { get; private set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // WebApplicationBuilder composes static web assets only in Development; without this every /_content/<Rcl>/ URL
        // 404s here and asp-append-version silently drops the ?v= hash.
        builder.UseStaticWebAssets();

        // No file watchers: each is an inotify instance a disposed host never releases, and Linux caps a user at 128,
        // so a few hundred hosts stopped booting. This setting is read before the JSON sources are added;
        // asp-append-version is answered by the non-watching wrapper below.
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.ConfigureAppConfiguration((context, _) =>
            context.HostingEnvironment.WebRootFileProvider =
                new NonWatchingFileProvider(context.HostingEnvironment.WebRootFileProvider));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
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
            services.RemoveAll<Humans.Base.Logging.InMemoryLogSink>();
            services.AddSingleton(LogSink);

            // No-op email: Hangfire's job-storage globals are disabled in Testing.
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailService));
            if (emailDescriptor != null)
                services.Remove(emailDescriptor);
            services.AddScoped(_ => Substitute.For<IEmailService>());

            // TestServer is plain http; CookieSecurePolicy.Always would strip the dev-login auth cookie.
            services.ConfigureApplicationCookie(options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            services.Configure<StripeSettings>(opts =>
            {
                opts.StoreWebhookSecret = TestStripeWebhookSecret;
            });

            // Defaults are re-seeded per test by ResetSharedSubstitutes.
            ConfigureStripeStubDefaults();

            var stripeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IStripeService));
            if (stripeDescriptor != null) services.Remove(stripeDescriptor);
            services.AddScoped(_ => StripeServiceStub);

            var mailerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMailerLiteService));
            if (mailerDescriptor != null) services.Remove(mailerDescriptor);
            services.AddSingleton<IMailerLiteService>(MailerLiteServiceStub);

            // Hangfire's registration is skipped in Testing, so consumers like HangfireImmediateOutboxProcessor need this.
            services.AddSingleton(BackgroundJobClientStub);

            RegisteredServices = services.ToList();

        });
    }

    /// <summary>
    /// Webhook parsing is pure CPU, so it goes to a real <see cref="StripeService"/> with the test secret: signature
    /// verification is real while network calls stay stubbed.
    /// </summary>
    private void ConfigureStripeStubDefaults()
    {
        var realStripeForWebhookParse = new StripeService(
            Options.Create(new StripeSettings { StoreWebhookSecret = TestStripeWebhookSecret }),
            NullLogger<StripeService>.Instance);
        StripeServiceStub.IsStoreWebhookConfigured.Returns(true);
        StripeServiceStub.ParseStoreCheckoutEvent(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => realStripeForWebhookParse.ParseStoreCheckoutEvent(
                (string)call[0], (string)call[1]));
    }

    public ValueTask InitializeAsync()
    {
        // Build now (migrations, cache warm-up) so a slow boot doesn't eat the first test's 30s timeout.
        _ = Services;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Dev login seeds an approved, consent-cleared persona; this adds consent to every required document version.
    /// </summary>
    /// <returns>The persona's user id.</returns>
    public Task<Guid> SignInAsFullyOnboardedAsync(HttpClient client, DevPersona persona) =>
        SignInAsFullyOnboardedAsync(client, persona.Slug);

    /// <inheritdoc cref="SignInAsFullyOnboardedAsync(HttpClient, DevPersona)"/>
    public async Task<Guid> SignInAsFullyOnboardedAsync(HttpClient client, string personaSlug)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(personaSlug);

        // Dev login matches personas case-insensitively, but the email lookup below is case-sensitive.
        var slug = personaSlug.ToLowerInvariant();

        // Seeds the persona (idempotent) and issues the auth cookie on the 302; the cookie is captured even with
        // AllowAutoRedirect=false.
        var loginResp = await client.GetAsync($"/dev/login/{slug}", TestContext.Current.CancellationToken);
        if (loginResp.StatusCode is not (HttpStatusCode.Redirect
            or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"Dev login for persona '{slug}' failed: {(int)loginResp.StatusCode} {loginResp.StatusCode}");
        }

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();

        var email = $"dev-{slug}@localhost";
        var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
        var userId = (await userEmailService.FindByAddressAsync(email, aliased: false, verifiedOnly: true, TestContext.Current.CancellationToken))
                .FirstOrDefault()?.UserId
            ?? throw new InvalidOperationException(
                $"Persona '{slug}' was not found after dev login (email {email}).");
        var user = await db.Users
            .AsNoTracking()
            .FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);

        // ConsentRecord is append-only (DB triggers block UPDATE/DELETE): insert only.
        await SeedMissingConsentsAsync(
            scope.ServiceProvider.GetRequiredService<LegalDbContext>(), user.Id);

        return user.Id;
    }

    /// <summary>
    /// Signs in a persona held on the Consents step, so anything gated on "still onboarding" renders. Against a complete
    /// user a "banner must not show a raw key" assertion passes on an empty string; two misbound keys shipped that way.
    /// </summary>
    /// <remarks>
    /// Order matters: sign in first, then seed a fresh required document. Dev login auto-consents to every required
    /// document present at login, so seeding first, or reusing an earlier document, leaves nothing outstanding. Both
    /// consent caches are invalidated because they snapshotted the empty document set at startup.
    /// </remarks>
    /// <returns>The persona's user id.</returns>
    public async Task<Guid> SignInAsMidOnboardingAsync(HttpClient client, DevPersona persona)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(persona);

        var now = SystemClock.Instance.GetCurrentInstant();

        // Sign in while nothing is outstanding; see the remarks.
        var slug = persona.Slug.ToLowerInvariant();
        var loginResp = await client.GetAsync($"/dev/login/{slug}", TestContext.Current.CancellationToken);
        if (loginResp.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"Dev login for persona '{slug}' failed: {(int)loginResp.StatusCode} {loginResp.StatusCode}");
        }

        // A required document that did not exist when the persona signed in.
        var documentId = Guid.NewGuid();
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

            db.LegalDocuments.Add(new LegalDocument
            {
                Id = documentId,
                Name = $"Mid-onboarding required document ({slug}, {documentId:N})",
                TeamId = SystemTeamIds.Volunteers,
                IsRequired = true,
                IsActive = true,
                GracePeriodDays = 7,
                CurrentCommitSha = new string('0', 40),
                CreatedAt = now,
                LastSyncedAt = now,
            });
            db.DocumentVersions.Add(new DocumentVersion
            {
                Id = Guid.NewGuid(),
                LegalDocumentId = documentId,
                VersionNumber = "v1",
                CommitSha = new string('0', 40),
                Content = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en"] = "Required for the mid-onboarding persona.",
                    ["es"] = "Requerido para la persona a medio registrar.",
                },
                EffectiveFrom = now - Duration.FromDays(1),
                RequiresReConsent = false,
                CreatedAt = now - Duration.FromDays(1),
                ChangesSummary = null,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Services.GetRequiredService<CachingLegalDocumentSyncService>().InvalidateAll();
        Services.GetRequiredService<CachingConsentService>().Clear();

        using var idScope = Services.CreateScope();
        var email = $"dev-{slug}@localhost";
        var userId = (await idScope.ServiceProvider
                .GetRequiredService<IUserEmailService>()
                .FindByAddressAsync(email, aliased: false, verifiedOnly: true, TestContext.Current.CancellationToken))
                .FirstOrDefault()?.UserId
            ?? throw new InvalidOperationException(
                $"Persona '{slug}' was not found after dev login (email {email}).");

        // Without this check the helper can silently regress to fully onboarded and every test on it goes vacuous.
        var step = await idScope.ServiceProvider
            .GetRequiredService<IOnboardingWidgetState>()
            .GetCurrentStepAsync(userId, TestContext.Current.CancellationToken);
        if (step == OnboardingWidgetStep.Complete)
        {
            throw new InvalidOperationException(
                $"SignInAsMidOnboardingAsync produced a Complete persona '{slug}'; "
                + "the required-document seed did not take, so anything gated on mid-onboarding "
                + "would render nothing and assert nothing. Most likely cause: something signed "
                + $"document {documentId:N} on the persona's behalf — check whether a sign-in or "
                + "a consent-repair path ran after step 2, or whether the cache invalidation "
                + "above no longer reaches the reader.");
        }

        return userId;
    }

    private static async Task SeedMissingConsentsAsync(LegalDbContext db, Guid userId)
    {
        var now = SystemClock.Instance.GetCurrentInstant();

        // As LegalDocumentSyncService does: the latest already-effective version per required document. Seeding every
        // version would consent to future ones and hide re-consent regressions.
        var documentGroups = await db.DocumentVersions
            .AsNoTracking()
            .Where(v => v.LegalDocument.IsRequired
                     && v.LegalDocument.IsActive
                     && v.EffectiveFrom <= now)
            .Select(v => new { v.Id, v.LegalDocumentId, v.EffectiveFrom, v.Content })
            .ToListAsync(TestContext.Current.CancellationToken);

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
            .ToListAsync(TestContext.Current.CancellationToken);
        var alreadyConsented = alreadyConsentedIds.ToHashSet();

        foreach (var version in currentVersions)
        {
            if (alreadyConsented.Contains(version.Id))
                continue;

            // Hash the canonical ("es") content, as ConsentService does.
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

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>A test host's files never change, and each watch costs an inotify instance it never gives back.</summary>
internal sealed class NonWatchingFileProvider(IFileProvider inner) : IFileProvider
{
    public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

/// <summary>The slug is the <c>/dev/login/{slug}</c> route segment.</summary>
public sealed record DevPersona(string Slug)
{
    /// <summary>Approved, consent-check cleared, Volunteers team.</summary>
    public static readonly DevPersona Volunteer = new("volunteer");

    /// <summary>Volunteers team plus the Admin role.</summary>
    public static readonly DevPersona Admin = new("admin");

    /// <summary>Volunteers and Board teams.</summary>
    public static readonly DevPersona Board = new("board");

    /// <summary>Has a seeded test department and sub-team.</summary>
    public static readonly DevPersona Coordinator = new("coordinator");
}
