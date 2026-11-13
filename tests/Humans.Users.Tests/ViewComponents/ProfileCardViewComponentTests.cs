using System.Globalization;
using System.Resources;
using System.Net;
using System.Text.RegularExpressions;
using Humans.Email.Contracts;
using Humans.Users.Tests.Infrastructure;
using AwesomeAssertions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Humans.Users.ViewComponents;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class ProfileCardViewComponentTests
{
    [HumansTheory]
    [Xunit.InlineData(true, true, "valid")]
    [Xunit.InlineData(true, false, "valid")]
    [Xunit.InlineData(false, false, "valid")]
    [Xunit.InlineData(false, false, null)]
    [Xunit.InlineData(false, false, "malformed")]
    public async Task PublicCard_UsesResolvedViewerPermissionsOrHidesMissingViewer(
        bool viewerExists, bool survivorIsBoard, string? claim)
    {
        var targetId = Guid.NewGuid();
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>()).Returns(
            UserInfo.Create(new User { Id = targetId, FirstName = "Private name" }, [], [], UserFixtures.Profile()));
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(viewerExists
            ? UserInfo.Create(new User { Id = survivorId }, [], [], null) : null);
        var contacts = Substitute.For<IContactFieldService>();
        contacts.GetViewerAccessLevelAsync(targetId, survivorId, Arg.Any<CancellationToken>())
            .Returns((ContactFieldVisibility.CoordinatorsAndBoard, survivorIsBoard, false));
        var emails = Substitute.For<IUserEmailService>();
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var component = new ProfileCardViewComponent(users, contacts, emails, teams)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(claim is null ? [] :
                            [new Claim(ClaimTypes.NameIdentifier,
                                string.Equals(claim, "valid", StringComparison.Ordinal) ? cookieId.ToString() : claim)], "test"))
                    },
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var result = await component.InvokeAsync(targetId, ProfileCardViewMode.Public);

        if (!viewerExists)
        {
            result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
            contacts.ReceivedCalls().Should().BeEmpty();
            emails.ReceivedCalls().Should().BeEmpty();
            return;
        }

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<ProfileCardViewModel>().Which;
        model.CanViewLegalName.Should().Be(survivorIsBoard);
        model.FirstName.Should().Be(survivorIsBoard ? "Private name" : null);
        await contacts.Received(1).GetVisibleContactFieldsAsync(targetId, survivorId, Arg.Any<CancellationToken>());
        await emails.Received(1).GetVisibleEmailsAsync(targetId,
            ContactFieldVisibility.CoordinatorsAndBoard, Arg.Any<CancellationToken>());
        await contacts.Received(1).GetViewerAccessLevelAsync(targetId, survivorId,
            Arg.Any<CancellationToken>(), ProfileCardViewMode.Public);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task VolunteerHistoryPreview_PreservesWholeSurrogatePairs(bool fits)
    {
        var prefix = new string('a', fits ? 78 : 79);
        var html = await RenderAsync(new ProfileCardViewModel
        {
            ViewMode = ProfileCardViewMode.Public,
            VolunteerHistory = [new VolunteerHistoryEntryViewModel
                {
                    EventName = "Example", Date = new LocalDate(2026, 7, 1), Description = prefix + "😀tail",
                }],
        }, Substitute.For<IUserService>());

        html.Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "..."));
    }

    [HumansTheory]
    [Xunit.InlineData("Public")]
    [Xunit.InlineData("Self")]
    [Xunit.InlineData("Admin")]
    public async Task TeamNames_PublicCardHidesHiddenParents_WhileSelfAndAdminKeepThem(string mode)
    {
        var viewMode = Enum.Parse<ProfileCardViewMode>(mode);
        var userId = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            UserInfo.Create(new User { Id = userId, BurnerName = "Ada" }, [], [], null)));
        var parent = new TeamInfo(Guid.NewGuid(), "Hidden", null, "hidden", true, false,
            SystemTeamType.None, false, false, true, false, now,
            [new(Guid.NewGuid(), userId, "Ada", null, null, TeamMemberRole.Member, now)]);
        var child = parent with { Id = Guid.NewGuid(), Name = "Visible", Slug = "visible", IsHidden = false, ParentTeamId = parent.Id };
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [parent.Id] = parent,
            [child.Id] = child
        });
        var component = new ProfileCardViewComponent(
            users, Substitute.For<IContactFieldService>(), Substitute.For<IUserEmailService>(), teams)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
                    },
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var result = await component.InvokeAsync(userId, viewMode);

        var viewData = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData
            ?? throw new InvalidOperationException("Profile card returned no view data.");
        var model = viewData.Model.Should().BeOfType<ProfileCardViewModel>().Which;
        model.Teams.Select(team => team.TeamName).Should().Equal(viewMode == ProfileCardViewMode.Public
            ? new[] { "Visible" } : new[] { "Hidden", "Hidden - Visible" });
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new ProfileCardViewComponent(
            users, Substitute.For<IContactFieldService>(), Substitute.For<IUserEmailService>(),
            Substitute.For<ITeamService>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid(), ProfileCardViewMode.Self);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Public)]
    [Xunit.InlineData(ProfileCardViewMode.Self)]
    [Xunit.InlineData(ProfileCardViewMode.Admin)]
    public async Task Identity_UsesTheCanonicalUserName_WhilePreservingPrivateProfileFields(ProfileCardViewMode viewMode)
    {
        var userId = Guid.NewGuid();
        const string name = "Canonical <script>&";
        var profile = UserFixtures.Profile(boardNotes: "Private board notes", profilePictureContentType: "image/png");
        var info = UserInfo.Create(new User { Id = userId, BurnerName = name, FirstName = "Legal first", LastName = "Legal last" }, [], [], profile);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/old-picture-projection");
        var contacts = Substitute.For<IContactFieldService>();
        contacts.GetViewerAccessLevelAsync(userId, userId, Arg.Any<CancellationToken>(), viewMode)
            .Returns((ContactFieldVisibility.BoardOnly, viewMode != ProfileCardViewMode.Public,
                viewMode != ProfileCardViewMode.Self));
        var component = new ProfileCardViewComponent(users, contacts,
            Substitute.For<IUserEmailService>(), teams)
        {
            Url = url,
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
                    },
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
                }
            }
        };
        var result = (await component.InvokeAsync(userId, viewMode)).Should().BeOfType<ViewViewComponentResult>().Which;
        var model = result.ViewData!.Model.Should().BeOfType<ProfileCardViewModel>().Which;

        var html = await RenderAsync(model, users);

        var heading = Regex.Match(html, "<h4>(?<heading>.*?)</h4>", RegexOptions.Singleline | RegexOptions.NonBacktracking).Groups["heading"].Value;
        WebUtility.HtmlDecode(Regex.Replace(heading, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking)).Trim()
            .Should().Be(name);
        html.Should().Contain(HtmlEncoder.Default.Encode(name)).And.NotContain("Stale profile name")
            .And.NotContain("/old-picture-projection");
        html.Should().Contain(HtmlEncoder.Default.Encode(info.ProfilePictureUrl!));
        html.Contains("Legal first", StringComparison.Ordinal).Should().Be(viewMode != ProfileCardViewMode.Public);
        html.Contains("Private board notes", StringComparison.Ordinal).Should().Be(viewMode != ProfileCardViewMode.Public);
        var messageLabel = string.Format(CultureInfo.CurrentCulture,
            new ResourceManager(typeof(UsersResource)).GetString("Users_SendMessage_Button", CultureInfo.CurrentUICulture)!, name);
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking));
        text.Contains(messageLabel, StringComparison.Ordinal).Should().Be(viewMode != ProfileCardViewMode.Self);
    }

    private static async Task<string> RenderAsync(ProfileCardViewModel model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProfileCardViewComponentTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(UsersResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            // <vc:nobodies-email-badge> is GoogleIntegration's.
            .AddApplicationPart(typeof(Humans.GoogleIntegration.ViewComponents.NobodiesEmailBadgeViewComponent).Assembly)
            .AddViewLocalization();
        builder.Services.AddSingleton(users);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shared/Components/ProfileCard/Default.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Profile";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }

}
