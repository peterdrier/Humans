using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Rideshare.Services;

/// <summary>
/// One <see cref="RideshareSnapshot"/> per event; every write clears the whole cache (an event rebuilds in
/// milliseconds). Carries <see cref="IUserDataContributor"/> and <see cref="IUserMerge"/> because erasure and the
/// merge fold change cached rows.
/// </summary>
internal sealed class CachingRideshareService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingRideshareService> logger)
    : TrackedCache<Guid, RideshareSnapshot>("Rideshare.Snapshot", warmOnStartup: false, logger),
      IRideshareService, IUserDataContributor, IUserMerge, IEventSettingsChangeListener
{
    public const string InnerServiceKey = "rideshare-inner";

    public ICacheStats SnapshotCacheStats => this;

    public Task<Guid?> GetActiveEventIdAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetActiveEventIdAsync(ct));

    public async Task<RideshareSnapshot> GetSnapshotAsync(Guid? eventSettingsId, CancellationToken ct = default) =>
        eventSettingsId is { } id
            ? (await GetAsync(id, ct))!
            : await WithInnerAsync(inner => inner.GetSnapshotAsync(null, ct));

    protected override async ValueTask<RideshareSnapshot?> LoadRowAsync(Guid key, CancellationToken ct) =>
        await WithInnerAsync(inner => inner.GetSnapshotAsync(key, ct));

    public Task<RideshareMutationResult> CreateOfferAsync(
        Guid userId, Guid? eventSettingsId, TripSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateOfferAsync(userId, eventSettingsId, save, ct));

    public Task<RideshareMutationResult> UpdateOfferAsync(Guid tripId, Guid actorUserId, TripSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateOfferAsync(tripId, actorUserId, save, ct));

    public Task<RideshareMutationResult> CancelOfferAsync(Guid tripId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CancelOfferAsync(tripId, actorUserId, ct));

    public Task<RideshareMutationResult> CreateRequestAsync(
        Guid userId, Guid? eventSettingsId, RequestSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateRequestAsync(userId, eventSettingsId, save, ct));

    public Task<RideshareMutationResult> UpdateRequestAsync(Guid requestId, Guid actorUserId, RequestSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateRequestAsync(requestId, actorUserId, save, ct));

    public Task<RideshareMutationResult> CancelRequestAsync(Guid requestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CancelRequestAsync(requestId, actorUserId, ct));

    public Task<RideshareMutationResult> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ExpressInterestAsync(fromUserId, tripId, requestId, seats, message, ct));

    public Task<RideshareMutationResult> AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AcceptInterestAsync(interestId, actorUserId, ct));

    public Task<RideshareMutationResult> DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeclineInterestAsync(interestId, actorUserId, ct));

    public Task<RideshareMutationResult> WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.WithdrawInterestAsync(interestId, actorUserId, ct));

    public Task<RideshareMutationResult> SaveSettingsAsync(
        Guid? eventSettingsId, SettingsSave save, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.SaveSettingsAsync(eventSettingsId, save, actorUserId, ct));

    public Task<IReadOnlyList<RideshareEventIdPlanRow>> GetEventIdPlanAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetEventIdPlanAsync(ct));

    public Task<int> AssignEventIdsAsync(CancellationToken ct = default) =>
        MutateAsync(inner => inner.AssignEventIdsAsync(ct));

    // A rename changes the snapshot's name, and a gate-opening change can move a year's legacy rows to another event.
    public void EventSettingsChanged(Guid eventSettingsId) => Clear();

    // Static table: the erasure-coverage architecture test reads it from an
    // uninitialized instance. Every category is erased in full.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RideshareService.RideshareTrips] = null,
            [RideshareService.RideshareRequests] = null,
            [RideshareService.RideshareInterests] = null,
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));

    // A repository write can commit before audit/notification work fails. Always evict
    // the previous snapshot, and let the original failure reach the caller.
    private Task MutateAsync(Func<IRideshareService, Task> work) =>
        MutateAsync(async inner =>
        {
            await work(inner);
            return true;
        });

    private async Task<T> MutateAsync<T>(Func<IRideshareService, Task<T>> work)
    {
        try
        {
            return await WithInnerAsync(work);
        }
        finally
        {
            Clear();
        }
    }

    private async Task<T> WithInnerAsync<T>(Func<IRideshareService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IRideshareService>(InnerServiceKey);
        return await work(inner);
    }
}
