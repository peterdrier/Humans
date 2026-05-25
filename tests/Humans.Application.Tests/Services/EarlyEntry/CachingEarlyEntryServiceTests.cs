using AwesomeAssertions;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Infrastructure.Services.EarlyEntry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.EarlyEntry;

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
        var entry = new UserEarlyEntry(new LocalDate(2026, 7, 1), ["Camp: Flags"]);
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<UserEarlyEntry?>(entry));

        var first = await sut.GetForUserAsync(userId, CancellationToken.None);
        var second = await sut.GetForUserAsync(userId, CancellationToken.None);

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
             .Returns(Task.FromResult<UserEarlyEntry?>(null));

        var first = await sut.GetForUserAsync(userId, CancellationToken.None);
        var second = await sut.GetForUserAsync(userId, CancellationToken.None);

        first.Should().BeNull();
        second.Should().BeNull();
        await inner.Received(1).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateUser_ForcesReload()
    {
        var (sut, inner) = CreateSut();
        var userId = Guid.NewGuid();
        inner.GetForUserAsync(userId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<UserEarlyEntry?>(null));

        _ = await sut.GetForUserAsync(userId, CancellationToken.None);
        sut.InvalidateUser(userId);
        _ = await sut.GetForUserAsync(userId, CancellationToken.None);

        await inner.Received(2).GetForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetRosterAsync_AlwaysDelegates()
    {
        var (sut, inner) = CreateSut();
        inner.GetRosterAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<EarlyEntryRosterRow>>([]));

        _ = await sut.GetRosterAsync(CancellationToken.None);
        _ = await sut.GetRosterAsync(CancellationToken.None);

        await inner.Received(2).GetRosterAsync(Arg.Any<CancellationToken>());
    }
}
