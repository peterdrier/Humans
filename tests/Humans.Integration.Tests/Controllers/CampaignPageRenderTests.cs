using System.Net;
using AwesomeAssertions;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Campaigns page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Campaigns</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure mode a G5 move introduces renders as a <b>200 with degraded content</b>: a
/// section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>, so a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops.
/// <c>&lt;vc:human&gt;</c> on the grants grid is the one Campaigns uses — and it only appears
/// once a grant exists, which is why the fixture seeds one.
/// </para>
/// <para>
/// There is no resx half here: Campaigns' views carry no <c>Localizer[…]</c> call and
/// <c>SharedResource</c> has no <c>Campaign_</c> key, so the section ships no resource set and
/// no satellite assemblies (§15 step 3b — Finance and Gate are the other two). The structural
/// guard for that is <c>CampaignsArchitectureTests.SectionTypesTakeNoStringLocalizer</c>.
/// Campaigns ships no <c>wwwroot/</c> either, so there is no static-asset half.
/// </para>
/// </remarks>
public class CampaignPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// One Active campaign, one code, one grant to the signed-in admin. Without a grant the
    /// Detail page renders an empty grants grid and the <c>&lt;vc:human&gt;</c> check —
    /// the whole point of the render pass — asserts nothing. Each integration test class owns
    /// its database (nobodies-collective/Humans#983), so this disturbs nothing. Campaigns has
    /// no §15 caching decorator, so seeding through the section's own context is enough.
    /// </summary>
    private async Task<Guid> SeedCampaignWithGrantAsync(Guid adminUserId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();

        var campaignId = Guid.NewGuid();
        var codeId = Guid.NewGuid();

        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            Title = "Render Test Campaign",
            Description = "Seeded by CampaignPageRenderTests",
            EmailSubject = "Your code",
            EmailBodyTemplate = "<p>Hi {{Name}}, your code is {{Code}}</p>",
            Status = CampaignStatus.Active,
            CreatedAt = now,
            CreatedByUserId = adminUserId,
        });
        db.CampaignCodes.Add(new CampaignCode
        {
            Id = codeId,
            CampaignId = campaignId,
            Code = "RENDER-1",
            ImportOrder = 1,
            ImportedAt = now,
        });
        db.CampaignGrants.Add(new CampaignGrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            CampaignCodeId = codeId,
            UserId = adminUserId,
            AssignedAt = now,
            LatestEmailStatus = Base.Enums.EmailOutboxStatus.Sent,
            LatestEmailAt = now,
        });

        await db.SaveChangesAsync(ct);
        return campaignId;
    }

    [HumansFact(Timeout = 120000)]
    public async Task Every_campaign_page_renders_without_unbound_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminUserId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var campaignId = await SeedCampaignWithGrantAsync(adminUserId, ct);

        string[] pages =
        [
            "/Campaigns/Admin",
            "/Campaigns/Admin/Create",
            $"/Campaigns/Admin/{campaignId}",
            $"/Campaigns/Admin/Edit/{campaignId}",
            $"/Campaigns/Admin/{campaignId}/SendWave",
        ];

        foreach (var url in pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper's rename refactoring rewrites <vc:name> to <name-view-component>,
            // which is equally inert (the "<vc:*> rename hazard" in G5-SECTION-TEMPLATE.md).
            html.Should().NotContain("-view-component", $"GET {url} left a renamed view-component tag unrendered");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_detail_page_renders_the_grant_recipient_through_the_human_component()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminUserId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var campaignId = await SeedCampaignWithGrantAsync(adminUserId, ct);

        var html = await (await Client.GetAsync($"/Campaigns/Admin/{campaignId}", ct))
            .Content.ReadAsStringAsync(ct);

        html.Should().Contain("Render Test Campaign");
        html.Should().Contain("RENDER-1");
        html.Should().Contain($"data-user-id=\"{adminUserId}\"",
            because: "the rendered <vc:human> emits the popover hook; the unrendered literal element does not");
    }
}
