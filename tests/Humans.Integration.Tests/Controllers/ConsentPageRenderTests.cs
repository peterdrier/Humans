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

/// <summary>
/// Renders every Consent page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Consent</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build.
/// </description></item>
/// <item><description>
/// The resx carve moved 36 keys out of <c>SharedResource</c>. A key the carve missed, or a
/// call site left bound to the wrong set, renders the raw key in every language with no error.
/// Two of those call sites are <b>outside</b> the section — Shell's onboarding widget and
/// Governance's statutes page both bind <c>IStringLocalizer&lt;ConsentResource&gt;</c> now —
/// and <c>_ConsentReviewBody</c> reads three of the keys through a
/// <c>ResourceManager</c> keyed on the marker type, which the compiler cannot check.
/// </description></item>
/// <item><description>
/// <c>_ConsentReviewBody</c> moved into the section's <c>Views/Shared/</c> and is still
/// rendered from Shell's onboarding widget by name across application parts, with
/// <c>ConsentReviewFormViewModel</c> on the contracts leaf so Shell can construct it.
/// </description></item>
/// </list>
/// <para>
/// Consent ships no <c>wwwroot/</c>, so there is no static-asset half here.
/// </para>
/// </remarks>
public class ConsentPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string DocumentName = "Integration Test Agreement";
    private const string EnglishBody = "This is the English body of the integration test agreement.";
    private const string SpanishBody = "Este es el cuerpo en espanol del acuerdo de prueba.";

    /// <summary>
    /// Writes one required document + version straight through the section's own DbContext and
    /// then clears the §15 decorator. <c>CachingLegalDocumentSyncService</c> is a Singleton with
    /// <c>warmOnStartup: true</c>, so it has already snapshotted an empty document set before the
    /// test body runs and a plain context write is invisible to every read (Calendar's rule).
    /// Each integration test class owns its database (nobodies-collective/Humans#983).
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

        Factory.Services.GetRequiredService<ILegalDocumentCacheInvalidator>().InvalidateAll();
        Factory.Services.GetRequiredService<IConsentCacheInvalidator>().InvalidateAll();

        return versionId;
    }

    /// <summary>Every prefix the carve took into <c>ConsentResource</c>.</summary>
    private static readonly string[] CarvedPrefixes = ["Consent_", "ConsentReview_", "ConsentIndex_"];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("<vc:", $"{what} left a view-component tag unrendered");
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

        html.Should().Contain("Agreements");            // ConsentIndex_Title
        html.Should().Contain("Consent History");       // Consent_History
        html.Should().Contain("About Consent");         // Consent_AboutTitle
        // Nav_Consents stayed in SharedResource (Humans.UI's login partial renders it too), so
        // the section binds a second localizer for it.
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
        // _ConsentReviewBody's checkbox copy and the ResourceManager-driven per-language JSON
        // both come from ConsentResource; a marker left pointing at SharedResource renders the
        // raw key here and nowhere else.
        html.Should().Contain("I have read and understood this document");   // ConsentReview_ConsentCheckbox
        html.Should().Contain("Give Consent");                               // ConsentReview_GiveConsent
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_onboarding_widget_renders_the_sections_partial_from_shell()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        // Seeded after sign-in, so the fixture's auto-consent has not covered it and the widget
        // has an unsigned document to show.
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        var response = await Client.GetAsync("/OnboardingWidget/Consents", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // Shell's widget view constructs ConsentReviewFormViewModel off the contracts leaf and
        // renders the section's _ConsentReviewBody by name; a partial that fails to resolve
        // throws, so a 200 with the checkbox copy is the proof both halves landed.
        AssertRenderedCleanly(html, "GET /OnboardingWidget/Consents");
        html.Should().Contain(DocumentName);
        html.Should().Contain("I have read and understood this document");
        html.Should().Contain("Give Consent");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_statutes_page_and_the_admin_document_list_render()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // /Legal is [AllowAnonymous] and reads GitHub, not the database; when the fetch yields
        // nothing the tabs render their empty message rather than failing.
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
        // An English-only check passes whether or not the RCL's satellites shipped — the
        // neutral set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the assertions
        // stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        await SeedRequiredDocumentAsync(SystemTeamIds.Volunteers, ct);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture provider
        // returns the user's PreferredLanguage and short-circuits the rest of the chain, and
        // /Consent is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Consent", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Consent", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        AssertRenderedCleanly(html, "GET /Consent (es)");

        html.Should().Contain("Acuerdos");                      // ConsentIndex_Title
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
