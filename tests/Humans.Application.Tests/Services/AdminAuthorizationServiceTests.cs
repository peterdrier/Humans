using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Auth;
using Humans.Domain.Constants;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class AdminAuthorizationServiceTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 11, 12, 0);

    private readonly ICurrentUserContext _currentUser = Substitute.For<ICurrentUserContext>();
    private readonly IRoleAssignmentRepository _roleAssignments = Substitute.For<IRoleAssignmentRepository>();
    private readonly AdminAuthorizationService _service;

    public AdminAuthorizationServiceTests()
    {
        _service = new AdminAuthorizationService(
            _currentUser,
            _roleAssignments,
            new FakeClock(TestNow));
    }

    [HumansFact]
    public async Task RequireCurrentUserIsAdminAsync_NoCurrentUser_Throws()
    {
        _currentUser.UserId.Returns((Guid?)null);

        var act = () => _service.RequireCurrentUserIsAdminAsync();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await _roleAssignments.DidNotReceiveWithAnyArgs()
            .HasActiveRoleAsync(Guid.Empty, null!, default, CancellationToken.None);
    }

    [HumansFact]
    public async Task RequireCurrentUserIsAdminAsync_CurrentUserIsNotAdmin_Throws()
    {
        var currentUserId = Guid.NewGuid();
        _currentUser.UserId.Returns(currentUserId);
        _roleAssignments
            .HasActiveRoleAsync(currentUserId, RoleNames.Admin, TestNow, Arg.Any<CancellationToken>())
            .Returns(false);

        var act = () => _service.RequireCurrentUserIsAdminAsync();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [HumansFact]
    public async Task RequireCurrentUserIsAdminAsync_CurrentUserIsAdmin_Succeeds()
    {
        var currentUserId = Guid.NewGuid();
        _currentUser.UserId.Returns(currentUserId);
        _roleAssignments
            .HasActiveRoleAsync(currentUserId, RoleNames.Admin, TestNow, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.RequireCurrentUserIsAdminAsync();

        await _roleAssignments.Received(1)
            .HasActiveRoleAsync(currentUserId, RoleNames.Admin, TestNow, Arg.Any<CancellationToken>());
    }
}
