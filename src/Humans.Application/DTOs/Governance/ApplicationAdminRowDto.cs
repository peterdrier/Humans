using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// Row projection for the admin applications list (<c>Applications.cshtml</c>).
/// Replaces the old <c>Application</c> entity whose <c>.User</c> nav was hydrated
/// via cross-domain <c>.Include</c>.
/// </summary>
public record ApplicationAdminRowDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    string UserDisplayName,
    ApplicationStatus Status,
    MembershipTier MembershipTier,
    Instant SubmittedAt,
    string Motivation);
