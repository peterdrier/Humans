using Profiles.Domain.Entities;

namespace Profiles.Application.DTOs;

/// <summary>
/// Result of attempting to link a Google resource to a team.
/// </summary>
public record LinkResourceResult(
    bool Success,
    GoogleResource? Resource = null,
    string? ErrorMessage = null,
    string? ServiceAccountEmail = null);
