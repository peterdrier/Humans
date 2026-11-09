using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using NodaTime;

namespace Humans.Rideshare.Services;

internal sealed class RideshareInterestMutations(
    IRideshareRepository repository,
    RideshareNotices notices,
    IClock clock,
    ILogger<RideshareService> logger,
    TrackedLock mutationLock) : IApplicationService
{
    internal async Task<RideshareMutationResult> ExpressInterestAsync(
        Guid fromUserId, Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct = default)
    {
        using var mutation = await mutationLock.AcquireAsync(logger, ct);
        var trip = await repository.GetTripAsync(tripId, ct);
        if (trip is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (trip.Status != TripStatus.Active)
            return new(Refusal: new("Rideshare_Error_RideUnavailable"));

        RideshareRequest? request = null;
        if (requestId is { } rid)
        {
            // Driver answering a pin: the seat comes from the driver's own trip.
            request = await repository.GetRequestAsync(rid, ct);
            if (request is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
            if (trip.UserId != fromUserId)
                return new(AccessFailure: RideshareAccessFailure.Forbidden);
            if (request.UserId == fromUserId)
                return new(Refusal: new("Rideshare_Error_OwnRequest"));
            if (CheckAnswerable(trip, request) is { } answerRefusal) return new(Refusal: answerRefusal);
            if (seats == 0)
                seats = request.PartySize;
        }
        else if (trip.UserId == fromUserId)
        {
            return new(Refusal: new("Rideshare_Error_OwnRide"));
        }

        if (seats < 1)
            return new(Refusal: new("Rideshare_Error_SeatsMinimum"));
        if (SeatsRemaining(trip) < seats)
            return new(Refusal: new("Rideshare_Error_NotEnoughSeats"));
        if (trip.Interests.Any(i => i.FromUserId == fromUserId && i.RequestId == requestId && i.Status == InterestStatus.Pending))
            return new(Refusal: new("Rideshare_Error_AlreadyInterested"));

        var cleanedMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (cleanedMessage is { Length: > 1000 })
            return new(Refusal: new("Rideshare_Error_MessageTooLong", 1000));

        var interest = new RideshareInterest
        {
            Id = Guid.NewGuid(),
            FromUserId = fromUserId,
            TripId = trip.Id,
            RequestId = request?.Id,
            Seats = seats,
            Message = cleanedMessage,
            Status = InterestStatus.Pending,
            CreatedAt = clock.GetCurrentInstant(),
        };
        await repository.AddInterestAsync(interest, ct);
        mutation.Dispose();

        await notices.InterestReceivedAsync(interest, trip, request);
        return new(interest.Id);
    }

    internal async Task<RideshareMutationResult> AcceptInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        using var mutation = await mutationLock.AcquireAsync(logger, ct);
        var (interest, accessFailure) = await LoadInterestForOwnerAsync(interestId, actorUserId, ct);
        if (interest is null) return new(AccessFailure: accessFailure);
        if (interest.Status != InterestStatus.Pending)
            return new(Refusal: new("Rideshare_Error_InterestNotPending"));
        if (interest.Trip.Status != TripStatus.Active)
            return new(Refusal: new("Rideshare_Error_RideUnavailable"));
        if (interest.Request is { } request && CheckAnswerable(interest.Trip, request) is { } answerRefusal)
            return new(Refusal: answerRefusal);
        if (SeatsRemaining(interest.Trip) < interest.Seats)
            return new(Refusal: new("Rideshare_Error_NotEnoughSeats"));

        interest.Status = InterestStatus.Accepted;
        interest.RespondedAt = clock.GetCurrentInstant();
        await repository.UpdateInterestAsync(interest, ct);
        mutation.Dispose();

        await notices.InterestAcceptedAsync(interest, actorUserId);
        return new();
    }

    internal async Task<RideshareMutationResult> DeclineInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        using var mutation = await mutationLock.AcquireAsync(logger, ct);
        var (interest, accessFailure) = await LoadInterestForOwnerAsync(interestId, actorUserId, ct);
        if (interest is null) return new(AccessFailure: accessFailure);
        if (interest.Status != InterestStatus.Pending)
            return new(Refusal: new("Rideshare_Error_InterestNotPending"));

        interest.Status = InterestStatus.Declined;
        interest.RespondedAt = clock.GetCurrentInstant();
        await repository.UpdateInterestAsync(interest, ct);
        mutation.Dispose();

        await notices.InterestDeclinedAsync(interest, actorUserId);
        return new();
    }

    internal async Task<RideshareMutationResult> WithdrawInterestAsync(Guid interestId, Guid actorUserId, CancellationToken ct = default)
    {
        using var mutation = await mutationLock.AcquireAsync(logger, ct);
        var interest = await repository.GetInterestAsync(interestId, ct);
        if (interest is null) return new(AccessFailure: RideshareAccessFailure.NotFound);
        if (interest.FromUserId != actorUserId && PostingOwner(interest) != actorUserId)
            return new(AccessFailure: RideshareAccessFailure.Forbidden);
        if (interest.Status is not (InterestStatus.Pending or InterestStatus.Accepted))
            return new(Refusal: new("Rideshare_Error_InterestNotWithdrawable"));

        interest.Status = InterestStatus.Withdrawn;
        await repository.UpdateInterestAsync(interest, ct);
        mutation.Dispose();
        return new();
    }

    /// <summary>
    /// A trip may answer a pin only while the pin is open and the trip still goes that way on that day.
    /// Checked when the driver answers and again when the rider accepts, since either side may have edited in between.
    /// </summary>
    private static RideshareRefusal? CheckAnswerable(RideshareTrip trip, RideshareRequest request)
    {
        if (request.Status != RequestStatus.Active)
            return new("Rideshare_Error_RequestClosed");
        if (trip.EventSettingsId is null || trip.EventSettingsId != request.EventSettingsId)
            return new("Rideshare_Error_RideUnavailable");
        if (trip.Direction != request.Direction || !RideshareTrip.CoversDate(trip.DepartureDate, trip.ExpectedDurationDays, request.DesiredDate))
            return new("Rideshare_Error_RideNotOnRequestDate");
        return null;
    }

    private async Task<(RideshareInterest? Interest, RideshareAccessFailure? Failure)> LoadInterestForOwnerAsync(
        Guid interestId, Guid actorUserId, CancellationToken ct)
    {
        var interest = await repository.GetInterestAsync(interestId, ct);
        if (interest is null) return (null, RideshareAccessFailure.NotFound);
        return PostingOwner(interest) == actorUserId
            ? (interest, null)
            : (null, RideshareAccessFailure.Forbidden);
    }

    /// <summary>Who answers: the rider when the interest answered their request, else the driver.</summary>
    private static Guid PostingOwner(RideshareInterest interest) =>
        interest.Request?.UserId ?? interest.Trip.UserId;

    internal static int AcceptedSeats(RideshareTrip trip) =>
        trip.Interests.Where(i => i.Status == InterestStatus.Accepted).Sum(i => i.Seats);

    internal static int SeatsRemaining(RideshareTrip trip) => trip.SeatsOffered - AcceptedSeats(trip);

}
