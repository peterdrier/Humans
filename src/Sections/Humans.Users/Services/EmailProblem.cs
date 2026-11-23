namespace Humans.Users.Services;

internal sealed record EmailProblem(
    EmailProblemKind Kind,
    Guid? UserId,
    Guid? OtherUserId,
    Guid? UserEmailId,
    string? Email);
