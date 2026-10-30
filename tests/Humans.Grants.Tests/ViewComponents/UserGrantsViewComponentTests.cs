using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Grants.Authorization;
using Humans.Grants.Tests.Infrastructure;
using Humans.Grants.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Grants.Tests.ViewComponents;

/// <summary>
/// The admin Grants card links a program only where the viewer passes the program page's own
/// checks, so a HumanAdmin who may see the card never gets a link that answers 403.
/// </summary>
public sealed class UserGrantsViewComponentTests : GrantsTestHarness
{
    [HumansFact]
    public async Task HumanAdmin_SeesTheCard_WithoutProgramLinks()
    {
        var (holder, a, _) = await SeedTwoProgramsAsync(manager: null);

        var model = await CardAsync(holder, SeedUser("Human admin"), UsersRoles.UsersAdmin);

        model.Grants.Should().HaveCount(2);
        model.LinkablePrograms.Should().NotContain(a);
        model.LinkablePrograms.Should().BeEmpty();
    }

    [HumansFact]
    public async Task HumanAdminWhoManagesOneProgram_GetsALinkToThatProgramOnly()
    {
        var manager = SeedUser("Manager");
        var (holder, a, _) = await SeedTwoProgramsAsync(manager);

        var model = await CardAsync(holder, manager, UsersRoles.UsersAdmin);

        model.LinkablePrograms.Should().BeEquivalentTo([a]);
    }

    [HumansFact]
    public async Task Board_GetsEveryProgramLink()
    {
        var (holder, a, b) = await SeedTwoProgramsAsync(manager: null);

        var model = await CardAsync(holder, SeedUser("Board member"), RoleNames.Board);

        model.LinkablePrograms.Should().BeEquivalentTo([a, b]);
    }

    private async Task<(Guid Holder, Guid A, Guid B)> SeedTwoProgramsAsync(Guid? manager)
    {
        var holder = SeedUser("Holder");
        var a = await SeedProgramAsync(name: "A", managers: manager is { } m ? [m] : []);
        var b = await SeedProgramAsync(name: "B");
        await SeedGrantAsync(a, holder);
        await SeedGrantAsync(b, holder);
        return (holder, a.Id, b.Id);
    }

    private async Task<GrantsCardViewModel> CardAsync(Guid holder, Guid viewer, string role)
    {
        var service = NewService();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
            {
                ((ISectionPolicies)new Section()).AddPolicies(options);
                options.AddPolicy(UsersPolicies.Read, policy =>
                    policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Board, RoleNames.Admin));
            })
            .AddSingleton<IAuthorizationHandler>(new GrantsPolicyHandler(service, Users))
            .AddSingleton<IAuthorizationHandler>(new ProgramAuthorizationHandler(Users))
            .BuildServiceProvider();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, viewer.ToString()), new Claim(ClaimTypes.Role, role)], "test"))
        };
        var component = new UserGrantsViewComponent(service, provider.GetRequiredService<IAuthorizationService>())
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } }
        };

        var result = await component.InvokeAsync(holder, ProfileCardViewMode.Admin);
        return result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<GrantsCardViewModel>().Subject;
    }
}
