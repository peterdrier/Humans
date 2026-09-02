using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Rideshare.Tests.Services;

/// <summary>
/// <see cref="CachingRideshareService"/> over a substitute keyed inner service: the
/// per-year snapshot is served from cache until any write or erasure clears it.
/// </summary>
public sealed class CachingRideshareServiceTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IRideshareService _inner = Substitute.For<IRideshareService>();
    private readonly CachingRideshareService _service;

    public CachingRideshareServiceTests()
    {
        _inner.GetSnapshotAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new RideshareSnapshot(call.Arg<int>(), null, [], [], [])));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IRideshareService>(CachingRideshareService.InnerServiceKey, (_, _) => _inner);
        var provider = services.BuildServiceProvider();

        _service = new CachingRideshareService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingRideshareService>.Instance);
    }

    [HumansFact]
    public async Task GetSnapshot_SecondReadOfTheSameYear_IsServedFromTheCache()
    {
        var first = await _service.GetSnapshotAsync(2026, Ct);
        var second = await _service.GetSnapshotAsync(2026, Ct);

        second.Should().BeSameAs(first);
        await _inner.Received(1).GetSnapshotAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetSnapshot_CachesEachYearSeparately()
    {
        (await _service.GetSnapshotAsync(2026, Ct)).Year.Should().Be(2026);
        (await _service.GetSnapshotAsync(2027, Ct)).Year.Should().Be(2027);
        await _service.GetSnapshotAsync(2026, Ct);

        await _inner.Received(1).GetSnapshotAsync(2026, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetSnapshotAsync(2027, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EveryWrite_ClearsTheCache()
    {
        var id = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var tripSave = new TripSave(RideshareDirection.Inbound, "Paris", 48.85, 2.35, [], July3, 1, null,
            VehicleType.Car, 3, LuggageSize.Moderate, null, null, false, CostSharing.ShareFuel, null);
        var requestSave = new RequestSave(RideshareDirection.Inbound, "Lyon", 45.76, 4.84, July3, 1,
            LuggageSize.Minimal, true, null);
        var settingsSave = new SettingsSave("Elsewhere", 43.2, -2.4, July3, July3, July3, July3);

        var writes = new (string Name, Func<Task> Write)[]
        {
            ("CreateOffer", () => _service.CreateOfferAsync(actor, 2026, tripSave, Ct)),
            ("UpdateOffer", () => _service.UpdateOfferAsync(id, actor, tripSave, Ct)),
            ("CancelOffer", () => _service.CancelOfferAsync(id, actor, Ct)),
            ("CreateRequest", () => _service.CreateRequestAsync(actor, 2026, requestSave, Ct)),
            ("UpdateRequest", () => _service.UpdateRequestAsync(id, actor, requestSave, Ct)),
            ("CancelRequest", () => _service.CancelRequestAsync(id, actor, Ct)),
            ("ExpressInterest", () => _service.ExpressInterestAsync(actor, id, null, 1, null, Ct)),
            ("AcceptInterest", () => _service.AcceptInterestAsync(id, actor, Ct)),
            ("DeclineInterest", () => _service.DeclineInterestAsync(id, actor, Ct)),
            ("WithdrawInterest", () => _service.WithdrawInterestAsync(id, actor, Ct)),
            ("SaveSettings", () => _service.SaveSettingsAsync(2026, settingsSave, actor, Ct)),
        };

        await _service.GetSnapshotAsync(2026, Ct);
        var loads = 1;
        foreach (var (name, write) in writes)
        {
            await _service.GetSnapshotAsync(2026, Ct);
            await _inner.Received(loads).GetSnapshotAsync(2026, Arg.Any<CancellationToken>());

            await write();

            await _service.GetSnapshotAsync(2026, Ct);
            loads++;
            await _inner.Received(loads).GetSnapshotAsync(2026, Arg.Any<CancellationToken>());
            _ = name; // the tuple name is for the reader; NSubstitute's message carries the count
        }
    }

    [HumansFact]
    public async Task EraseForUser_ForwardsToTheInner_AndClearsTheCache()
    {
        var userId = Guid.NewGuid();
        await _service.GetSnapshotAsync(2026, Ct);

        await _service.EraseForUserAsync(userId, Ct);
        await _service.GetSnapshotAsync(2026, Ct);

        await _inner.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetSnapshotAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ContributeForUser_ForwardsToTheInner()
    {
        var userId = Guid.NewGuid();
        IReadOnlyList<UserDataSlice> slices = [new UserDataSlice(GdprExportSections.RideshareTrips, new List<object>())];
        _inner.ContributeForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(slices);

        (await _service.ContributeForUserAsync(userId, Ct)).Should().BeSameAs(slices);
    }

    [HumansFact]
    public async Task GetActiveYear_PassesThrough()
    {
        _inner.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(2031);

        (await _service.GetActiveYearAsync(Ct)).Should().Be(2031);
    }
}
