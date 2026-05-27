using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

/// <summary>
/// Pins the account-merge-fold cache-invalidation invariant on
/// <see cref="TicketSyncService.ReassignAsync"/> (T-07 spec, step A).
///
/// <para>
/// When the orchestrator (<c>AccountMergeService.AcceptAsync</c>) fans out
/// to every <c>IUserMerge</c>, the Tickets section re-FKs
/// <c>TicketOrder.MatchedUserId</c>, <c>TicketAttendee.MatchedUserId</c>,
/// and the two <c>TicketTransferRequest</c> user columns from source to
/// target. The eviction contract is: drop the global projection AND both
/// users' per-user TTL entries — without the latter, the homepage card and
/// ticket-holdings widget lag up to 5 minutes after a merge.
/// </para>
///
/// <para>
/// This test pins that <see cref="TicketSyncService.ReassignAsync"/> calls
/// <see cref="ITicketCacheInvalidator.InvalidateAfterUserMerge"/> with both
/// user ids. The decorator's implementation of that seam (drop projection,
/// remove both users' per-user cache entries) is pinned by
/// <c>CachingTicketQueryService</c>'s own unit tests.
/// </para>
/// </summary>
public sealed class TicketSyncService_ReassignCacheTests
{
    [HumansFact]
    public async Task ReassignAsync_CallsInvalidateAfterUserMerge_WithBothUserIds()
    {
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var invalidator = Substitute.For<ITicketCacheInvalidator>();

        var service = new TicketSyncService(
            Substitute.For<ITicketRepository>(),
            Substitute.For<ITicketTransferRepository>(),
            Substitute.For<ITicketVendorService>(),
            Substitute.For<IStripeService>(),
            new FakeClock(Instant.FromUtc(2026, 5, 16, 12, 0)),
            Options.Create(new TicketVendorSettings { EventId = "ev_t07", ApiKey = "k", SyncIntervalMinutes = 15 }),
            NullLogger<TicketSyncService>.Instance,
            invalidator,
            Substitute.For<IUserServiceRead>(),
            Substitute.For<IUserService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IShiftManagementService>());

        await service.ReassignAsync(
            sourceUserId,
            targetUserId,
            actorUserId: Guid.NewGuid(),
            updatedAt: Instant.FromUtc(2026, 5, 16, 12, 0),
            CancellationToken.None);

        invalidator.Received(1).InvalidateAfterUserMerge(sourceUserId, targetUserId);
    }
}
