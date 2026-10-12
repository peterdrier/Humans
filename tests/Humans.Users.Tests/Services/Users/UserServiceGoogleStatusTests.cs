using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Users.Tests.Services.Users;

public sealed class UserServiceGoogleStatusTests : ServiceTestHarness
{
    private readonly IUserRepository _repo = Substitute.For<IUserRepository>();

    private UserService Service() => new(_repo, Substitute.For<ICommunicationPreferenceRepository>(),
        AdminAuthorization, Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
        Substitute.For<IFileStorage>(), Clock, NullLogger<UserService>.Instance, AuditLog);

    [HumansTheory]
    [Xunit.InlineData(GoogleEmailStatus.Unknown, GoogleEmailStatus.Rejected, true)]
    [Xunit.InlineData(GoogleEmailStatus.Unknown, GoogleEmailStatus.Valid, true)]
    [Xunit.InlineData(GoogleEmailStatus.Rejected, GoogleEmailStatus.Valid, false)]
    public async Task VendorResultAfterSelectionChanges_UpdatesOnlyAttemptedRow(
        GoogleEmailStatus initial, GoogleEmailStatus result, bool changed)
    {
        var userId = Guid.NewGuid();
        var attempted = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "old@example.org",
            IsVerified = true,
            IsGoogle = false,
            GoogleEmailStatus = initial
        };
        var selected = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "new@example.org",
            IsVerified = true,
            IsGoogle = true,
            GoogleEmailStatus = GoogleEmailStatus.Unknown
        };
        _repo.GetUserEmailsByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken)
            .Returns([attempted, selected]);

        var updated = await Service().TrySetGoogleEmailStatusFromSyncAsync(
            userId, result, Xunit.TestContext.Current.CancellationToken, attempted.Id);

        updated.Should().Be(changed);
        attempted.GoogleEmailStatus.Should().Be(changed ? result : initial);
        selected.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Unknown);
        await _repo.Received(changed ? 1 : 0).UpdateUserEmailAsync(attempted, Xunit.TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task MissingAttemptedRow_DoesNotFallBackToTheNewSelection()
    {
        var userId = Guid.NewGuid();
        var selected = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "new@example.org",
            IsVerified = true,
            IsGoogle = true,
            GoogleEmailStatus = GoogleEmailStatus.Unknown
        };
        _repo.GetUserEmailsByUserIdReadOnlyAsync(userId, Xunit.TestContext.Current.CancellationToken).Returns([selected]);

        var updated = await Service().TrySetGoogleEmailStatusFromSyncAsync(
            userId, GoogleEmailStatus.Rejected, Xunit.TestContext.Current.CancellationToken, Guid.NewGuid());

        updated.Should().BeFalse();
        selected.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Unknown);
        await _repo.DidNotReceiveWithAnyArgs().UpdateUserEmailAsync(default!, default);
    }
}
