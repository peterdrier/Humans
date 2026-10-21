using Humans.Consent.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// An <see cref="IConsentService"/> double whose Consent Coordinator review reads come from a
/// dictionary (a member without an entry reads <see cref="ConsentReviewInfo.None"/>). Needed
/// because NSubstitute's auto-value for a <c>Task</c> of a sealed record is null.
/// </summary>
internal static class ConsentReviewStubs
{
    public static IConsentService Create(IDictionary<Guid, ConsentReviewInfo>? reviews = null)
    {
        var service = Substitute.For<IConsentService>();
        service.StubReviews(reviews ?? new Dictionary<Guid, ConsentReviewInfo>());
        return service;
    }

    public static void StubReviews(this IConsentService service, IDictionary<Guid, ConsentReviewInfo> reviews)
    {
        service.GetConsentReviewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => reviews.TryGetValue(call.Arg<Guid>(), out var review)
                ? review
                : ConsentReviewInfo.None(call.Arg<Guid>()));
        service.GetConsentReviewsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, ConsentReviewInfo>)reviews.ToDictionary());
    }

    public static ConsentReviewInfo Approved(Guid userId) =>
        ConsentReviewInfo.None(userId) with { Status = ConsentCheckStatus.Cleared, IsApproved = true };

    public static ConsentReviewInfo Rejected(Guid userId, string? reason = null) =>
        ConsentReviewInfo.None(userId) with { RejectionReason = reason, RejectedAt = Instant.FromUtc(2026, 2, 1, 0, 0) };
}
