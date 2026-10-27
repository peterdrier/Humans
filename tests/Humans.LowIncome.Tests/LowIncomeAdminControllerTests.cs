using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Grants.Contracts;
using Humans.LowIncome.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.LowIncome.Tests;

/// <summary>The stub page: Admin only, and every action goes to Grants under the low-income key.</summary>
public sealed class LowIncomeAdminControllerTests
{
    private readonly IGrantWrite _grants = Substitute.For<IGrantWrite>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Guid _actor = Guid.NewGuid();

    private readonly CapturingLogger<LowIncomeAdminController> _logger = new();

    private LowIncomeAdminController NewController()
    {
        _users.GetUserInfoAsync(_actor, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(new UserInfo(
            _actor, "Admin", false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, null, null, null, null,
            null, null, null, [], [], [],
            UserFixtures.Profile(burnerName: "Admin", membershipTier: LegacyMembershipTier.Volunteer), [])));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _actor.ToString()), new Claim(ClaimTypes.Role, RoleNames.Admin)], "test"))
        };
        return new LowIncomeAdminController(_grants, _users, _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>())
        };
    }

    public LowIncomeAdminControllerTests()
    {
        _grants.IssueAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GrantIssueResult(Guid.NewGuid()));
        _grants.RevokeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GrantRevokeResult(false));
    }

    private static string? Message(Controller c, string key) => c.TempData[key] as string;

    [HumansFact]
    public void EveryAction_IsAdminOnly()
    {
        var type = typeof(LowIncomeAdminController);
        type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>()
            .Select(a => a.Policy).Should().Equal(PolicyNames.AdminOnly);
        foreach (var name in new[] { nameof(LowIncomeAdminController.Grant), nameof(LowIncomeAdminController.Remove) })
            type.GetMethod(name)!.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()
                .Select(a => a.Policy).Should().Equal(PolicyNames.AdminOnly);
    }

    [HumansFact]
    public void AdminSidebar_PointsAtTheIndex_AdminOnly()
    {
        ISectionAdminNav navigation = new Section();
        var item = navigation.Groups().SelectMany(g => g.Items).Single();
        item.Controller.Should().Be("LowIncomeAdmin");
        item.Action.Should().Be(nameof(LowIncomeAdminController.Index));
        item.Policy.Should().Be(PolicyNames.AdminOnly);
    }

    [HumansFact]
    public async Task Grant_IssuesUnderTheLowIncomeKey_AsTheActor()
    {
        var person = Guid.NewGuid();
        var controller = NewController();

        var result = await controller.Grant(person, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(LowIncomeAdminController.Index));
        await _grants.Received(1).IssueAsync(Section.SourceKey, person, null, _actor, Arg.Any<CancellationToken>());
        Message(controller, TempDataKeys.SuccessMessage).Should().NotBeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Remove_PassesTheReason_ReportsWhetherAGrantWasHeld(bool held)
    {
        var person = Guid.NewGuid();
        _grants.RevokeAsync(Section.SourceKey, person, "Moved away", _actor, Arg.Any<CancellationToken>()).Returns(new GrantRevokeResult(held));
        var controller = NewController();

        await controller.Remove(person, "Moved away", CancellationToken.None);

        if (held) Message(controller, TempDataKeys.SuccessMessage).Should().Contain("removed");
        else Message(controller, TempDataKeys.ErrorMessage).Should().Contain("no Low Income grant");
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task NoOpenProgram_IsShownAsAnError_NotThrown(bool revoke)
    {
        _grants.IssueAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GrantIssueResult(Guid.Empty, GrantWriteRefusal.NoOpenProgram));
        _grants.RevokeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GrantRevokeResult(false, GrantWriteRefusal.NoOpenProgram));
        var controller = NewController();

        if (revoke) await controller.Remove(Guid.NewGuid(), null, CancellationToken.None);
        else await controller.Grant(Guid.NewGuid(), CancellationToken.None);

        Message(controller, TempDataKeys.ErrorMessage).Should().Contain("no open Grants program");
    }

    [HumansFact]
    public async Task TooLongReason_IsShownAsAnError_AndLogged()
    {
        var person = Guid.NewGuid();
        _grants.RevokeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GrantRevokeResult(false, GrantWriteRefusal.ReasonTooLong));
        var controller = NewController();

        await controller.Remove(person, new string('x', 501), CancellationToken.None);

        Message(controller, TempDataKeys.ErrorMessage).Should().Contain("too long");
        _logger.Warnings.Should().ContainSingle().Which.Should().BeNull();
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task SourceDependencyFailurePropagatesWithoutRefusalFeedback(bool revoke, bool argumentFailure)
    {
        Exception failure = argumentFailure ? new ArgumentException("Private dependency diagnostic", nameof(argumentFailure))
            : new InvalidOperationException("Private dependency diagnostic");
        _grants.IssueAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);
        _grants.RevokeAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);
        var controller = NewController();
        Func<Task> act = async () =>
        {
            if (revoke) await controller.Remove(Guid.NewGuid(), "Reason", CancellationToken.None);
            else await controller.Grant(Guid.NewGuid(), CancellationToken.None);
        };

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
        _logger.Warnings.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NoPersonPicked_CallsNothing()
    {
        var controller = NewController();

        await controller.Grant(null, CancellationToken.None);

        _grants.ReceivedCalls().Should().BeEmpty();
        Message(controller, TempDataKeys.ErrorMessage).Should().NotBeNull();
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<Exception?> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Warnings.Add(exception);
    }
}
