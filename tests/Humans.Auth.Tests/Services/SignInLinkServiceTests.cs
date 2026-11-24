using System.Globalization;
using Humans.Base.Extensions;
using Humans.Auth.Services;
using Humans.Auth.Tests.Infrastructure;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SignInLinkService = Humans.Auth.Services.SignInLinkService;

namespace Humans.Auth.Tests.Services;

public sealed class SignInLinkServiceTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 25, 12, 0));
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    // AuthEmails is sealed with no interface, so the assertions below read the EmailMessage
    // it built off IEmailService.SendAsync rather than mocking the builder.
    private readonly AuthEmails _emailMessages = TestAuthEmails.Create();
    private readonly ISignInLinkUrlBuilder _urlBuilder;
    private readonly ISignInLinkRateLimiter _rateLimiter;
    private readonly SignInLinkService _service;

    public SignInLinkServiceTests()
    {
        _userEmailService = Substitute.For<IUserEmailService>();
        _userService = Substitute.For<IUserService>();

        // Default: no verified UserEmail row exists. Individual tests override
        // by stubbing the service with a row.
        _userEmailService
            .FindByAddressAsync(Arg.Any<string>(), true, true, Arg.Any<CancellationToken>())
            .Returns([]);

        _emailService = Substitute.For<IEmailService>();
        _urlBuilder = Substitute.For<ISignInLinkUrlBuilder>();
        _urlBuilder.BuildSignInUrl(Arg.Any<Guid>(), Arg.Any<string?>()).Returns(call =>
            $"https://test.example.com/Account/SignInLinkConfirm?userId={call[0]}&token=abc");
        _urlBuilder.BuildSignupUrl(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            $"https://test.example.com/Account/SignInLinkSignup?email={call[0]}&token=abc");

        _rateLimiter = Substitute.For<ISignInLinkRateLimiter>();
        _rateLimiter.TryConsumeTokenAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(true);
        _rateLimiter.ReserveSignupSendAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(0);
        _rateLimiter.ReserveUserSendAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>()).Returns(0);

        _service = new SignInLinkService(
            _userEmailService,
            _userService,
            _emailService,
            _emailMessages,
            _urlBuilder,
            _rateLimiter,
            _clock,
            NullLogger<SignInLinkService>.Instance);
    }

    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task SendSignInLinkAsync_ExistingUserByUserEmail_SendsLoginLink(string recipientCulture)
    {
        using var culture = new CultureScope("en");
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            PreferredLanguage = recipientCulture,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            BurnerName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindByAddressAsync("alice@work.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "alice@work.com")]);

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(user));

        await _service.SendSignInLinkAsync("alice@work.com", "/dashboard", Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.TemplateName == AuthEmails.SignInLinkTemplate
                && m.RecipientEmail == "alice@work.com"
                && m.RecipientName == "Alice"
                && m.Subject == $"Auth_Email_SignInLink_Subject#{recipientCulture}"
                && m.HtmlBody.Contains("/Account/SignInLinkConfirm", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        _urlBuilder.Received(1).BuildSignInUrl(userId, "/dashboard");
        CultureInfo.CurrentUICulture.Name.Should().Be("en");
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_ExistingUserByVerifiedUserEmail_SendsLoginLink()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = "Alice",
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            BurnerName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindByAddressAsync("alice@gmail.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "alice@gmail.com")]);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(user));

        await _service.SendSignInLinkAsync("alice@gmail.com", null, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.TemplateName == AuthEmails.SignInLinkTemplate
                && m.RecipientEmail == "alice@gmail.com"
                && m.RecipientName == "Alice"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_UnknownEmail_SendsSignupLink()
    {
        // Default _userEmailService.FindByAddressAsync returns no rows —
        // no setup needed; the service falls through to signup.
        await _service.SendSignInLinkAsync("newperson@example.com", "/welcome", Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.TemplateName == AuthEmails.SignInLinkSignupTemplate
                && m.RecipientEmail == "newperson@example.com"
                && m.HtmlBody.Contains("/Account/SignInLinkSignup", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private static UserInfo CreateUserInfo(User user) =>
        UserInfo.Create(
            user,
            [],
            [],
            null);

    [HumansFact]
    public async Task SendSignInLinkAsync_RateLimited_DoesNotSendEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindByAddressAsync("alice@gmail.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "alice@gmail.com")]);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _rateLimiter.ReserveUserSendAsync(userId, Arg.Any<TimeSpan>()).Returns(1);

        await _service.SendSignInLinkAsync("alice@gmail.com", null, Xunit.TestContext.Current.CancellationToken);

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == AuthEmails.SignInLinkTemplate),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_SignupRateLimited_DoesNotSendEmail()
    {
        await _service.SendSignInLinkAsync("newperson@example.com", null, Xunit.TestContext.Current.CancellationToken);
        _emailService.ClearReceivedCalls();

        _rateLimiter.ReserveSignupSendAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(1);

        await _service.SendSignInLinkAsync("newperson@example.com", null, Xunit.TestContext.Current.CancellationToken);

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == AuthEmails.SignInLinkSignupTemplate),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_SignupSendThrows_ReleasesTheReservation()
    {
        // The reservation is taken before the send. If a send failure left it
        // standing, the address would be locked out of retrying for the whole
        // cooldown over an error that was never the caller's fault.
        _emailService
            .SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP down"));

        var send = async () => await _service.SendSignInLinkAsync(
            "newperson@example.com", null, Xunit.TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<InvalidOperationException>();
        _rateLimiter.Received(1).ReleaseSignupReservation("newperson@example.com");
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_OverlappingAliasesForTheSameUser_EnqueueOnlyOnce()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SignInLinkService(_userEmailService, _userService, _emailService,
            _emailMessages, _urlBuilder, new SignInLinkRateLimiter(cache), _clock, NullLogger<SignInLinkService>.Instance);
        _userEmailService.FindByAddressAsync("first@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "first@example.com")]);
        _userEmailService.FindByAddressAsync("second@example.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "second@example.com")]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<UserInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return new ValueTask<UserInfo?>(resume.Task);
        });

        var first = service.SendSignInLinkAsync("first@example.com", null, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var second = service.SendSignInLinkAsync("second@example.com", null, ct);
        resume.TrySetResult(null);
        await Task.WhenAll(first, second);

        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _userService.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendLoginLink_SendThrows_ReleasesCooldownReservation()
    {
        // The in-memory reservation is the login cooldown. Keeping it after a failed
        // send would cool the user down for 60s on an email that never left the building.
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindByAddressAsync("alice@gmail.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "alice@gmail.com")]);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _emailService
            .SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP down"));

        var send = async () => await _service.SendSignInLinkAsync(
            "alice@gmail.com", null, Xunit.TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<InvalidOperationException>();
        _rateLimiter.Received(1).ReleaseUserSendReservation(userId);
    }

    [HumansFact]
    public async Task SendLoginLink_Sends_KeepsReservation()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "alice@gmail.com",
            Email = "alice@gmail.com",
            DisplayName = "Alice",
            CreatedAt = _clock.GetCurrentInstant()
        };

        _userEmailService
            .FindByAddressAsync("alice@gmail.com", true, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "alice@gmail.com")]);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));

        await _service.SendSignInLinkAsync("alice@gmail.com", null, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        _rateLimiter.DidNotReceiveWithAnyArgs().ReleaseUserSendReservation(Guid.Empty);
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_UnverifiedEmail_DoesNotMatch()
    {
        // The verified-only FindByAddressAsync skips unverified rows (the default
        // substitute returns none), so the service routes to the signup-link branch.
        await _service.SendSignInLinkAsync("alice@work.com", null, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == AuthEmails.SignInLinkSignupTemplate),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void VerifySignupToken_InvalidToken_ReturnsNull()
    {
        _urlBuilder.UnprotectSignupToken(Arg.Any<string>()).Returns((SignInLinkToken?)null);
        var result = _service.VerifySignupToken("not-a-valid-token");
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_NonexistentUser_ReturnsFalse()
    {
        _userService.GetRawUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _service.VerifySignInTokenAsync(Guid.NewGuid(), "some-token", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_InvalidToken_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _urlBuilder.UnprotectSignInToken("bad-token").Returns((SignInLinkToken?)null);

        var result = await _service.VerifySignInTokenAsync(userId, "bad-token", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_TokenAlreadyConsumed_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _urlBuilder.UnprotectSignInToken("good-token").Returns(Token(userId.ToString()));
        _rateLimiter.TryConsumeTokenAsync("good-token", Arg.Any<TimeSpan>()).Returns(false);

        var result = await _service.VerifySignInTokenAsync(userId, "good-token", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_ExpiredToken_ReturnsFalseWithoutConsumingIt()
    {
        var userId = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(CreateUserInfo(new User { Id = userId, UserName = "test@test.com" }));
        _urlBuilder.UnprotectSignInToken("old-token").Returns(ExpiredToken(userId.ToString()));

        var result = await _service.VerifySignInTokenAsync(userId, "old-token", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        await _rateLimiter.DidNotReceiveWithAnyArgs().TryConsumeTokenAsync(default!, default);
    }

    [HumansFact]
    public void VerifySignupToken_ExpiredToken_ReturnsNull()
    {
        _urlBuilder.UnprotectSignupToken("old-token").Returns(ExpiredToken("new@test.com"));

        _service.VerifySignupToken("old-token").Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_ValidToken_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, UserName = "test@test.com" };
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateUserInfo(user));
        _urlBuilder.UnprotectSignInToken("good-token").Returns(Token(userId.ToString()));

        var result = await _service.VerifySignInTokenAsync(userId, "good-token", Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task VerifyLoginTokenAsync_MergedIdentity_KeepsOriginalTokenBinding(bool survivorPayload)
    {
        var userId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var ct = Xunit.TestContext.Current.CancellationToken;
        _userService.GetRawUserInfoAsync(userId, ct).Returns(CreateUserInfo(
            new User { Id = userId, State = UserState.Merged, MergedToUserId = survivorId }));
        _urlBuilder.UnprotectSignInToken("merged-token")
            .Returns(Token((survivorPayload ? survivorId : userId).ToString()));

        var result = await _service.VerifySignInTokenAsync(userId, "merged-token", ct);

        result.Should().Be(!survivorPayload);
        await _userService.Received(1).GetRawUserInfoAsync(userId, ct);
        if (survivorPayload)
            await _rateLimiter.DidNotReceiveWithAnyArgs().TryConsumeTokenAsync(default!, default);
    }

    [HumansFact]
    public async Task SendSignInLinkAsync_MergedIdentity_BindsLinkToOriginalIdAndResolvesDisplayData()
    {
        var userId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var ct = Xunit.TestContext.Current.CancellationToken;
        _userEmailService.FindByAddressAsync("source@example.com", true, true, ct)
            .Returns([UserEmailFixtures.Row(userId, "source@example.com")]);
        _userService.GetUserInfoAsync(userId, ct).Returns(CreateUserInfo(
            new User { Id = survivorId, BurnerName = "Survivor" }));

        await _service.SendSignInLinkAsync("source@example.com", null, ct);

        _urlBuilder.Received(1).BuildSignInUrl(userId, null);
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.UserId == userId && m.RecipientName == "Survivor"), ct);
    }

    [HumansFact]
    public async Task VerifyLoginTokenAsync_IdentityReadCancellation_StopsBeforeTokenReservation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var ct = cancellation.Token;
        var userId = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(userId, ct)
            .Returns(ValueTask.FromCanceled<UserInfo?>(ct));

        var act = async () => { _ = await _service.VerifySignInTokenAsync(userId, "token", ct); };

        await act.Should().ThrowAsync<OperationCanceledException>();
        _rateLimiter.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task VerifyAndConsumeSignupTokenAsync_ValidUnusedToken_ReturnsEmail()
    {
        _urlBuilder.UnprotectSignupToken("good-token").Returns(Token("new@test.com"));

        var result = await _service.VerifyAndConsumeSignupTokenAsync(
            "good-token", ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().Be("new@test.com");
        await _rateLimiter.Received(1).TryConsumeTokenAsync("good-token", Arg.Any<TimeSpan>());
    }

    [HumansFact]
    public async Task VerifyAndConsumeSignupTokenAsync_TokenAlreadyConsumed_ReturnsNull()
    {
        // A consumed signup link must not redeem again. The payload still decodes —
        // only the reservation stops the second redemption.
        _urlBuilder.UnprotectSignupToken("good-token").Returns(Token("new@test.com"));
        _rateLimiter.TryConsumeTokenAsync("good-token", Arg.Any<TimeSpan>()).Returns(false);

        var result = await _service.VerifyAndConsumeSignupTokenAsync(
            "good-token", ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task VerifyAndConsumeSignupTokenAsync_InvalidToken_NeverReachesTheReservation()
    {
        _urlBuilder.UnprotectSignupToken("bad-token").Returns((SignInLinkToken?)null);

        var result = await _service.VerifyAndConsumeSignupTokenAsync(
            "bad-token", ct: Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await _rateLimiter.DidNotReceive().TryConsumeTokenAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    [HumansFact]
    public void VerifySignupToken_DoesNotConsume_SoScannersCannotBurnTheLink()
    {
        // The GET that renders the signup form decodes only; redemption is the POST's job.
        _urlBuilder.UnprotectSignupToken("good-token").Returns(Token("new@test.com"));

        var result = _service.VerifySignupToken("good-token");

        result.Should().Be("new@test.com");
        _rateLimiter.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Should().NotContain(nameof(ISignInLinkRateLimiter.TryConsumeTokenAsync));
    }

    [HumansFact]
    public void ReleaseSignupToken_HandsTheLinkBackAfterFailedProvisioning()
    {
        // Provisioning rolls itself back on failure, so the redemption accomplished
        // nothing and the person must be able to resubmit the same link.
        _service.ReleaseSignupToken("good-token");

        _rateLimiter.Received(1).ReleaseTokenReservation("good-token");
    }

    private SignInLinkToken Token(string payload) => new(payload, _clock.GetCurrentInstant() + Duration.FromMinutes(15));

    private SignInLinkToken ExpiredToken(string payload) => new(payload, _clock.GetCurrentInstant() - Duration.FromMinutes(1));
}
