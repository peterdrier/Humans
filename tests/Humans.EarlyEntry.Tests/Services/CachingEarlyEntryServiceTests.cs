using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.EarlyEntry.Tests.Services;

public class CachingEarlyEntryServiceTests
{
    private static (CachingEarlyEntryService Sut, IEarlyEntryService Inner) CreateSut()
    {
        var inner = Substitute.For<IEarlyEntryService>();
        var services = new ServiceCollection();
        services.AddKeyedScoped<IEarlyEntryService>(
            CachingEarlyEntryService.InnerServiceKey, (_, _) => inner);
        var sp = services.BuildServiceProvider();
        var cache = new CachingEarlyEntryService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingEarlyEntryService>.Instance);
        return (cache, inner);
    }

    [HumansFact]
    public async Task GetForUserAsync_SecondCall_IsACacheHit()
    {
        var (sut, inner) = CreateSut();
        var userId = Guid.NewGuid();
        var entry = new EarlyEntryRosterRow(userId, new LocalDate(2026, 7, 1), ["Camp: Flags"], false);
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<EarlyEntryRosterRow?>(entry));

        var first = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var second = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().Be(second);
        await inner.Received(1).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetForUserAsync_NullResult_IsCached()
    {
        var (sut, inner) = CreateSut();
        var userId = Guid.NewGuid();
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<EarlyEntryRosterRow?>(null));

        var first = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        var second = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        first.Should().BeNull();
        second.Should().BeNull();
        await inner.Received(1).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task GetForUserAsync_LoadStartedBeforeEviction_DoesNotRepopulateCache(
        bool invalidateAll, bool wasGranted)
    {
        var (sut, inner) = CreateSut();
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var grant = new EarlyEntryRosterRow(userId, new LocalDate(2026, 7, 1), ["Camp: Flags"], false);
        var old = wasGranted ? grant : null;
        var current = wasGranted ? null : grant;
        var pending = new TaskCompletionSource<EarlyEntryRosterRow?>(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.GetForUserAsync(userId, ct).Returns(pending.Task, Task.FromResult(current));
        var read = sut.GetForUserAsync(userId, ct);

        if (invalidateAll) sut.InvalidateAll();
        else sut.InvalidateUser(userId);
        pending.SetResult(old);
        (await read).Should().Be(old);

        (await sut.GetForUserAsync(userId, ct)).Should().Be(current);
        await inner.Received(2).GetForUserAsync(userId, ct);
    }

    [HumansFact]
    public async Task EventSettingsChanged_DropsEveryCachedAnswer()
    {
        // The gate date and EarlyEntryStartOffset move every holder's entry date at once,
        // so a Settings-side save flushes the whole cache through the listener seam.
        var (sut, inner) = CreateSut();
        var userId = Guid.NewGuid();
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<EarlyEntryRosterRow?>(null));

        _ = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        ((IEventSettingsChangeListener)sut).EventSettingsChanged(Guid.NewGuid());
        _ = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await inner.Received(2).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateUser_ForcesReload()
    {
        var (sut, inner) = CreateSut();
        var userId = Guid.NewGuid();
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<EarlyEntryRosterRow?>(null));

        _ = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        sut.InvalidateUser(userId);
        _ = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await inner.Received(2).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAll_ForcesReloadForEveryone()
    {
        var (sut, inner) = CreateSut();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        inner.GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<EarlyEntryRosterRow?>(null));

        _ = await sut.GetForUserAsync(alice, Xunit.TestContext.Current.CancellationToken);
        _ = await sut.GetForUserAsync(bob, Xunit.TestContext.Current.CancellationToken);
        sut.InvalidateAll();
        _ = await sut.GetForUserAsync(alice, Xunit.TestContext.Current.CancellationToken);
        _ = await sut.GetForUserAsync(bob, Xunit.TestContext.Current.CancellationToken);

        await inner.Received(2).GetForUserAsync(alice, Arg.Any<CancellationToken>());
        await inner.Received(2).GetForUserAsync(bob, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_AlwaysDelegates()
    {
        var (sut, inner) = CreateSut();
        inner.GetRosterAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<EarlyEntryRosterRow>>([]));

        _ = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);
        _ = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);

        await inner.Received(2).GetRosterAsync(Arg.Any<CancellationToken>());
    }
}
