using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Onboarding.Services;

internal sealed record ReviewDetailData(
    ReviewProfileDetail? Profile,
    int ConsentCount,
    int RequiredConsentCount,
    string? PendingApplicationMotivation);

internal sealed record ReviewProfileDetail(
    string FirstName,
    string LastName,
    string? City,
    string? CountryCode,
    MembershipTier MembershipTier,
    ConsentCheckStatus? ConsentCheckStatus,
    string? ConsentCheckNotes,
    Instant CreatedAt);
