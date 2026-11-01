using AwesomeAssertions;
using Humans.Calendar.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NodaTime;

namespace Humans.Calendar.Tests.Contracts;

public sealed class UserCalendarViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_OrdersCommitmentsByStart()
    {
        var userId = Guid.NewGuid();
        var early = Instant.FromUtc(2026, 7, 1, 10, 0);
        var late = early.Plus(Duration.FromDays(1));
        var feed = Substitute.For<IICalFeedService>();
        feed.GetFeedItemsAsync(userId, Arg.Any<CancellationToken>()).Returns(new List<CalendarFeedItem>
        {
            new("late", "Events", "Late", null, late, late.Plus(Duration.FromHours(1)), null, null),
            new("early", "Shifts", "Early", null, early, early.Plus(Duration.FromHours(1)), null, null),
        });
        var component = new UserCalendarViewComponent(feed, Substitute.For<IUserService>(),
            NullLogger<UserCalendarViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext(),
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var result = await component.InvokeAsync(userId);
        var model = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<UserCalendarViewModel>().Subject;

        model.Items.Select(i => i.Uid).Should().Equal("early", "late");
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));

        var component = new UserCalendarViewComponent(
            Substitute.For<IICalFeedService>(), users,
            NullLogger<UserCalendarViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
