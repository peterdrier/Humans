using Humans.Users.Contracts;
using Humans.Governance.Contracts;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// One row on the Board Voting dashboard — one application awaiting Board
/// votes. Replaces the old <c>Application</c> entity whose <c>.User</c> nav
/// was hydrated via cross-domain <c>.Include</c>.
/// </summary>
internal sealed record BoardVotingDashboardRow(
    Guid ApplicationId,
    Guid UserId,
    MembershipTier MembershipTier,
    string ApplicationMotivation,
    Instant SubmittedAt,
    ApplicationStatus Status,
    IReadOnlyList<BoardVoteRow> Votes);
