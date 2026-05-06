using Humans.Application;
using Humans.Domain.Entities;

namespace Humans.Web.Models.EmailProblems;

public sealed class EmailProblemsCompareViewModel
{
    public required string SharedEmail { get; init; }
    public required CompareSide Account1 { get; init; }
    public required CompareSide Account2 { get; init; }
}

public sealed record CompareSide(
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    IReadOnlyList<UserEmailSnapshot> AllUserEmails,
    int TeamCount,
    int RoleAssignmentCount,
    DateTime? LastLogin,
    bool IsProfileComplete);
