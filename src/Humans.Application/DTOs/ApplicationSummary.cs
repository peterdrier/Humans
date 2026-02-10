using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

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
