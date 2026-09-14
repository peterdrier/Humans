using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// An approved application whose stored <c>TermExpiresAt</c> is not what
/// <c>TermExpiryCalculator</c> gives for its <c>ResolvedAt</c>. Feeds the temporary
/// <c>/Governance/Applications/Admin/TermExpiry</c> correction screen.
/// </summary>
internal sealed record TermExpiryDriftRow(
    Guid ApplicationId,
    Guid UserId,
    MembershipTier MembershipTier,
    LocalDate ResolvedOn,
    LocalDate? StoredExpiry,
    LocalDate ExpectedExpiry);
