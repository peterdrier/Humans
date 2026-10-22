namespace Humans.Auth.Contracts;

/// <summary><c>ErrorKey</c> names the failure for the caller to map to its own message.</summary>
public record RoleAssignmentResult(bool Success, string? ErrorKey = null);
