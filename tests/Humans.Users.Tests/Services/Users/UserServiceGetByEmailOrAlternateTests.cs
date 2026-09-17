using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Services.Users;

/// <summary>
/// Covers <see cref="UserService.GetByEmailOrAlternateAsync"/>: resolution through the
/// canonical verified <c>user_emails</c> query (nobodies-collective/Humans#1102), including
/// the gmail/googlemail alternate form and the verified-only gate.
/// </summary>
public sealed class UserServiceGetByEmailOrAlternateTests : ServiceTestHarness
{
    private readonly IUserRepository _repo = Substitute.For<IUserRepository>();
    private readonly UserService _service;

    public UserServiceGetByEmailOrAlternateTests()
    {
        var communicationPreferenceRepository = Substitute.For<ICommunicationPreferenceRepository>();
        communicationPreferenceRepository
            .GetByUserIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([]));

        _service = new UserService(
            _repo,
            communicationPreferenceRepository,
            AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IFileStorage>(),
            Clock,
            NullLogger<UserService>.Instance);
    }

    /// <summary>Stubs the repo reads <c>GetUserInfoAsync</c> needs once a match is found.</summary>
    private void StubUserInfoReads(Guid userId)
    {
        _repo.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = "Test User", PreferredLanguage = "en" });
        _repo.GetUserEmailsByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmail>)[]);
        _repo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<EventParticipation>)[]);
        _repo.GetExternalLoginsByUserIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _repo.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);
    }

    [HumansFact]
    public async Task GetByEmailOrAlternateAsync_VerifiedExactMatch_ResolvesUser()
    {
        var userId = Guid.NewGuid();
        StubUserInfoReads(userId);
        _repo.GetUserEmailsByAddressAsync("alice@example.com", null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmail>)
            [
                new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "alice@example.com", IsVerified = true }
            ]);

        var result = await _service.GetByEmailOrAlternateAsync(
            "alice@example.com", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }

    [HumansFact]
    public async Task GetByEmailOrAlternateAsync_VerifiedGooglemailAlternate_ResolvesUser()
    {
        var userId = Guid.NewGuid();
        StubUserInfoReads(userId);
        // The row is stored on the googlemail.com twin; the query normalizes to gmail.com
        // and computes googlemail.com as the alternate (GetAlternateEmail).
        _repo.GetUserEmailsByAddressAsync("a.b@gmail.com", "a.b@googlemail.com", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmail>)
            [
                new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "a.b@googlemail.com", IsVerified = true }
            ]);

        var result = await _service.GetByEmailOrAlternateAsync(
            "a.b@gmail.com", TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
    }

    [HumansFact]
    public async Task GetByEmailOrAlternateAsync_OnlyUnverifiedRowMatches_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        // Stub the reads GetUserInfoAsync would need, so the ONLY thing standing between
        // this row and a resolved UserInfo is the IsVerified filter. Without this the test
        // would pass even if the filter were deleted.
        StubUserInfoReads(userId);
        _repo.GetUserEmailsByAddressAsync("alice@example.com", null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmail>)
            [
                new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "alice@example.com", IsVerified = false }
            ]);

        var result = await _service.GetByEmailOrAlternateAsync(
            "alice@example.com", TestContext.Current.CancellationToken);

        // Security-relevant: an unverified address must never resolve to an account —
        // this is the only row on file for the address and it is still not enough.
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetByEmailOrAlternateAsync_NoMatchingRow_ReturnsNull()
    {
        _repo.GetUserEmailsByAddressAsync("nobody@example.com", null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserEmail>)[]);

        var result = await _service.GetByEmailOrAlternateAsync(
            "nobody@example.com", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }
}
