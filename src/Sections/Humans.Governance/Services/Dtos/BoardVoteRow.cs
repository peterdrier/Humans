using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// Board vote projection; the views render the voter through the Human component.
/// </summary>
internal sealed record BoardVoteRow(
    Guid BoardMemberUserId,
    VoteChoice Vote,
    string? Note,
    Instant VotedAt);
