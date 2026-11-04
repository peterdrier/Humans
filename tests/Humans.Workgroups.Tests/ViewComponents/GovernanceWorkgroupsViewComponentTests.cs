using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Humans.Workgroups.Services;
using Humans.Workgroups.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Workgroups.Tests.ViewComponents;

public sealed class GovernanceWorkgroupsViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ASensitiveGroup_ShowsNoMeetingsOrDocuments()
    {
        var now = Instant.FromUtc(2026, 3, 1, 12, 0);
        var meeting = new WorkgroupMeetingInfo(Guid.NewGuid(), "Private sync", now.Plus(Duration.FromDays(1)),
            now.Plus(Duration.FromDays(1)).Plus(Duration.FromHours(1)), null, null, true, null, null, now, now);
        var document = new WorkgroupDocumentInfo(Guid.NewGuid(), Guid.NewGuid(), "Draft", Humans.Workgroups.Domain.WorkgroupDocumentKind.Other,
            "Body", Humans.Workgroups.Domain.WorkgroupDocumentStatus.Published, ["Scope"], now.Minus(Duration.FromHours(1)),
            now.Plus(Duration.FromDays(7)), null, null, null, null, null, null, null, now, now, []);
        WorkgroupInfo Group(string name, bool sensitive) => new(Guid.NewGuid(), name.ToLowerInvariant(), name, "Purpose",
            "A report", Humans.Workgroups.Domain.WorkgroupDeliverableKind.Report, Humans.Workgroups.Domain.WorkgroupAudience.Board,
            null, Humans.Workgroups.Domain.WorkgroupStatus.Active, null, null, null, null, null, now, now, null,
            [],
            [
                meeting with { Id = Guid.NewGuid(), StartUtc = now.Plus(Duration.FromDays(5)), EndUtc = now.Plus(Duration.FromDays(5)).Plus(Duration.FromHours(1)) },
                meeting with { Id = Guid.NewGuid(), StartUtc = now.Minus(Duration.FromDays(1)), EndUtc = now.Minus(Duration.FromDays(1)).Plus(Duration.FromHours(1)) },
                meeting,
            ], [], [document], null, null, null, sensitive);
        var workgroups = Substitute.For<IWorkgroupService>();
        workgroups.GetRegisterAsync(Arg.Any<CancellationToken>())
            .Returns([Group("Open", false), Group("Secret", true)]);
        var component = new GovernanceWorkgroupsViewComponent(
            workgroups, new NodaTime.Testing.FakeClock(now))
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() }
            }
        };

        var result = await component.InvokeAsync();

        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<GovernanceWorkgroupsViewModel>().Which;
        model.Active.Single(w => string.Equals(w.Name, "Open", StringComparison.Ordinal)).NextMeeting(now).Should().Be(meeting);
        model.Active.Single(w => string.Equals(w.Name, "Open", StringComparison.Ordinal)).IsOpenForComment(now).Should().BeTrue();
        model.Active.Single(w => string.Equals(w.Name, "Secret", StringComparison.Ordinal)).NextMeeting(now).Should().BeNull();
        model.Active.Single(w => string.Equals(w.Name, "Secret", StringComparison.Ordinal)).IsOpenForComment(now).Should().BeFalse();
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var workgroups = Substitute.For<IWorkgroupService>();
        workgroups.GetRegisterAsync(aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<WorkgroupInfo>>(
                new OperationCanceledException(aborted.Token)));
        var component = new GovernanceWorkgroupsViewComponent(
            workgroups, SystemClock.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task CoordinatorNames_PreserveOrderAndSeparatorsThroughHumanRendering(string language)
    {
        using var culture = new CultureScope(language);
        var now = Instant.FromUtc(2026, 11, 3, 1, 0);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        const string first = "First <script>&";
        const string second = "Second & <b>";
        users.GetUserInfoAsync(firstId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = firstId, BurnerName = first }, [], [], null));
        users.GetUserInfoAsync(secondId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = secondId, BurnerName = second }, [], [], null));
        var model = Group(now) with
        {
            Members =
            [
                new(Guid.NewGuid(), secondId, WorkgroupMemberRole.Coordinator, now, null),
                new(Guid.NewGuid(), firstId, WorkgroupMemberRole.Coordinator, now - Duration.FromDays(1), null),
                new(Guid.NewGuid(), Guid.NewGuid(), WorkgroupMemberRole.Coordinator, now + Duration.FromDays(1), null),
                new(Guid.NewGuid(), Guid.NewGuid(), WorkgroupMemberRole.Coordinator, now, now),
                new(Guid.NewGuid(), Guid.NewGuid(), WorkgroupMemberRole.Coordinator, now, null, IsPending: true),
                new(Guid.NewGuid(), Guid.NewGuid(), WorkgroupMemberRole.Member, now, null),
            ]
        };

        var html = await RenderAsync("/Views/Shared/_WorkgroupCoordinators.cshtml", model, users);
        var unknown = new ResourceManager(typeof(SharedResource))
            .GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;

        PlainText(html).Trim().Should().Be($"{first}, {second}, {unknown}");
        html.Should().Contain(HtmlEncoder.Default.Encode(first)).And.Contain(HtmlEncoder.Default.Encode(second));
        html.Should().NotContain("<script>").And.NotContain("<b>").And.NotContain("<a ");
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    [Xunit.InlineData("nl")]
    public async Task DocumentAttribution_RendersEncodedHumansInsideTheLocalizedSentence(string language)
    {
        using var culture = new CultureScope(language);
        var now = Instant.FromUtc(2026, 11, 3, 1, 0);
        var editorId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        const string editor = "Editor <script>&";
        const string author = "Author <b>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(editorId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = editorId, BurnerName = editor }, [], [], null));
        users.GetUserInfoAsync(authorId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = authorId, BurnerName = author }, [], [], null));
        var group = Group(now);
        var documentId = Guid.NewGuid();
        var document = new WorkgroupDocumentInfo(documentId, group.Id, "Document", WorkgroupDocumentKind.Other,
            "Body", WorkgroupDocumentStatus.Published, ["Scope"], null, null,
            null, null, null, null, null, Guid.NewGuid(), editorId, now, now,
            [new(Guid.NewGuid(), documentId, "Scope", authorId, "Comment", now,
                WorkgroupCommentDisposition.Pending, null, null, null, null, null, null)]);
        var model = new DocumentPageViewModel
        {
            Workgroup = group,
            Document = document,
            Now = now,
            CurrentUserId = Guid.NewGuid(),
            IsMember = false,
            CanAdminister = false,
            CanDoMemberWork = false,
        };

        var html = await RenderAsync("/Views/Workgroups/Document.cshtml", model, users);
        var expected = string.Format(CultureInfo.CurrentCulture,
            new ResourceManager(typeof(WorkgroupsResource)).GetString("Workgroups_LastEditedBy", CultureInfo.CurrentUICulture)!,
            editor, now.ToDateTime());

        PlainText(html).Should().Contain(expected).And.Contain(author);
        html.Should().Contain(HtmlEncoder.Default.Encode(editor)).And.Contain(HtmlEncoder.Default.Encode(author));
        html.Should().NotContain(editor).And.NotContain(author);
    }

    private static WorkgroupInfo Group(Instant now) => new(Guid.NewGuid(), "group", "Group", "Purpose", "Report",
        WorkgroupDeliverableKind.Report, WorkgroupAudience.Board, null, WorkgroupStatus.Active,
        null, null, null, null, null, now, now, null, [], [], [], [], null, null, null);

    private static string PlainText(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty, RegexOptions.NonBacktracking));

    private static async Task<string> RenderAsync(string path, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GovernanceWorkgroupsViewComponentTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(WorkgroupsResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            .AddViewLocalization();
        builder.Services.AddSingleton(users);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>().GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
        };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }

}
