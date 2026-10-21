using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Grants.Tests.Infrastructure;
using Humans.Grants.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Grants.Tests.ViewComponents;

/// <summary>
/// The member's own card keeps grants that ended this year or later, judged by when the grant
/// ended (revocation, else expiry), not by its program's year.
/// </summary>
public sealed class MyGrantsViewComponentTests : GrantsTestHarness
{
    [HumansFact]
    public async Task KeepsGrantsEndedThisYear_FromAnOlderProgram_DropsOnesEndedEarlier()
    {
        var holder = SeedUser("Holder");
        var older = await SeedProgramAsync(name: "Older");
        await using (var db = OpenContext())
        {
            (await db.Programs.SingleAsync(p => p.Id == older.Id, Ct)).Year = Today.Year - 1;
            await db.SaveChangesAsync(Ct);
        }
        var revokedThisYear = await SeedGrantAsync(older, holder, revoked: true);
        var expiredThisYear = await SeedGrantAsync(older, holder, expiresOn: Today.PlusDays(-1));
        await SeedGrantAsync(older, holder, expiresOn: new LocalDate(Today.Year - 1, 12, 31));

        var model = await CardAsync(holder);

        model.Grants.Select(g => g.Id).Should().BeEquivalentTo([revokedThisYear.Id, expiredThisYear.Id]);
    }

    [HumansFact]
    public async Task ACookieStillCarryingAMergedAwayId_SeesTheSurvivorsGrants()
    {
        var archived = Guid.NewGuid();
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var grant = await SeedGrantAsync(await SeedProgramAsync(), survivor);

        var model = await CardAsync(survivor, viewerClaim: archived);

        model.Grants.Select(g => g.Id).Should().Equal(grant.Id);
    }

    private async Task<GrantsCardViewModel> CardAsync(Guid holder, Guid? viewerClaim = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, (viewerClaim ?? holder).ToString())], "test"))
        };
        var component = new MyGrantsViewComponent(NewService(), Users)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } }
        };

        var result = await component.InvokeAsync(holder, ProfileCardViewMode.Self);
        return result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<GrantsCardViewModel>().Subject;
    }
}
