using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs.Governance;

/// <summary>
/// One row on the Board Voting dashboard — one application awaiting Board
/// votes. Replaces the old <c>Application</c> entity whose <c>.User</c> nav
/// was hydrated via cross-domain <c>.Include</c>.
/// </summary>
public record BoardVotingDashboardRow(
    Guid ApplicationId,
    Guid UserId,
    string UserDisplayName,
    string? UserProfilePictureUrl,
    MembershipTier MembershipTier,
    string ApplicationMotivation,
    Instant SubmittedAt,
    ApplicationStatus Status,
    IReadOnlyList<BoardVoteRow> Votes);
