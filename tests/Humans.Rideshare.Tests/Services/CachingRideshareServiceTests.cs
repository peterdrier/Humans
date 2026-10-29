using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Rideshare.Tests.Services;

/// <summary>
/// <see cref="CachingRideshareService"/> over a substitute keyed inner service: the
/// per-year snapshot is served from cache until any write or erasure clears it.
/// </summary>
public sealed class CachingRideshareServiceTests
{
    private static readonly LocalDate July3 = new(2026, 7, 3);
    private static readonly Guid Event2026 = Guid.NewGuid();
    private static readonly Guid Event2027 = Guid.NewGuid();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IRideshareService _inner = Substitute.For<IRideshareService>();
    private readonly CachingRideshareService _service;

    public CachingRideshareServiceTests()
    {
        _inner.GetSnapshotAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new RideshareSnapshot(call.Arg<Guid?>(), "Event", null, [], [], [])));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IRideshareService>(CachingRideshareService.InnerServiceKey, (_, _) => _inner);
        var provider = services.BuildServiceProvider();

        _service = new CachingRideshareService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingRideshareService>.Instance);
    }

    [HumansFact]
    public async Task GetSnapshot_SecondReadOfTheSameEvent_IsServedFromTheCache()
    {
        var first = await _service.GetSnapshotAsync(Event2026, Ct);
        var second = await _service.GetSnapshotAsync(Event2026, Ct);

        second.Should().BeSameAs(first);
        await _inner.Received(1).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSnapshot_LoadStartedBeforeWrite_DoesNotRepopulateCache(bool returnsId)
    {
        var old = new RideshareSnapshot(Event2026, "Event", null, [], [], []);
        var current = new RideshareSnapshot(Event2026, "Event", null, [], [], []);
        var pending = new TaskCompletionSource<RideshareSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inner.GetSnapshotAsync(Event2026, Ct).Returns(pending.Task, Task.FromResult(current));
        var read = _service.GetSnapshotAsync(Event2026, Ct);

        if (returnsId)
        {
            var save = new RequestSave(RideshareDirection.Inbound, "Lyon", 45.76, 4.84, July3, 1,
                LuggageSize.Minimal, true, null);
            await _service.CreateRequestAsync(Guid.NewGuid(), Event2026, save, Ct);
        }
        else
        {
            await _service.CancelOfferAsync(Guid.NewGuid(), Guid.NewGuid(), Ct);
        }

        pending.SetResult(old);
        (await read).Should().BeSameAs(old);
        (await _service.GetSnapshotAsync(Event2026, Ct)).Should().BeSameAs(current);
        await _inner.Received(2).GetSnapshotAsync(Event2026, Ct);
    }

    [HumansFact]
    public async Task GetSnapshot_CachesEachYearSeparately()
    {
        (await _service.GetSnapshotAsync(Event2026, Ct)).EventSettingsId.Should().Be(Event2026);
        (await _service.GetSnapshotAsync(Event2027, Ct)).EventSettingsId.Should().Be(Event2027);
        await _service.GetSnapshotAsync(Event2026, Ct);

        await _inner.Received(1).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetSnapshotAsync(Event2027, Arg.Any<CancellationToken>());
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
            ("CreateOffer", () => _service.CreateOfferAsync(actor, Event2026, tripSave, Ct)),
            ("UpdateOffer", () => _service.UpdateOfferAsync(id, actor, tripSave, Ct)),
            ("CancelOffer", () => _service.CancelOfferAsync(id, actor, Ct)),
            ("CreateRequest", () => _service.CreateRequestAsync(actor, Event2026, requestSave, Ct)),
            ("UpdateRequest", () => _service.UpdateRequestAsync(id, actor, requestSave, Ct)),
            ("CancelRequest", () => _service.CancelRequestAsync(id, actor, Ct)),
            ("ExpressInterest", () => _service.ExpressInterestAsync(actor, id, null, 1, null, Ct)),
            ("AcceptInterest", () => _service.AcceptInterestAsync(id, actor, Ct)),
            ("DeclineInterest", () => _service.DeclineInterestAsync(id, actor, Ct)),
            ("WithdrawInterest", () => _service.WithdrawInterestAsync(id, actor, Ct)),
            ("SaveSettings", () => _service.SaveSettingsAsync(Event2026, settingsSave, actor, Ct)),
        };

        await _service.GetSnapshotAsync(Event2026, Ct);
        var loads = 1;
        foreach (var (name, write) in writes)
        {
            await _service.GetSnapshotAsync(Event2026, Ct);
            await _inner.Received(loads).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());

            await write();

            await _service.GetSnapshotAsync(Event2026, Ct);
            loads++;
            await _inner.Received(loads).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());
            _ = name; // the tuple name is for the reader; NSubstitute's message carries the count
        }
    }

    [HumansFact]
    public async Task EraseForUser_ForwardsToTheInner_AndClearsTheCache()
    {
        var userId = Guid.NewGuid();
        await _service.GetSnapshotAsync(Event2026, Ct);

        await _service.EraseForUserAsync(userId, Ct);
        await _service.GetSnapshotAsync(Event2026, Ct);

        await _inner.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Reassign_ForwardsToTheInner_AndClearsTheCache()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 3, 1, 12, 0);
        await _service.GetSnapshotAsync(Event2026, Ct);

        await _service.ReassignAsync(source, target, actor, now, Ct);
        await _service.GetSnapshotAsync(Event2026, Ct);

        await _inner.Received(1).ReassignAsync(source, target, actor, now, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetSnapshotAsync(Event2026, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("settings", false)]
    [InlineData("settings", true)]
    [InlineData("offer", false)]
    [InlineData("offer", true)]
    [InlineData("erasure", false)]
    [InlineData("erasure", true)]
    public async Task FailedWrite_EvictsSnapshots_AndPreservesTheFailure(string operation, bool cancelled)
    {
        var actor = Guid.NewGuid();
        Exception failure = cancelled
            ? new OperationCanceledException(Ct)
            : new InvalidOperationException("Post-write dependency failed");
        Func<Task> write;
        switch (operation)
        {
            case "settings":
                var settings = new SettingsSave("Elsewhere", 43.2, -2.4, July3, July3, July3, July3);
                _inner.SaveSettingsAsync(Event2026, settings, actor, Ct).Returns(Task.FromException<RideshareMutationResult>(failure));
                write = () => _service.SaveSettingsAsync(Event2026, settings, actor, Ct);
                break;
            case "offer":
                var offer = new TripSave(RideshareDirection.Inbound, "Paris", 48.85, 2.35, [], July3, 1, null,
                    VehicleType.Car, 3, LuggageSize.Moderate, null, null, false, CostSharing.ShareFuel, null);
                _inner.CreateOfferAsync(actor, Event2026, offer, Ct).Returns(Task.FromException<RideshareMutationResult>(failure));
                write = () => _service.CreateOfferAsync(actor, Event2026, offer, Ct);
                break;
            default:
                _inner.EraseForUserAsync(actor, Ct).Returns(Task.FromException(failure));
                write = () => _service.EraseForUserAsync(actor, Ct);
                break;
        }

        var before = await _service.GetSnapshotAsync(Event2026, Ct);
        var otherYear = await _service.GetSnapshotAsync(Event2027, Ct);
        (await write.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);

        (await _service.GetSnapshotAsync(Event2026, Ct)).Should().NotBeSameAs(before);
        (await _service.GetSnapshotAsync(Event2027, Ct)).Should().NotBeSameAs(otherYear);
        await _inner.Received(2).GetSnapshotAsync(Event2026, Ct);
        await _inner.Received(2).GetSnapshotAsync(Event2027, Ct);
    }

    [HumansFact]
    public async Task ContributeForUser_ForwardsToTheInner()
    {
        var userId = Guid.NewGuid();
        IReadOnlyList<UserDataSlice> slices = [new UserDataSlice(RideshareService.RideshareTrips, new List<object>())];
        _inner.ContributeForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(slices);

        (await _service.ContributeForUserAsync(userId, Ct)).Should().BeSameAs(slices);
    }

    [HumansFact]
    public async Task GetActiveEventId_PassesThrough()
    {
        _inner.GetActiveEventIdAsync(Arg.Any<CancellationToken>()).Returns(Event2027);

        (await _service.GetActiveEventIdAsync(Ct)).Should().Be(Event2027);
    }

    [HumansFact]
    public async Task GetSnapshot_WithNoEvent_IsNotCached()
    {
        await _service.GetSnapshotAsync(null, Ct);
        await _service.GetSnapshotAsync(null, Ct);

        await _inner.Received(2).GetSnapshotAsync(null, Arg.Any<CancellationToken>());
    }
}
