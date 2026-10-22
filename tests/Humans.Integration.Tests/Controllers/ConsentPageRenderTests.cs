using System.Net;
using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Services;
using Humans.Base.Constants;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Consent page through the real app.</summary>
/// <remarks>
/// Failures render as a 200 with degraded content: a mis-bound key renders as its name. Two
/// <c>ConsentResource</c> call sites live outside the section, and <c>_ConsentReviewBody</c> reads three keys through
/// a <c>ResourceManager</c> the compiler can't check. Shell's onboarding widget renders that partial by name.
/// </remarks>
public class ConsentPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string DocumentName = "Integration Test Agreement";
    private const string EnglishBody = "This is the English body of the integration test agreement.";
    private const string SpanishBody = "Este es el cuerpo en espanol del acuerdo de prueba.";

    /// <summary>
    /// Written to the context, then the cache is cleared: <c>CachingLegalDocumentSyncService</c> snapshotted an empty
    /// document set at startup, so a plain write is invisible to every read.
    /// </summary>
    private async Task<Guid> SeedRequiredDocumentAsync(Guid teamId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        db.LegalDocuments.Add(new LegalDocument
        {
            Id = documentId,
            Name = DocumentName,
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 7,
            CurrentCommitSha = "0000000000000000000000000000000000000000",
            CreatedAt = now,
            LastSyncedAt = now,
        });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = documentId,
            VersionNumber = "v1",
            CommitSha = "0000000000000000000000000000000000000000",
            Content = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["es"] = SpanishBody,
                ["en"] = EnglishBody,
            },
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now - Duration.FromDays(1),
            ChangesSummary = null,
        });

        await db.SaveChangesAsync(ct);

        Factory.Services.GetRequiredService<CachingLegalDocumentSyncService>().InvalidateAll();
        Factory.Services.GetRequiredService<CachingConsentService>().Clear();

        return versionId;
    }

    private static readonly string[] CarvedPrefixes = ["Consent_", "Consent_Review_", "Consent_Index_"];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("-view-component", $"{what} has a renamed view-component element");
        foreach (var prefix in CarvedPrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_consent_dashboard_renders_its_copy_and_the_seeded_document()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        var response = await Client.GetAsync("/Consent", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Consent");

        html.Should().Contain("Agreements");            // Consent_Index_Title
        html.Should().Contain("Consent History");       // Consent_History
        html.Should().Contain("About Consent");         // Consent_AboutTitle
        // Nav_Consents stays in SharedResource (the login partial renders it too), through a second localizer.
        html.Should().NotContain("Nav_Consents");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_review_page_renders_the_sections_shared_partial()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var versionId = await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        var response = await Client.GetAsync($"/Consent/Review?id={versionId}", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Consent/Review");

        html.Should().Contain(DocumentName);
        html.Should().Contain(EnglishBody);
        // The checkbox copy and the ResourceManager-driven JSON come from ConsentResource; a marker left on
        // SharedResource renders the raw key here only.
        html.Should().Contain("I have read and understood this document");   // Consent_Review_ConsentCheckbox
        html.Should().Contain("Give Consent");                               // Consent_Review_GiveConsent
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_onboarding_widget_renders_the_sections_partial_from_shell()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        // Seeded after sign-in, so dev login hasn't auto-consented to it.
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        var response = await Client.GetAsync("/OnboardingWidget/Consents", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // Shell renders the section's _ConsentReviewBody by name and a miss throws, so a 200 with the checkbox copy is
        // the proof.
        AssertRenderedCleanly(html, "GET /OnboardingWidget/Consents");
        html.Should().Contain(DocumentName);
        html.Should().Contain("I have read and understood this document");
        html.Should().Contain("Give Consent");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_statutes_page_and_the_admin_document_list_render()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // /Legal is anonymous and reads GitHub; with nothing fetched the tabs render their empty message.
        var statutes = await Client.GetAsync("/Legal", ct);
        statutes.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertRenderedCleanly(await statutes.Content.ReadAsStringAsync(ct), "GET /Legal");

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        var admin = await Client.GetAsync("/Legal/Admin/Documents", ct);
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminHtml = await admin.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(adminHtml, "GET /Legal/Admin/Documents");
        adminHtml.Should().Contain(DocumentName);
    }

    [HumansFact(Timeout = 120000)]
    public async Task Consent_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Consent", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Consent", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Consent (es)");

        html.Should().Contain("Acuerdos");                      // Consent_Index_Title
        html.Should().Contain("Historial de consentimientos");  // Consent_History
        html.Should().NotContain("About Consent");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }
}
