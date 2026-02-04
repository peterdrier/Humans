using NodaTime;
using Profiles.Domain.Enums;

namespace Profiles.Application.DTOs;

/// <summary>
/// Summary of an application for listing purposes.
/// </summary>
public record ApplicationSummary(
    Guid Id,
    Guid UserId,
    string ApplicantName,
    string ApplicantEmail,
    ApplicationStatus Status,
    Instant SubmittedAt,
    Instant? ResolvedAt);
