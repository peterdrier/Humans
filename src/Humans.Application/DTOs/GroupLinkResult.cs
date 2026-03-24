namespace Humans.Application.DTOs;

public class GroupLinkResult
{
    public bool Success { get; init; }
    public string? WarningMessage { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RequiresConfirmation { get; init; }
    public Guid? InactiveResourceId { get; init; }

    public static GroupLinkResult Ok() => new() { Success = true };
    public static GroupLinkResult Error(string message) => new() { ErrorMessage = message };
    public static GroupLinkResult NeedsConfirmation(string message, Guid inactiveResourceId) =>
        new() { RequiresConfirmation = true, WarningMessage = message, InactiveResourceId = inactiveResourceId };
}
