namespace Humans.Application.DTOs.EmailProblems;

/// <summary>
/// One detected EmailProblem entry. Some kinds are scoped to a single user,
/// some to a pair (case 5), some to a single email row (case 7), some to a
/// single user with no rows (case 8).
/// </summary>
public sealed record EmailProblem(
    EmailProblemKind Kind,
    Guid? UserId,
    Guid? OtherUserId,
    Guid? UserEmailId,
    string? Email,
    string? Detail);
