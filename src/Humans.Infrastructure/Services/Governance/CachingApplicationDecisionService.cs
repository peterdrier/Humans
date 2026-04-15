using NodaTime;
using Humans.Application.DTOs.Governance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services.Governance;

/// <summary>
/// Caching decorator for <see cref="IApplicationDecisionService"/>. Registered
/// via Scrutor <c>.Decorate&lt;&gt;()</c>. Zero business logic — all decisions
/// live in the inner service.
///
/// Responsibilities:
/// <list type="bullet">
/// <item>Write pass-through: all write methods forward to the inner service,
/// which is responsible for updating the store after its repository write
/// returns successfully.</item>
/// <item>Cross-cutting cache invalidation: after a successful
/// <see cref="ApproveAsync"/> or <see cref="RejectAsync"/>, invalidate the
/// NavBadge, NotificationMeter, and per-voter VotingBadge caches.
/// <see cref="SubmitAsync"/> and <see cref="WithdrawAsync"/> invalidate
/// NavBadge and NotificationMeter only.</item>
/// <item>Read pass-through: <b>read methods currently pass through to the
/// inner service</b> rather than short-circuiting via the store. Reason:
/// <c>ProfileService.SaveProfileAsync</c> still creates/edits tier
/// applications directly via <c>_dbContext.Applications</c> during initial
/// profile setup (known incoming violation, tracked in
/// <c>docs/sections/Governance.md</c>). Those writes never hit
/// <see cref="IApplicationStore"/>, so any store-backed read would return
/// stale data until the Profile section migrates too. Once Profile routes
/// its application writes through <see cref="IApplicationDecisionService"/>,
/// <see cref="GetUserApplicationsAsync"/> can be flipped back to serving
/// from <see cref="IApplicationStore"/> directly. The store is still used
/// internally by the inner service for its own state mirroring and is
/// warmed at startup.</item>
/// </list>
///
/// Voter ids for the per-voter VotingBadge invalidation are captured BEFORE
/// calling the inner service's finalize path via
/// <see cref="IApplicationRepository.GetVoterIdsForApplicationAsync"/>, because
/// <see cref="IApplicationRepository.FinalizeAsync"/> deletes the underlying
/// <c>BoardVote</c> rows as part of its atomic commit.
/// </summary>
public sealed class CachingApplicationDecisionService : IApplicationDecisionService
{
    private readonly IApplicationDecisionService _inner;
    private readonly IApplicationRepository _repository;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly INotificationMeterCacheInvalidator _notificationMeter;
    private readonly IVotingBadgeCacheInvalidator _votingBadge;

    public CachingApplicationDecisionService(
        IApplicationDecisionService inner,
        IApplicationRepository repository,
        INavBadgeCacheInvalidator navBadge,
        INotificationMeterCacheInvalidator notificationMeter,
        IVotingBadgeCacheInvalidator votingBadge)
    {
        _inner = inner;
        _repository = repository;
        _navBadge = navBadge;
        _notificationMeter = notificationMeter;
        _votingBadge = votingBadge;
    }

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        // Capture voter ids BEFORE the inner service deletes them inside
        // its atomic finalize path.
        var voterIds = await _repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        var result = await _inner.ApproveAsync(
            applicationId, reviewerUserId, notes, boardMeetingDate, cancellationToken);

        if (result.Success)
        {
            _navBadge.Invalidate();
            _notificationMeter.Invalidate();
            foreach (var id in voterIds)
                _votingBadge.Invalidate(id);
        }

        return result;
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var voterIds = await _repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        var result = await _inner.RejectAsync(
            applicationId, reviewerUserId, reason, boardMeetingDate, cancellationToken);

        if (result.Success)
        {
            _navBadge.Invalidate();
            _notificationMeter.Invalidate();
            foreach (var id in voterIds)
                _votingBadge.Invalidate(id);
        }

        return result;
    }

    public Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // Pass through to the inner service (which reads from the
        // repository) instead of serving from IApplicationStore. See the
        // class remarks above for the reason — ProfileService.SaveProfileAsync
        // still creates/edits tier applications outside this service, so
        // any store-backed read would serve stale data until the Profile
        // section migrates too. Flip this back once that happens.
        return _inner.GetUserApplicationsAsync(userId, ct);
    }

    public Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        // Pass-through: stitched detail DTOs require user lookups that
        // belong in the service. Caching the DTOs would duplicate the
        // user/profile cache for no win.
        return _inner.GetUserApplicationDetailAsync(applicationId, userId, ct);
    }

    public async Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default)
    {
        var result = await _inner.SubmitAsync(
            userId, tier, motivation, additionalInfo, significantContribution, roleUnderstanding,
            language, ct);

        if (result.Success)
        {
            _navBadge.Invalidate();
            _notificationMeter.Invalidate();
        }

        return result;
    }

    public async Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.WithdrawAsync(applicationId, userId, ct);

        if (result.Success)
        {
            _navBadge.Invalidate();
            _notificationMeter.Invalidate();
        }

        return result;
    }

    public Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default)
    {
        return _inner.GetFilteredApplicationsAsync(statusFilter, tierFilter, page, pageSize, ct);
    }

    public Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        return _inner.GetApplicationDetailAsync(applicationId, ct);
    }
}
