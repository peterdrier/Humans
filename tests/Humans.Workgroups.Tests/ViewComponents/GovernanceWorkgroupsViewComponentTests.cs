using AwesomeAssertions;
using Humans.Users.Contracts;
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
            workgroups, Substitute.For<IUserService>(), new NodaTime.Testing.FakeClock(now))
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
            workgroups, Substitute.For<IUserService>(), SystemClock.Instance)
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
}
