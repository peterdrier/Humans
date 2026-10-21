using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// The per-member outbox pages served by Email (Users decomposition commit 3): the member's own
/// at <c>/Email/Outbox</c>, the admin's at <c>/Email/Admin/{id}/Outbox</c>.
/// </summary>
public sealed class MemberOutboxControllerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IEmailOutboxService _outbox = Substitute.For<IEmailOutboxService>();

    private MemberOutboxController BuildController(Guid? signedInAs, CancellationToken aborted = default)
    {
        var claims = signedInAs is { } id ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) } : [];
        return new MemberOutboxController(_users, _outbox)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                    RequestAborted = aborted,
                },
            },
        };
    }

    private void GivenKnownUser(Guid id) =>
        _users.GetUserInfoAsync(id, Arg.Any<CancellationToken>()).Returns(new UserInfo(
            id, "Member", false, "en", null, Instant.FromUnixTimeSeconds(0),
            null, null, null, null, null, null, null,
            null, null, null, [], [], [], null, []));

    private static EmailOutboxMessageDto Message(Guid userId) => new(
        Guid.NewGuid(), "m@example.org", "Member", "Hello", "<p>Hi</p>", "welcome", userId,
        Humans.Base.Enums.EmailOutboxStatus.Sent, Instant.FromUnixTimeSeconds(1), null, 0, null);

    [HumansFact]
    public async Task MyOutbox_ShowsOnlyTheSignedInMembersMessages()
    {
        GivenKnownUser(UserId);
        _outbox.GetMessagesForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns([Message(UserId)]);

        var result = await BuildController(UserId).MyOutbox();

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Outbox");
        view.Model.Should().BeAssignableTo<IList<EmailOutboxMessageDto>>().Which.Should().ContainSingle();
        await _outbox.Received(1).GetMessagesForUserAsync(UserId, Arg.Any<CancellationToken>());
        await _outbox.DidNotReceive().GetMessagesForUserAsync(
            Arg.Is<Guid>(g => g != UserId), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MyOutbox_IsNotFound_WhenTheSignedInAccountIsUnknown()
    {
        var result = await BuildController(UserId).MyOutbox();

        result.Should().BeOfType<NotFoundResult>();
        await _outbox.DidNotReceive().GetMessagesForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MyOutbox_IsNotFound_WhenNobodyIsSignedIn()
    {
        var result = await BuildController(null).MyOutbox();

        result.Should().BeOfType<NotFoundResult>();
        await _outbox.DidNotReceive().GetMessagesForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MyOutbox_StopsLoading_AfterRequestCancellation()
    {
        using var request = new CancellationTokenSource();
        GivenKnownUser(UserId);
        _outbox.GetMessagesForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Array.Empty<EmailOutboxMessageDto>();
        });
        var controller = BuildController(UserId, request.Token);
        (await controller.MyOutbox()).Should().BeOfType<ViewResult>();

        await request.CancelAsync();

        await controller.Invoking(c => c.MyOutbox()).Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task MyOutbox_StopsLoading_WhenTheRequestIsAbandonedDuringTheViewerLookup()
    {
        using var request = new CancellationTokenSource();
        async ValueTask<UserInfo?> AbandonDuringLookupAsync(CancellationToken ct)
        {
            await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null;
        }
        _users.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(call => AbandonDuringLookupAsync(call.Arg<CancellationToken>()));

        await BuildController(UserId, request.Token).Invoking(c => c.MyOutbox())
            .Should().ThrowAsync<OperationCanceledException>();
        await _outbox.DidNotReceive().GetMessagesForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdminOutbox_ShowsTheNamedMembersMessagesInAdminMode()
    {
        var target = Guid.NewGuid();
        _outbox.GetMessagesForUserAsync(target, Arg.Any<CancellationToken>()).Returns([Message(target)]);

        var controller = BuildController(UserId);
        var result = await controller.AdminOutbox(target, CancellationToken.None);

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("Outbox");
        ((object?)controller.ViewBag.HumanId).Should().Be(target);
    }

    [HumansFact]
    public void Routes_AreEmailUrls()
    {
        Route(nameof(MemberOutboxController.MyOutbox)).Template.Should().Be("Email/Outbox");
        Route(nameof(MemberOutboxController.AdminOutbox)).Template.Should().Be("Email/Admin/{id:guid}/Outbox");
    }

    [HumansFact]
    public void Authorization_AnyMemberForOwnPage_HumanAdminBoardOrAdminForTheAdminPage()
    {
        // Deny path: the admin page is policy-gated; the own-page action takes no id, so a member
        // cannot read another member's outbox through it.
        typeof(MemberOutboxController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        Method(nameof(MemberOutboxController.AdminOutbox)).GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should().Be(PolicyNames.HumanAdminBoardOrAdmin);
        Method(nameof(MemberOutboxController.MyOutbox)).GetParameters().Should().BeEmpty();
        Method(nameof(MemberOutboxController.MyOutbox)).GetCustomAttribute<AuthorizeAttribute>().Should().BeNull();
    }

    private static MethodInfo Method(string name) => typeof(MemberOutboxController).GetMethod(name)!;

    private static HttpGetAttribute Route(string action) => Method(action).GetCustomAttribute<HttpGetAttribute>()!;
}
