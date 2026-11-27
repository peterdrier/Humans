using AwesomeAssertions;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using NodaTime;
using Humans.Audit.Contracts;
using Humans.Audit.Models;
using Humans.Base.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Humans.Audit.Tests;

public class AuditLogUiLocalizationTests
{
    [HumansFact]
    public void Render_ActorIsSubject_UsesSelfForm()
    {
        using var culture = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditResource>>();

        TestActions.RoleAssigned.ToLocalizedAuditVerb(false, localizer).Should().Be("assigned role to");
        TestActions.RoleAssigned.ToLocalizedAuditVerb(true, localizer).Should().Be("assigned role");
        TestActions.ShiftSignupCreated.ToLocalizedAuditVerb(false, localizer).Should().Be("created signup for");
        TestActions.ShiftSignupCreated.ToLocalizedAuditVerb(true, localizer).Should().Be("signed up for");
        TestActions.CampaignCreated.ToLocalizedAuditVerb(false, localizer).Should().BeNull();
        TestActions.CampaignCreated.ToAuditBadgeLabel(localizer).Should().Be("Campaign created");
        TestActions.AnomalousPermissionDetected.ToAuditBadgeLabel(localizer).Should().Be("Anomalous permission detected");
    }

    [HumansFact]
    public void Render_ActionWithoutResx_UsesEnglishVerb()
    {
        using var culture = new CultureScope("es");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditResource>>();
        var fresh = new AuditActionDefinition("GrantHandedOver", AuditKept.Forever, Verb: "handed a grant to");

        fresh.ToLocalizedAuditVerb(false, localizer).Should().Be("handed a grant to");
        fresh.ToLocalizedAuditVerb(true, localizer).Should().Be("handed a grant");
        fresh.ToAuditBadgeLabel(localizer).Should().Be("Grant Handed Over");
        fresh.ToAuditBadgeLabel().Should().Be("GrantHandedOver");
    }

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task MemberLine_OtherCulture_RendersLocalizedVerb(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditResource>>();
        var entry = new AuditEntry(Guid.NewGuid(), Instant.FromUtc(2026, 10, 7, 3, 0),
            TestActions.RoleAssigned, null, null, "User", Guid.NewGuid(), null, null,
            null, null, null, null, null, "role '<Board>'");
        var item = new RazorCompiledItemLoader().LoadItems(typeof(AuditResource).Assembly)
            .Single(i => string.Equals(i.Identifier,
                "/Views/Shared/Components/AuditLog/_Entry.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<AuditEntry>)Activator.CreateInstance(item.Type)!;
        item.Type.GetProperty("Localizer")!.SetValue(page, localizer);
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<AuditEntry>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = entry,
        };
        page.ViewContext = new ViewContext { Writer = writer, ViewData = data };
        page.ViewData = data;

        await page.ExecuteAsync();

        writer.ToString().Should().Contain(HtmlEncoder.Default.Encode(localizer["Audit_System"]));
        writer.ToString().Should().Contain(
            $"<span>{HtmlEncoder.Default.Encode(TestActions.RoleAssigned.ToLocalizedAuditVerb(true, localizer)!)}</span>");
        writer.ToString().Should().Contain("role &#x27;&lt;Board&gt;&#x27;");
        writer.ToString().Should().NotContain("Audit_");
        entry.Description.Should().Be("role '<Board>'");
        entry.RenderPlainText().Should().Contain("System assigned role — role '<Board>'");
    }

    [HumansFact]
    public void OperatorBadge_WithoutLocalizer_ShowsTheStoredName()
    {
        TestActions.TeamMemberAdded.ToAuditBadgeLabel().Should().Be("TeamMemberAdded");
        TestActions.AnomalousPermissionDetected.ToAuditBadgeLabel().Should().Be("AnomalousPermissionDetected");
        TestActions.AnomalousPermissionDetected.ToAuditBadgeClass().Should().Be("bg-warning text-dark");
        TestActions.TeamMemberAdded.ToAuditEntryRowClass().Should().BeEmpty();
    }
}
