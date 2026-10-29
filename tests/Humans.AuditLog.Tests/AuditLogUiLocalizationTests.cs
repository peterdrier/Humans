using AwesomeAssertions;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Models;
using Humans.Base.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Humans.AuditLog.Tests;

public class AuditLogUiLocalizationTests
{
    [HumansFact]
    public void EnglishNarrative_SuppressesSubjectWithoutDanglingPreposition_AndUsesSelfForm()
    {
        using var culture = new CultureScope("en");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditLogResource>>();

        TestActions.RoleAssigned.ToLocalizedAuditVerb(false, localizer).Should().Be("assigned role to");
        TestActions.RoleAssigned.ToLocalizedAuditVerb(true, localizer).Should().Be("assigned role");
        TestActions.ShiftSignupCreated.ToLocalizedAuditVerb(false, localizer).Should().Be("created signup for");
        TestActions.ShiftSignupCreated.ToLocalizedAuditVerb(true, localizer).Should().Be("signed up for");
        TestActions.CampaignCreated.ToLocalizedAuditVerb(false, localizer).Should().BeNull();
        TestActions.CampaignCreated.ToAuditBadgeLabel(localizer).Should().Be("Campaign created");
        TestActions.AnomalousPermissionDetected.ToAuditBadgeLabel(localizer).Should().Be("Anomalous permission detected");
    }

    [HumansFact]
    public void ActionWithoutResxEntries_FallsBackToItsEnglishVerbAndHumanizedName()
    {
        using var culture = new CultureScope("es");
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditLogResource>>();
        var fresh = new AuditActionDefinition("GrantHandedOver", Verb: "handed a grant to");

        fresh.ToLocalizedAuditVerb(false, localizer).Should().Be("handed a grant to");
        fresh.ToLocalizedAuditVerb(true, localizer).Should().Be("handed a grant");
        fresh.ToAuditBadgeLabel(localizer).Should().Be("Grant Handed Over");
        fresh.ToAuditBadgeLabel().Should().Be("GrantHandedOver");
    }

    [HumansTheory]
    [InlineData("en", "System", "assigned role")]
    [InlineData("es", "Sistema", "asignó un rol")]
    [InlineData("de", "System", "hat eine Rolle verliehen")]
    [InlineData("it", "Sistema", "ha assegnato un ruolo")]
    [InlineData("fr", "Système", "a attribué un rôle")]
    [InlineData("ca", "Sistema", "ha assignat un rol")]
    public async Task MemberLine_RendersLocalizedActorAndVerb_WithoutChangingStoredDescription(
        string cultureName, string systemLabel, string subjectlessVerb)
    {
        using var culture = new CultureScope(cultureName);
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<AuditLogResource>>();
        var entry = new AuditEvent(Guid.NewGuid(), Instant.FromUtc(2026, 10, 7, 3, 0),
            TestActions.RoleAssigned, null, null, "User", Guid.NewGuid(), null, null,
            null, null, null, null, null, "role '<Board>'");
        var item = new RazorCompiledItemLoader().LoadItems(typeof(AuditLogResource).Assembly)
            .Single(i => string.Equals(i.Identifier,
                "/Views/Shared/Components/AuditLog/_Entry.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<AuditEvent>)Activator.CreateInstance(item.Type)!;
        item.Type.GetProperty("Localizer")!.SetValue(page, localizer);
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<AuditEvent>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = entry,
        };
        page.ViewContext = new ViewContext { Writer = writer, ViewData = data };
        page.ViewData = data;

        await page.ExecuteAsync();

        writer.ToString().Should().Contain(HtmlEncoder.Default.Encode(systemLabel));
        writer.ToString().Should().Contain(HtmlEncoder.Default.Encode(subjectlessVerb));
        writer.ToString().Should().Contain("role &#x27;&lt;Board&gt;&#x27;");
        writer.ToString().Should().NotContain("AuditLog_");
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
