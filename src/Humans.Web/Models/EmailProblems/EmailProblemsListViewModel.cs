using Humans.Application.DTOs.EmailProblems;
using Humans.Application;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Models.EmailProblems;

public sealed class EmailProblemsListViewModel
{
    public Instant ScannedAt { get; init; }
    public int TotalProblems =>
        CrossUserConflicts.Count + SingleUserIssues.Count + SystemLevelIssues.Count + LegacyEmailRows.Count;

    public IReadOnlyList<CrossUserConflictRow> CrossUserConflicts { get; init; } = [];
    public IReadOnlyList<SingleUserIssueRow> SingleUserIssues { get; init; } = [];
    public IReadOnlyList<SystemLevelIssueRow> SystemLevelIssues { get; init; } = [];
    public IReadOnlyList<LegacyEmailRow> LegacyEmailRows { get; init; } = [];

    public static EmailProblemsListViewModel From(
        EmailProblemsReport report,
        IReadOnlyDictionary<Guid, UserInfo> users)
    {
        string DisplayName(Guid? id) =>
            id is Guid g && users.TryGetValue(g, out var u) ? u.BurnerName : "(unknown)";

        var crossUser = new List<CrossUserConflictRow>();
        var singleUserMap = new Dictionary<Guid, List<string>>();
        var systemLevel = new List<SystemLevelIssueRow>();
        var legacyEmails = new List<LegacyEmailRow>();

        foreach (var p in report.Problems)
        {
            switch (p.Kind)
            {
                case EmailProblemKind.SharedAcrossUsers when p.UserId is Guid u1 && p.OtherUserId is Guid u2:
                    crossUser.Add(new CrossUserConflictRow(p.Email ?? "(unknown)", u1, DisplayName(u1), u2, DisplayName(u2)));
                    break;

                case EmailProblemKind.MultipleIsPrimary or EmailProblemKind.MultipleIsGoogle
                    or EmailProblemKind.ZeroIsPrimary or EmailProblemKind.ZeroIsGoogle
                    or EmailProblemKind.Unverified
                    when p.UserId is Guid u:
                    if (!singleUserMap.TryGetValue(u, out var list))
                    {
                        list = [];
                        singleUserMap[u] = list;
                    }
                    list.Add(p.Kind switch
                    {
                        EmailProblemKind.MultipleIsPrimary => "multiple IsPrimary",
                        EmailProblemKind.MultipleIsGoogle => "multiple IsGoogle",
                        EmailProblemKind.ZeroIsPrimary => "zero IsPrimary",
                        EmailProblemKind.ZeroIsGoogle => "zero IsGoogle",
                        EmailProblemKind.Unverified => $"unverified: {p.Email}",
                        _ => p.Kind.ToString()
                    });
                    break;

                case EmailProblemKind.OrphanUserEmail:
                    systemLevel.Add(new SystemLevelIssueRow(
                        p.Kind, p.UserEmailId, p.UserId,
                        $"Orphan UserEmail \"{p.Email}\" (was userId {p.UserId})"));
                    break;

                case EmailProblemKind.GhostExternalLogins:
                    systemLevel.Add(new SystemLevelIssueRow(
                        p.Kind, null, p.UserId,
                        $"Ghost AspNetUserLogins for userId {p.UserId}"));
                    break;

                case EmailProblemKind.LegacyIdentityEmailNotInUserEmails when p.UserId is Guid uid:
                    legacyEmails.Add(new LegacyEmailRow(uid, DisplayName(uid), p.Email ?? "(unknown)"));
                    break;
            }
        }

        var singleUser = singleUserMap
            .Select(kvp => new SingleUserIssueRow(kvp.Key, DisplayName(kvp.Key), kvp.Value))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EmailProblemsListViewModel
        {
            ScannedAt = report.ScannedAt,
            CrossUserConflicts = crossUser,
            SingleUserIssues = singleUser,
            SystemLevelIssues = systemLevel,
            LegacyEmailRows = legacyEmails
                .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }
}

public sealed record CrossUserConflictRow(
    string Email,
    Guid User1Id, string User1DisplayName,
    Guid User2Id, string User2DisplayName);

public sealed record SingleUserIssueRow(
    Guid UserId, string DisplayName,
    IReadOnlyList<string> ProblemSummaries);

public sealed record SystemLevelIssueRow(
    EmailProblemKind Kind,
    Guid? UserEmailId,
    Guid? UserId,
    string Detail);

public sealed record LegacyEmailRow(
    Guid UserId,
    string DisplayName,
    string LegacyEmail);
