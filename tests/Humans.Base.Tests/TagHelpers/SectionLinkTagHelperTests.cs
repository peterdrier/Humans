using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.TagHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace Humans.Base.Tests.TagHelpers;

/// <summary>
/// Two stand-in sections: a type's section is its assembly, so this test assembly is one and Base the other.
/// </summary>
public class SectionLinkTagHelperTests
{
    private static readonly Type OffController = typeof(SectionLinkTagHelperTests);
    private static readonly Type OnController = typeof(SectionRoutes);
    private static readonly string Off = SectionSwitchboard.SectionNameOf(OffController);
    private static readonly string On = SectionSwitchboard.SectionNameOf(OnController);

    private static RouteEndpoint Endpoint(
        string template, string controller, Type type, object? requiredValues = null, int order = 0) =>
        new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template, defaults: null, parameterPolicies: null, requiredValues),
            order,
            new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerName = controller,
                ControllerTypeInfo = type.GetTypeInfo(),
            }),
            template);

    private static SectionRoutes Routes(params RouteEndpoint[] endpoints) =>
        new(new DefaultEndpointDataSource(endpoints));

    private static readonly SectionRoutes Sample = Routes(
        Endpoint("Teams", "Team", OffController),
        Endpoint("Teams/{slug}", "Team", OffController),
        Endpoint("Profile/{id}", "Profile", OnController),
        Endpoint("{controller}/{action=Index}/{id?}", "Camp", OffController, new { controller = "Camp", action = "Index" }),
        Endpoint("{controller}/{action=Index}/{id?}", "Home", OnController, new { controller = "Home", action = "Privacy" }));

    [HumansFact]
    public void AControllerName_MapsToTheSectionItsTypeShipsIn()
    {
        Sample.SectionOfController("team").Should().Be(Off);
        Sample.SectionOfController("Profile").Should().Be(On);
        Sample.SectionOfController("Nowhere").Should().BeNull();
    }

    [HumansFact]
    public void AControllerNameTwoSectionsShare_MapsToNeither()
    {
        var routes = Routes(Endpoint("A", "Shared", OffController), Endpoint("B", "Shared", OnController));

        routes.SectionOfController("Shared").Should().BeNull();
    }

    [HumansTheory]
    [InlineData("/Teams")]
    [InlineData("/teams/kitchen?tab=members#roster")]
    [InlineData("/Camp")]
    [InlineData("/Camp/Index/5")]
    public void ALocalUrl_MapsToTheSectionThatRoutesIt(string url) =>
        Sample.SectionOfUrl(url).Should().Be(Off);

    [HumansTheory]
    [InlineData(null)]
    [InlineData("https://example.org/Teams")]
    [InlineData("//example.org/Teams")]
    [InlineData("#roster")]
    [InlineData("/css/site.css")]
    [InlineData("/Camp/Details")]
    public void AnythingElse_MapsToNoSection(string? url) =>
        Sample.SectionOfUrl(url).Should().BeNull();

    [HumansFact]
    public void TheRequestsPathBase_IsNotPartOfTheRoute()
    {
        Sample.SectionOfUrl("/humans/Teams/kitchen", "/humans").Should().Be(Off);
        Sample.SectionOfUrl("/Home/Privacy", "/humans").Should().Be(On);
    }

    [HumansFact]
    public void WhereTemplatesOverlap_TheMoreSpecificOneDecides()
    {
        var routes = Routes(
            Endpoint("Admin/{page}", "Admin", OnController),
            Endpoint("Admin/Teams", "TeamAdmin", OffController));

        routes.SectionOfUrl("/Admin/Teams").Should().Be(Off);
        routes.SectionOfUrl("/Admin/Other").Should().Be(On);
    }

    private static ViewContext View(params string[] switchedOff)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                .BuildServiceProvider(Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                    .AddSingleton(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), Sample)),
        };
        if (switchedOff.Length > 0)
            httpContext.SetSwitchedOffSections(new HashSet<string>(switchedOff, StringComparer.OrdinalIgnoreCase));
        return new ViewContext { HttpContext = httpContext };
    }

    private static TagHelperOutput Link(
        ViewContext view, string? controller = null, string? href = null, bool keepText = false,
        IDictionary<object, object>? items = null)
    {
        var attributes = new TagHelperAttributeList();
        if (href is not null)
            attributes.Add("href", href);
        if (keepText)
            attributes.Add(new TagHelperAttribute("keep-text"));
        var output = new TagHelperOutput("a", attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        var context = new TagHelperContext(
            new TagHelperAttributeList(), items ?? new Dictionary<object, object>(), "link");

        new SectionLinkTagHelper { Controller = controller, ViewContext = view }.Process(context, output);
        return output;
    }

    [HumansFact]
    public void WithNothingSwitchedOff_ALinkRendersAsWritten()
    {
        var output = Link(View(), controller: "Team", keepText: true);

        output.TagName.Should().Be("a");
        output.Attributes.ContainsName("keep-text").Should().BeFalse();
    }

    [HumansFact]
    public void ALinkIntoASwitchedOffSection_IsDropped_ByControllerOrByUrl()
    {
        var byController = Link(View(Off), controller: "Team");
        var byUrl = Link(View(Off), href: "/Teams/kitchen");

        byController.TagName.Should().BeNull();
        byController.IsContentModified.Should().BeTrue("the link text goes with the link");
        byUrl.TagName.Should().BeNull();
    }

    [HumansFact]
    public void LinksElsewhere_Stay()
    {
        Link(View(Off), controller: "Profile").TagName.Should().Be("a");
        Link(View(Off), href: "/Profile/7").TagName.Should().Be("a");
        Link(View(Off), href: "https://example.org/Teams").TagName.Should().Be("a");
        Link(View(On), controller: "Team").TagName.Should().Be("a");
    }

    [HumansFact]
    public void KeepText_DropsOnlyTheAnchor()
    {
        var output = Link(View(Off), controller: "Team", keepText: true);

        output.TagName.Should().BeNull();
        output.IsContentModified.Should().BeFalse("the text inside still renders");
    }

    private static async Task<TagHelperOutput> WrapAsync(ViewContext view, params (string Controller, bool KeepText)[] links)
    {
        var context = new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "wrapper");
        var output = new TagHelperOutput("li", [new TagHelperAttribute("hide-with-links")], (_, _) =>
        {
            foreach (var (controller, keepText) in links)
                Link(view, controller, keepText: keepText, items: context.Items);
            return Task.FromResult<TagHelperContent>(new DefaultTagHelperContent());
        });

        var wrapper = new HideWithLinksTagHelper { ViewContext = view };
        wrapper.Init(context);
        await wrapper.ProcessAsync(context, output);
        return output;
    }

    [HumansFact]
    public async Task AWrapper_GoesOnceEveryLinkInsideItWent()
    {
        (await WrapAsync(View(Off), ("Team", false))).TagName.Should().BeNull();
        (await WrapAsync(View(Off), ("Team", false), ("Camp", false))).TagName.Should().BeNull();
    }

    [HumansFact]
    public async Task AWrapper_StaysWhileAnythingInsideItStillRenders()
    {
        (await WrapAsync(View(Off), ("Team", false), ("Profile", false))).TagName.Should().Be("li");
        (await WrapAsync(View(Off), ("Team", true))).TagName.Should().Be("li");
        (await WrapAsync(View(Off))).TagName.Should().Be("li");

        var untouched = await WrapAsync(View(), ("Team", false));
        untouched.TagName.Should().Be("li");
        untouched.Attributes.ContainsName("hide-with-links").Should().BeFalse();
    }
}
