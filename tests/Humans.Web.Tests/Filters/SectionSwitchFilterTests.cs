using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Controllers;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace Humans.Web.Tests.Filters;

public class SectionSwitchFilterTests
{
    private readonly ISectionSwitches _switches = Substitute.For<ISectionSwitches>();

    /// <summary>Any controller that ships in a section: the filter reads the section off its assembly.</summary>
    private static readonly TypeInfo SectionController = typeof(Humans.Teams.Section).Assembly.GetTypes()
        .First(t => t is { IsAbstract: false } && typeof(Controller).IsAssignableFrom(t)).GetTypeInfo();

    private async Task<(ResourceExecutingContext Context, bool RanAction)> RunAsync(
        TypeInfo controller, params string[] switchedOff)
    {
        _switches.GetSwitchedOffAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(switchedOff, StringComparer.OrdinalIgnoreCase));
        var context = new ResourceExecutingContext(
            new ActionContext(
                new DefaultHttpContext(), new RouteData(),
                new ControllerActionDescriptor { ControllerTypeInfo = controller }),
            [], []);
        var ranAction = false;

        await new SectionSwitchFilter(_switches).OnResourceExecutionAsync(context, () =>
        {
            ranAction = true;
            return Task.FromResult(new ResourceExecutedContext(context, []));
        });

        return (context, ranAction);
    }

    [HumansFact]
    public async Task ASwitchedOffSectionsController_AnswersNotFound()
    {
        var (context, ranAction) = await RunAsync(SectionController, "Teams");

        context.Result.Should().BeOfType<NotFoundResult>();
        ranAction.Should().BeFalse();
    }

    [HumansFact]
    public async Task OtherSections_AndShellControllers_StillRun()
    {
        (await RunAsync(SectionController, "Cantina")).RanAction.Should().BeTrue();
        (await RunAsync(typeof(HomeController).GetTypeInfo(), "Teams")).Context.Result.Should().BeNull();
    }

    [HumansFact]
    public async Task TheRequest_CarriesTheSetForTheNavComponents()
    {
        var (context, _) = await RunAsync(SectionController, "Cantina");

        context.HttpContext.SwitchedOffSections().Should().BeEquivalentTo(["Cantina"]);
        new object[] { new Humans.Teams.Section() }.SwitchedOn(context.HttpContext).Should().HaveCount(1);
        (await RunAsync(SectionController, "Teams")).Context.HttpContext.SwitchedOffSections()
            .Should().Contain("teams");
    }

    /// <summary>The sections a deployment cannot run without are never offered, so nobody can lock themselves out.</summary>
    [HumansFact]
    public void TheSectionsTheAppNeeds_CannotBeSwitchedOff()
    {
        ISection[] essential =
        [
            new Humans.Auth.Section(), new Humans.Users.Section(), new Humans.Settings.Section(),
            new Humans.Consent.Section(), new Humans.Gdpr.Section(), new Humans.Onboarding.Section(),
        ];

        essential.Should().OnlyContain(section => !section.CanBeSwitchedOff);
        ((ISection)new Humans.Teams.Section()).CanBeSwitchedOff.Should().BeTrue();
    }
}
