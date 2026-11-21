namespace Humans.Base.Authorization;

/// <summary>
/// What a role, task or permission belongs to: an owner kind registered by its section ("Team", "Camp") plus that
/// owner's id. The owner's section builds it; Permissions and Roles only compare it.
/// </summary>
public sealed record Owner(string Kind, Guid Id);
