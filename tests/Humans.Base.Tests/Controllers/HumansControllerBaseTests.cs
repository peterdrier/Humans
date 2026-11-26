using System.Security.Claims;
using Humans.Base.Enums;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace Humans.Base.Tests.Controllers;

public sealed class HumansControllerBaseTests
{
    [HumansFact]
    public void SetError_without_service_provider_sets_the_toast()
    {
        var controller = new TestController(Substitute.For<IUserService>());
        var http = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());

        controller.Error("Failed.");

        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("Failed.");
    }

    public enum CurrentUserGuard { MvcNotFound, MvcChallenge, MvcUnauthorized, ApiUnauthorized }
    public enum CurrentAccount { Missing, Deleted, LegacyTombstone, GdprErased, UnresolvedMerge, LiveAlias }

    [HumansTheory]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.Missing)]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.Deleted)]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.LegacyTombstone)]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.GdprErased)]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.UnresolvedMerge)]
    [Xunit.InlineData(CurrentUserGuard.MvcNotFound, CurrentAccount.LiveAlias)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.Missing)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.Deleted)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.LegacyTombstone)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.GdprErased)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.UnresolvedMerge)]
    [Xunit.InlineData(CurrentUserGuard.MvcChallenge, CurrentAccount.LiveAlias)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.Missing)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.Deleted)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.LegacyTombstone)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.GdprErased)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.UnresolvedMerge)]
    [Xunit.InlineData(CurrentUserGuard.MvcUnauthorized, CurrentAccount.LiveAlias)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.Missing)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.Deleted)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.LegacyTombstone)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.GdprErased)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.UnresolvedMerge)]
    [Xunit.InlineData(CurrentUserGuard.ApiUnauthorized, CurrentAccount.LiveAlias)]
    public async Task ResolveCurrentUser_UnavailableAccount_ReturnsExistingRefusal(CurrentUserGuard guard, CurrentAccount account)
    {
        var requestedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        UserInfo? person = account == CurrentAccount.Missing ? null
            : UserInfo.Create(new User { Id = account == CurrentAccount.LiveAlias ? survivorId : requestedId, Email = "member@example.org" }, [], [], null);
        person = account switch
        {
            CurrentAccount.Deleted => person! with { State = UserState.Deleted },
            CurrentAccount.LegacyTombstone => person! with { IdentityEmailColumn = "erased@deleted.local" },
            CurrentAccount.GdprErased => person! with { IsGdprAnonymized = true },
            CurrentAccount.UnresolvedMerge => person! with { MergedToUserId = Guid.NewGuid() },
            _ => person
        };
        users.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(person));
        ControllerBase controller = guard == CurrentUserGuard.ApiUnauthorized ? new ApiTestController(users) : new TestController(users);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, requestedId.ToString())], "Test"))
            }
        };
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);

        var (error, resolved) = controller is TestController mvc
            ? await mvc.ResolveAsync(guard, request.Token)
            : await ((ApiTestController)controller).ResolveAsync(request.Token);

        if (account == CurrentAccount.LiveAlias)
        {
            error.Should().BeNull();
            resolved.Should().BeSameAs(person);
            resolved.Id.Should().Be(survivorId);
        }
        else
        {
            switch (guard)
            {
                case CurrentUserGuard.MvcNotFound: error.Should().BeOfType<NotFoundResult>(); break;
                case CurrentUserGuard.MvcChallenge: error.Should().BeOfType<ChallengeResult>(); break;
                default: error.Should().BeOfType<UnauthorizedResult>(); break;
            }
            resolved.Should().BeNull();
        }
        await users.Received(1).GetUserInfoAsync(requestedId, request.Token);
    }

    private sealed class ApiTestController(IUserService users) : ApiControllerBase(users)
    {
        public Task<(IActionResult? ErrorResult, UserInfo User)> ResolveAsync(CancellationToken ct) =>
            ResolveCurrentUserOrUnauthorizedAsync(ct);
    }

    private sealed class TestController(IUserService users) : HumansControllerBase(users)
    {
        public void Error(string message) => SetError(message);

        public Task<(IActionResult? ErrorResult, UserInfo User)> ResolveAsync(CurrentUserGuard guard, CancellationToken ct) => guard switch
        {
            CurrentUserGuard.MvcNotFound => RequireCurrentUserAsync(ct),
            CurrentUserGuard.MvcChallenge => ResolveCurrentUserOrChallengeAsync(ct),
            _ => ResolveCurrentUserOrUnauthorizedAsync(ct)
        };
    }
}
