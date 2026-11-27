using System.Security.Claims;
using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Humans.Notifications.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Notifications.Tests.Services;

public class NotificationMeterProviderTests
{
    private readonly INotificationMeterContributor _meterContributor = Substitute.For<INotificationMeterContributor>();
    private readonly List<INotificationMeterContributor> _contributors;
    private readonly NotificationMeterProvider _provider;

    public NotificationMeterProviderTests()
    {
        _meterContributor.GetMetersAsync(default!, default).ReturnsForAnyArgs([]);
        _contributors = [_meterContributor];
        _provider = new NotificationMeterProvider(_contributors, NullLogger<NotificationMeterProvider>.Instance);
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_MergesEveryContributor()
    {
        var viewerId = Guid.NewGuid();
        var principal = CreatePrincipalWithId(viewerId);
        _meterContributor.GetMetersAsync(principal, viewerId, Arg.Any<CancellationToken>())
            .Returns([Meter("Low", 3), Meter("Top", 10)]);
        var other = Substitute.For<INotificationMeterContributor>();
        other.GetMetersAsync(principal, viewerId, Arg.Any<CancellationToken>()).Returns([Meter("Middle", 7)]);
        _contributors.Add(other);

        var meters = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken);

        meters.Select(m => m.Title).Should().BeEquivalentTo("Top", "Middle", "Low");
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_AsksContributorsForTheResolvedAccountOverTheClaim()
    {
        var survivorId = Guid.NewGuid();
        var principal = CreatePrincipalWithId(Guid.NewGuid());
        _meterContributor.GetMetersAsync(principal, survivorId, Arg.Any<CancellationToken>()).Returns([Meter("Survivor", 4)]);

        var meters = await _provider.GetMetersForUserAsync(principal, TestContext.Current.CancellationToken, survivorId);

        meters.Should().ContainSingle(m => m.Title == "Survivor");
    }

    [HumansFact]
    public async Task GetMetersForUserAsync_WithoutAViewerId_AsksNoContributor()
    {
        var meters = await _provider.GetMetersForUserAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), TestContext.Current.CancellationToken);

        meters.Should().BeEmpty();
        await _meterContributor.DidNotReceiveWithAnyArgs().GetMetersAsync(default!, default);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMetersForUserAsync_IsolatesContributorFailureButPropagatesCallerCancellation(bool callerCancelled)
    {
        var viewerId = Guid.NewGuid();
        var principal = CreatePrincipalWithId(viewerId);
        using var cts = new CancellationTokenSource();
        _meterContributor.GetMetersAsync(principal, viewerId, cts.Token)
            .Returns<Task<IReadOnlyList<NotificationMeter>>>(async _ =>
            {
                if (callerCancelled)
                {
                    await cts.CancelAsync();
                    throw new OperationCanceledException(cts.Token);
                }
                throw new InvalidOperationException("contributor unavailable");
            });
        var healthy = Substitute.For<INotificationMeterContributor>();
        healthy.GetMetersAsync(principal, viewerId, cts.Token).Returns([Meter("Healthy section", 4)]);
        _contributors.Add(healthy);

        var read = () => _provider.GetMetersForUserAsync(principal, cts.Token);
        if (callerCancelled)
        {
            await read.Should().ThrowAsync<OperationCanceledException>();
            await healthy.DidNotReceiveWithAnyArgs().GetMetersAsync(default!, default);
        }
        else
        {
            var meters = await read();
            meters.Should().ContainSingle(m => m.Title == "Healthy section");
        }
    }

    private static NotificationMeter Meter(string title, int priority) =>
        new() { Title = title, Count = 1, ActionUrl = "/", Audience = "viewer", Priority = priority };

    private static ClaimsPrincipal CreatePrincipalWithId(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], authenticationType: "Test"));
}
