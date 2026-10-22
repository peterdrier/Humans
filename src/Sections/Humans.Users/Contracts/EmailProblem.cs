namespace Humans.Users.Contracts;

public sealed record EmailProblem(
    EmailProblemKind Kind,
    Guid? UserId,
    Guid? OtherUserId,
    Guid? UserEmailId,
    string? Email,
    string? Detail);
