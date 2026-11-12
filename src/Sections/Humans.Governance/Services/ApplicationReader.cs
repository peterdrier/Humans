using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Services;

internal sealed class ApplicationReader(
    IApplicationRepository repository,
    IUserService userService,
    IRoleAssignmentService roleAssignmentService) : IApplicationService
{
    public async Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var applications = await repository.GetByUserIdAsync(userId, ct);
        return applications
            .Select(application => new UserApplicationSnapshot(
                application.Id,
                application.UserId,
                application.Status,
                application.MembershipTier,
                application.SubmittedAt,
                application.ResolvedAt,
                application.TermExpiresAt,
                application.Motivation,
                application.AdditionalInfo,
                application.SignificantContribution,
                application.RoleUnderstanding))
            .ToList();
    }

    public async Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null || application.UserId != userId)
            return null;

        var historyDtos = ProjectHistory(application);

        return new ApplicationUserDetailDto(
            Id: application.Id,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            ResolvedAt: application.ResolvedAt,
            ReviewedByUserId: application.ReviewedByUserId,
            ReviewNotes: application.ReviewNotes,
            History: historyDtos);
    }

    public async Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default)
    {
        ApplicationStatus? status = null;
        if (!string.IsNullOrWhiteSpace(statusFilter)
            && Enum.TryParse<ApplicationStatus>(statusFilter, ignoreCase: true, out var parsedStatus))
        {
            status = parsedStatus;
        }

        MembershipTier? tier = null;
        if (!string.IsNullOrWhiteSpace(tierFilter)
            && Enum.TryParse<MembershipTier>(tierFilter, ignoreCase: true, out var parsedTier))
        {
            tier = parsedTier;
        }

        var (apps, totalCount) = await repository.GetFilteredAsync(status, tier, page, pageSize, ct);
        if (apps.Count == 0)
        {
            return ([], totalCount);
        }

        var userIds = apps.Select(a => a.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var rows = apps.Select(a =>
        {
            var user = users.GetValueOrDefault(a.UserId);
            return new ApplicationAdminRowDto(
                Id: a.Id,
                UserId: a.UserId,
                UserEmail: user?.Email ?? string.Empty,
                Status: a.Status,
                MembershipTier: a.MembershipTier,
                SubmittedAt: a.SubmittedAt,
                Motivation: a.Motivation);
        }).ToList();

        return (rows, totalCount);
    }

    public async Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        var applicant = await userService.GetUserInfoAsync(application.UserId, ct);
        var history = ProjectHistory(application);

        return new ApplicationAdminDetailDto(
            Id: application.Id,
            UserId: application.UserId,
            UserEmail: applicant?.Email ?? string.Empty,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            Language: application.Language,
            SubmittedAt: application.SubmittedAt,
            ReviewedByUserId: application.ReviewedByUserId,
            ReviewNotes: application.ReviewNotes,
            History: history);
    }

    public Task<IReadOnlySet<Guid>> GetUserIdsWithPendingApplicationAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repository.GetUserIdsWithSubmittedAsync(userIds, ct);

    public async Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetSubmittedForUserAsync(userId, ct);
        return application is null
            ? null
            : new SubmittedApplicationSnapshot(
                application.Id,
                application.MembershipTier,
                application.Motivation);
    }

    public async Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default)
    {
        var applications = await repository.GetAllSubmittedWithVotesAsync(ct);

        var rows = applications.Select(a => new BoardVotingDashboardRow(
            ApplicationId: a.Id,
            UserId: a.UserId,
            MembershipTier: a.MembershipTier,
            ApplicationMotivation: a.Motivation,
            SubmittedAt: a.SubmittedAt,
            Status: a.Status,
            Votes: ProjectBoardVotes(a))).ToList();

        var boardMemberIds = await roleAssignmentService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, ct);
        var boardUsersById = await userService.GetUserInfosAsync(boardMemberIds, ct);
        var boardMembers = boardUsersById.Values
            .Select(u => new BoardMemberInfo(u.Id, u.BurnerName))
            .ToList();

        return new BoardVotingDashboardData(rows, boardMembers);
    }

    public async Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        var applicantInfo = await userService.GetUserInfoAsync(application.UserId, ct);
        var profile = applicantInfo?.Profile;

        return new BoardVotingDetailData(
            ApplicationId: application.Id,
            UserId: application.UserId,
            FirstName: applicantInfo?.FirstName ?? string.Empty,
            LastName: applicantInfo?.LastName ?? string.Empty,
            MembershipTier: application.MembershipTier,
            Status: application.Status,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            Votes: ProjectBoardVotes(application));
    }

    public Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default) =>
        repository.GetAdminStatsAsync(ct);

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) =>
        repository.CountByStatusAsync(ApplicationStatus.Submitted, ct);

    private static IReadOnlyList<BoardVoteRow> ProjectBoardVotes(MemberApplication application) =>
        application.BoardVotes
            .Select(vote => new BoardVoteRow(
                BoardMemberUserId: vote.BoardMemberUserId,
                Vote: vote.Vote,
                Note: vote.Note,
                VotedAt: vote.VotedAt))
            .ToList();

    private static IReadOnlyList<ApplicationStateHistoryDto> ProjectHistory(MemberApplication application) =>
        application.StateHistory
            .Select(h => new ApplicationStateHistoryDto(
                Status: h.Status,
                ChangedAt: h.ChangedAt,
                ChangedByUserId: h.ChangedByUserId,
                Notes: h.Notes))
            .ToList();
}
