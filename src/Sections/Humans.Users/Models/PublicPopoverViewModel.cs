namespace Humans.Users.Models;

/// <summary>
/// The anonymous-viewer popover on public team pages: only data the team page already shows
/// publicly (no location, full team list, languages, tier or suspended badge).
/// </summary>
internal sealed class PublicPopoverViewModel
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> RoleLabels { get; init; } = [];
}
