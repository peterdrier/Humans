using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using NSubstitute;
using NotificationRecipientResolver = Humans.Application.Services.Notifications.NotificationRecipientResolver;

namespace Humans.Application.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationRecipientResolver"/>, the thin
/// adapter that <see cref="NotificationService.SendToRoleAsync"/> routes
/// through to fetch recipient sets without taking a direct dependency on
/// <see cref="IRoleAssignmentService"/> (which would close a DI cycle).
/// </summary>
public class NotificationRecipientResolverTests
{
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly NotificationRecipientResolver _resolver;

    public NotificationRecipientResolverTests()
    {
        _resolver = new NotificationRecipientResolver(_roleAssignmentService);
    }

    [HumansFact]
    public async Task GetActiveUserIdsForRoleAsync_DelegatesToRoleAssignmentService()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([u1, u2]));

        var ids = await _resolver.GetActiveUserIdsForRoleAsync("Board", Xunit.TestContext.Current.CancellationToken);

        ids.Should().BeEquivalentTo([u1, u2]);
        await _roleAssignmentService.Received(1)
            .GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>());
    }
}
