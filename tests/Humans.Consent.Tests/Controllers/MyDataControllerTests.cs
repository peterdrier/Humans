using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Consent.Controllers;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Controllers;

/// <summary>The My Data page shell at <c>/Consent/MyData</c>.</summary>
public class MyDataControllerTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentManagement _consentService = Substitute.For<IConsentManagement>();

    private MyDataController BuildSut(CancellationToken ct = default)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "test")),
            RequestAborted = ct,
        };
        return new MyDataController(
            _userService,
            _consentService,
            new ConfigurationBuilder().Build(),
            new ConfigurationRegistry())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static UserInfo Info(Guid id, Instant? requested = null, Instant? scheduled = null, Instant? eligibleAfter = null) =>
        UserInfo.Create(
            user: new User
            {
                Id = id,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                DeletionRequestedAt = requested,
                DeletionScheduledFor = scheduled,
                DeletionEligibleAfter = eligibleAfter,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            communicationPreferences: []);

    [HumansFact]
    public async Task Index_returns_NotFound_without_a_user()
    {
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        (await BuildSut().Index()).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Index_lists_the_documents_the_member_signed()
    {
        var versionId = Guid.NewGuid();
        var signedAt = Instant.FromUtc(2026, 3, 1, 0, 0);
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(Info(_userId));
        _consentService.GetConsentDashboardAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ConsentDashboard([], [new ConsentDashboardHistoryItem(versionId, "Statutes", "v2", signedAt)]));

        var view = (await BuildSut().Index()).Should().BeOfType<ViewResult>().Subject;

        var model = view.Model.Should().BeOfType<MyDataViewModel>().Subject;
        model.IsDeletionPending.Should().BeFalse();
        model.SignedDocuments.Should().ContainSingle().Which.DocumentVersionId.Should().Be(versionId);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task Index_reports_the_later_of_grace_deadline_and_ticket_hold(int? holdDays)
    {
        var now = Instant.FromUtc(2026, 10, 3, 0, 0);
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(Info(
            _userId,
            requested: now,
            scheduled: now + Duration.FromDays(30),
            eligibleAfter: holdDays is { } days ? now + Duration.FromDays(days) : null));
        _consentService.GetConsentDashboardAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ConsentDashboard([], []));
        var expected = (now + Duration.FromDays(Math.Max(30, holdDays ?? 0))).ToDateTimeUtc();

        var view = (await BuildSut().Index()).Should().BeOfType<ViewResult>().Subject;

        view.Model.Should().BeOfType<MyDataViewModel>().Which.DeletionScheduledFor.Should().Be(expected);
    }

    [HumansFact]
    public async Task Index_stops_loading_after_request_cancellation()
    {
        using var request = new CancellationTokenSource();
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Info(_userId);
        });
        _consentService.GetConsentDashboardAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ConsentDashboard([], []));
        var sut = BuildSut(request.Token);

        await request.CancelAsync();

        await FluentActions.Awaiting(() => sut.Index()).Should().ThrowAsync<OperationCanceledException>();
    }
}
