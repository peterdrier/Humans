using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class BoardVotingDashboardViewModel
{
    public List<BoardVotingApplicationViewModel> Applications { get; set; } = [];
    public List<BoardVoteMemberViewModel> BoardMembers { get; set; } = [];
}

public class BoardVotingApplicationViewModel
{
    public Guid ApplicationId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public string ApplicationMotivation { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; }
    public ApplicationStatus Status { get; set; }
    public Dictionary<Guid, BoardVoteCellViewModel> VotesByBoardMember { get; set; } = new();
}

public class BoardVoteCellViewModel
{
    public VoteChoice? Vote { get; set; }
    public string? Note { get; set; }
}

public class BoardVoteMemberViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class BoardVotingDetailViewModel
{
    public Guid ApplicationId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public MembershipTier MembershipTier { get; set; }
    public ApplicationStatus Status { get; set; }
    public string ApplicationMotivation { get; set; } = string.Empty;
    public string? AdditionalInfo { get; set; }
    public string? SignificantContribution { get; set; }
    public string? RoleUnderstanding { get; set; }
    public DateTime SubmittedAt { get; set; }
    public List<BoardVoteDetailItemViewModel> Votes { get; set; } = [];
    public VoteChoice? CurrentUserVote { get; set; }
    public string? CurrentUserNote { get; set; }
    public bool CanFinalize { get; set; }
}

public class BoardVoteDetailItemViewModel
{
    public Guid BoardMemberUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public VoteChoice Vote { get; set; }
    public string? Note { get; set; }
    public DateTime VotedAt { get; set; }
}

public class BoardVotingFinalizeModel
{
    public Guid ApplicationId { get; set; }
    public bool Approved { get; set; }
    public string? BoardMeetingDate { get; set; }
    public string? DecisionNote { get; set; }
}
