using System.Net;
using AwesomeAssertions;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Campaigns page through the real app.</summary>
/// <remarks>
/// A missing <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a 200. <c>&lt;vc:human&gt;</c> on the
/// grants grid appears only once a grant exists, which is why the fixture seeds one. No resource set or
/// <c>wwwroot/</c>; <c>CampaignsArchitectureTests.SectionTypesTakeNoStringLocalizer</c> guards the former.
/// </remarks>
public class CampaignPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>
    /// One Active campaign, one code, one grant to the signed-in admin. Without a grant the grants grid renders empty
    /// and the <c>&lt;vc:human&gt;</c> check asserts nothing. No caching decorator, so seeding through the section's
    /// own context is enough.
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

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
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
