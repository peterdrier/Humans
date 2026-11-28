using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Users.Tests.Services;

public sealed class ExternalLoginServiceTests : ServiceTestHarness
{
    private const string Provider = "Google";

    private readonly UserManager<User> _userManager;
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ExternalLoginService _service;

    public ExternalLoginServiceTests()
    {
        var userStore = new UserStore<User, IdentityRole<Guid>, UsersDbContext, Guid>(Db);
        _userManager = new UserManager<User>(
            userStore,
            Options.Create(new IdentityOptions
            {
                User = { RequireUniqueEmail = false },
                SignIn = { RequireConfirmedEmail = false }
            }),
            new PasswordHasher<User>(),
            [new UserValidator<User>()],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<User>>.Instance);

        _userEmailService.FindByAddressAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthReconcileResult(
                ReconcileOutcome.NoChange, null, Guid.NewGuid(),
                null, null, null));

        _service = new ExternalLoginService(
            _userManager,
            _userService,
            _userEmailService,
            Clock,
            NullLogger<ExternalLoginService>.Instance);
    }

    public override void Dispose()
    {
        _userManager.Dispose();
        base.Dispose();
    }

    private async Task<User> CreateUserAsync(
        UserState state = UserState.Active,
        Guid? mergedToUserId = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.GetCurrentInstant(),
            State = state,
            MergedToUserId = mergedToUserId
        };
        var result = await _userManager.CreateAsync(user);
        result.Succeeded.Should().BeTrue();
        return user;
    }

    private static ExternalLoginInfo MakeLoginInfo(
        string providerKey,
        string email = "user@example.com",
        bool emailVerified = true,
        string? displayName = "Test Human")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(ClaimTypes.NameIdentifier, providerKey),
        };
        if (displayName is not null)
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        if (emailVerified)
            claims.Add(new Claim("email_verified", "true"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return new ExternalLoginInfo(principal, Provider, providerKey, Provider);
    }

    private void StubVerifiedEmailOwner(string email, User owner)
    {
        _userEmailService.FindByAddressAsync(email, true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(owner.Id, email)]);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CompleteExternalLogin_OtherUsersLogin_ReturnsLinkFailed(
        bool providerSignInSucceeded)
    {
        var userA = await CreateUserAsync();
        var userB = await CreateUserAsync();
        const string providerKey = "sub-b";
        (await _userManager.AddLoginAsync(userB, new UserLoginInfo(Provider, providerKey, Provider)))
            .Succeeded.Should().BeTrue();

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "userb@example.com"),
                "userb@example.com",
                "User B",
                EmailVerified: true,
                ProviderSignInSucceeded: providerSignInSucceeded,
                ProviderSignInLockedOut: false,
                CurrentUserId: userA.Id),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.LinkToCurrentUserFailed);
        result.SignInUserId.Should().BeNull();

        (await _userManager.FindByLoginAsync(Provider, providerKey))!.Id.Should().Be(userB.Id);
        (await _userManager.GetLoginsAsync(userA)).Should().BeEmpty();
        await _userService.DidNotReceiveWithAnyArgs().RecordSignInAsync(default);
        await _userEmailService.DidNotReceiveWithAnyArgs()
            .ReconcileOAuthIdentityAsync(default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task CompleteExternalLogin_OwnExistingLogin_Succeeds()
    {
        var userA = await CreateUserAsync();
        const string providerKey = "sub-a";
        (await _userManager.AddLoginAsync(userA, new UserLoginInfo(Provider, providerKey, Provider)))
            .Succeeded.Should().BeTrue();

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "a@example.com"),
                "a@example.com",
                "User A",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: userA.Id),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().BeNull();

        (await _userManager.GetLoginsAsync(userA))
            .Should().ContainSingle(l => l.LoginProvider == Provider && l.ProviderKey == providerKey);
        await _userService.Received(1).RecordSignInAsync(userA.Id, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(
            userA.Id, null, null, null, Arg.Any<CancellationToken>(), userA.Id);
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userA.Id, Provider, providerKey, "a@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteExternalLogin_AuthenticatedUserLinkingFreshLogin_PersistsLogin()
    {
        var userA = await CreateUserAsync();
        const string providerKey = "sub-fresh";

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "fresh@example.com"),
                "fresh@example.com",
                "User A",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: userA.Id),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().BeNull();

        (await _userManager.FindByLoginAsync(Provider, providerKey))!.Id.Should().Be(userA.Id);
        await _userService.Received(1).RecordSignInAsync(userA.Id, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(
            userA.Id, null, null, null, Arg.Any<CancellationToken>(), userA.Id);
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userA.Id, Provider, providerKey, "fresh@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteExternalLogin_AddressConfirmedElsewhere_ChangesNothing()
    {
        // Two accounts of one person: they merge through the mailed link, never by a login moving the address.
        var userA = await CreateUserAsync();
        var userB = await CreateUserAsync();
        const string providerKey = "sub-new";
        StubVerifiedEmailOwner("b@example.com", userB);

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "b@example.com"),
                "b@example.com",
                "User B",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: userA.Id),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.LinkToCurrentUserFailed);
        (await _userManager.FindByLoginAsync(Provider, providerKey)).Should().BeNull();
        await _userEmailService.DidNotReceiveWithAnyArgs()
            .ReconcileOAuthIdentityAsync(default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task CompleteExternalLogin_SignedInUserMissing_ReturnsLinkFailed()
    {
        var userB = await CreateUserAsync();
        const string providerKey = "sub-b";
        (await _userManager.AddLoginAsync(userB, new UserLoginInfo(Provider, providerKey, Provider)))
            .Succeeded.Should().BeTrue();
        StubVerifiedEmailOwner("b@example.com", userB);

        var missingUserId = Guid.NewGuid();
        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "b@example.com"),
                "b@example.com",
                "User B",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: missingUserId),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.LinkToCurrentUserFailed);
        result.SignInUserId.Should().BeNull();

        Db.Users.Should().ContainSingle(u => u.Id == userB.Id);
        await _userEmailService.DidNotReceiveWithAnyArgs()
            .ReconcileOAuthIdentityAsync(default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task CompleteExternalLogin_UnauthenticatedKnownLogin_ReconcilesExistingOwner()
    {
        var userA = await CreateUserAsync();
        const string providerKey = "sub-a";
        (await _userManager.AddLoginAsync(userA, new UserLoginInfo(Provider, providerKey, Provider)))
            .Succeeded.Should().BeTrue();

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "a@example.com"),
                "a@example.com",
                "User A",
                EmailVerified: true,
                ProviderSignInSucceeded: true,
                ProviderSignInLockedOut: false,
                CurrentUserId: null),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().BeNull();

        await _userService.Received(1).RecordSignInAsync(userA.Id, Arg.Any<CancellationToken>());
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userA.Id, Provider, providerKey, "a@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteExternalLogin_LockedOutRelink_MovesLogin()
    {
        var activeTarget = await CreateUserAsync(UserState.Active);
        var lockedSource = await CreateUserAsync(UserState.Merged, mergedToUserId: activeTarget.Id);
        const string providerKey = "sub-locked";
        (await _userManager.AddLoginAsync(lockedSource, new UserLoginInfo(Provider, providerKey, Provider)))
            .Succeeded.Should().BeTrue();
        StubVerifiedEmailOwner("relink@example.com", activeTarget);

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "relink@example.com"),
                "relink@example.com",
                "Target",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: true,
                CurrentUserId: null),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().Be(activeTarget.Id);

        (await _userManager.FindByLoginAsync(Provider, providerKey))!.Id.Should().Be(activeTarget.Id);
        (await _userManager.GetLoginsAsync(lockedSource)).Should().BeEmpty();
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            activeTarget.Id, Provider, providerKey, "relink@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteExternalLogin_VerifiedEmailMatch_LinksLogin()
    {
        var existingUser = await CreateUserAsync(UserState.Active);
        const string providerKey = "sub-match";
        StubVerifiedEmailOwner("match@example.com", existingUser);

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "match@example.com"),
                "match@example.com",
                "Existing",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: null),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().Be(existingUser.Id);

        (await _userManager.FindByLoginAsync(Provider, providerKey))!.Id.Should().Be(existingUser.Id);
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            existingUser.Id, Provider, providerKey, "match@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteExternalLogin_NewUser_CreatesUser()
    {
        const string providerKey = "sub-new";

        var result = await _service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(
                MakeLoginInfo(providerKey, "new@example.com", displayName: "New Human"),
                "new@example.com",
                "New Human",
                EmailVerified: true,
                ProviderSignInSucceeded: false,
                ProviderSignInLockedOut: false,
                CurrentUserId: null),
            Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        result.SignInUserId.Should().NotBeNull();

        var createdId = result.SignInUserId!.Value;
        (await _userManager.FindByLoginAsync(Provider, providerKey))!.Id.Should().Be(createdId);
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            createdId, Provider, providerKey, "new@example.com", true, Arg.Any<CancellationToken>());
    }
}
