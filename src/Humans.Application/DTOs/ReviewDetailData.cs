using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

public record ReviewDetailData(
    ReviewProfileDetail? Profile,
    int ConsentCount,
    int RequiredConsentCount,
    string? PendingApplicationMotivation);

public record ReviewProfileDetail(
    string FirstName,
    string LastName,
    string? City,
    string? CountryCode,
    MembershipTier MembershipTier,
    ConsentCheckStatus? ConsentCheckStatus,
    string? ConsentCheckNotes,
    Instant CreatedAt);
